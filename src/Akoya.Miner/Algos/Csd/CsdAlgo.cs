// CSD (Compute Substrate) algorithm module — pool mining over canonical Bitcoin
// Stratum V1 with a sha256d GPU search kernel (csd_capi.dll). Self-contained per
// the --algo plugin rules: config comes from ARC_CSD_* / shared pool env only.

using System.Linq;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Csd;

internal sealed class CsdAlgo : IMiningAlgo
{
    public string Name => "csd";

    private static readonly long[] DefaultHeartbeats = { 0L };

    private sealed record CsdConfig(string Host, int Port, string Address, string Worker, int? GpuIndex, bool UseTls);

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static CsdConfig? LoadConfig(ILogger log)
    {
        var address = Env("ARC_CSD_ADDRESS");
        if (address is null)
        {
            var pw = Env("ARC_POOL_WALLET");
            if (pw is not null && pw != "unused-non-prl-algo") address = pw;
        }
        if (address is null) { log.LogError("csd: ARC_CSD_ADDRESS (or --wallet) is required"); return null; }

        // TLS default follows the shared --tls/--no-tls flag (ARC_POOL_TLS);
        // a stratum+ssl:// / stratum+tls:// scheme on --pool sets it too.
        bool? useTls = Env("ARC_POOL_TLS") is { } t ? t.Equals("true", StringComparison.OrdinalIgnoreCase) : null;

        var poolUrl = Env("ARC_CSD_POOL");
        string? host; int port;
        if (poolUrl is not null)
        {
            // Accept an optional scheme; ssl/tls implies TLS on.
            if (poolUrl.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase) ||
                poolUrl.StartsWith("stratum+tls://", StringComparison.OrdinalIgnoreCase) ||
                poolUrl.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase)) useTls = true;
            else if (poolUrl.StartsWith("stratum+tcp://", StringComparison.OrdinalIgnoreCase) ||
                     poolUrl.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) useTls ??= false;
            var hp = poolUrl.Contains("://") ? poolUrl[(poolUrl.IndexOf("://", StringComparison.Ordinal) + 3)..] : poolUrl;
            var colon = hp.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(hp[(colon + 1)..], out port)) { log.LogError("csd: ARC_CSD_POOL must be [scheme://]host:port"); return null; }
            host = hp[..colon];
        }
        else
        {
            host = Env("ARC_POOL_HOST");
            var portStr = Env("ARC_POOL_PORT");
            if (host is null || portStr is null || !int.TryParse(portStr, out port))
            {
                log.LogError("csd: pool required — set --pool host:port (or ARC_CSD_POOL / ARC_POOL_HOST+PORT)");
                return null;
            }
        }

        return new CsdConfig(
            Host: host,
            Port: port,
            Address: address,
            Worker: Env("ARC_CSD_WORKER") ?? Env("ARC_POOL_WORKER") ?? Environment.MachineName,
            GpuIndex: int.TryParse(Env("ARC_CSD_GPU_INDEX"), out var gi) ? gi : null,
            UseTls: useTls ?? false);
    }

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("csd");
        var cfg = LoadConfig(log);
        if (cfg is null) return 78;

        try { _ = CsdNative.AbiVersion(); }
        catch (DllNotFoundException)
        {
            log.LogError("csd: csd_capi.dll not found next to the miner binary — this build has no CSD kernel");
            return 78;
        }

        var explicitIdx = cfg.GpuIndex is int gi ? new[] { gi } : null;
        var devices = Akoya.Miner.Mining.GpuSelection.EnumerateMiningDevices(
            CsdNative.DeviceCount(), CsdNative.DeviceNameAt, explicitIdx, log, "csd");
        if (devices.Count == 0)
        {
            log.LogError("csd: no eligible GPU found — integrated GPUs are skipped; pass --igpu, or set ARC_CSD_GPU_INDEX");
            return 78;
        }
        var names = devices.Select(i => { try { return CsdNative.DeviceNameAt(i); } catch { return $"GPU {i}"; } }).ToArray();
        log.LogInformation("csd: mining on {N} GPU(s), capi abi v{Abi}", devices.Count, CsdNative.AbiVersion());

        Akoya.Miner.Observability.Metrics.Init(devices.Count, new long[devices.Count]);
        Akoya.Miner.Observability.Metrics.SetSessionInfo($"{cfg.Host}:{cfg.Port}", cfg.Worker);
        Akoya.Miner.Observability.Metrics.SetGpuNames(names);
        // Attach hwmon sensors (temperature / fan / power) to the right card.
        Akoya.Miner.Observability.GpuIdentity.RecordPciAddresses(devices);
        Akoya.Miner.Observability.Metrics.SetPoolConnected(false);

        try
        {
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var client = new CsdStratumClient(
                        new CsdStratumClient.PoolConfig(cfg.Host, cfg.Port, cfg.Address, cfg.Worker, devices, cfg.UseTls), log);
                    await client.RunSessionAsync(ct).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    attempt++;
                    var backoff = ReconnectBackoff.NextDelay(attempt);
                    log.LogWarning("csd: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, attempt);
                    try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            return 0;
        }
        finally { /* each solver thread closes its own thread_local device context */ }
    }
}
