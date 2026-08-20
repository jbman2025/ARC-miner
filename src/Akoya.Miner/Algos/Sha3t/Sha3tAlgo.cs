// BitcoinIII (BC3) — --algo sha3t. Pool mining over canonical Bitcoin Stratum
// V1 with a SHA3-256t GPU search kernel (sha3t_capi.dll). Self-contained per
// the --algo plugin rules: config comes from ARC_SHA3T_* / shared pool env only.

using System.Linq;
using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Sha3t;

internal sealed class Sha3tAlgo : IMiningAlgo
{
    public string Name => "sha3t";

    private sealed record Sha3tConfig(string Host, int Port, string Address, string Worker, int? GpuIndex, bool UseTls);

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static Sha3tConfig? LoadConfig(ILogger log)
    {
        var address = Env("ARC_SHA3T_ADDRESS");
        if (address is null)
        {
            var pw = Env("ARC_POOL_WALLET");
            if (pw is not null && pw != "unused-non-prl-algo") address = pw;
        }
        if (address is null) { log.LogError("sha3t: ARC_SHA3T_ADDRESS (or --wallet) is required"); return null; }

        // TLS default follows the shared --tls/--no-tls flag (ARC_POOL_TLS);
        // a stratum+ssl:// / stratum+tls:// scheme on --pool sets it too.
        bool? useTls = Env("ARC_POOL_TLS") is { } t ? t.Equals("true", StringComparison.OrdinalIgnoreCase) : null;

        var poolUrl = Env("ARC_SHA3T_POOL");
        string? host; int port;
        if (poolUrl is not null)
        {
            if (poolUrl.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase) ||
                poolUrl.StartsWith("stratum+tls://", StringComparison.OrdinalIgnoreCase) ||
                poolUrl.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase)) useTls = true;
            else if (poolUrl.StartsWith("stratum+tcp://", StringComparison.OrdinalIgnoreCase) ||
                     poolUrl.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) useTls ??= false;
            var hp = poolUrl.Contains("://") ? poolUrl[(poolUrl.IndexOf("://", StringComparison.Ordinal) + 3)..] : poolUrl;
            var colon = hp.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(hp[(colon + 1)..], out port)) { log.LogError("sha3t: ARC_SHA3T_POOL must be [scheme://]host:port"); return null; }
            host = hp[..colon];
        }
        else
        {
            host = Env("ARC_POOL_HOST");
            var portStr = Env("ARC_POOL_PORT");
            if (host is null || portStr is null || !int.TryParse(portStr, out port))
            {
                log.LogError("sha3t: pool required — set --pool host:port (or ARC_SHA3T_POOL / ARC_POOL_HOST+PORT)");
                return null;
            }
        }

        return new Sha3tConfig(
            Host: host,
            Port: port,
            Address: address,
            Worker: Env("ARC_SHA3T_WORKER") ?? Env("ARC_POOL_WORKER") ?? Environment.MachineName,
            GpuIndex: int.TryParse(Env("ARC_SHA3T_GPU_INDEX"), out var gi) ? gi : null,
            UseTls: useTls ?? false);
    }

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("sha3t");
        var cfg = LoadConfig(log);
        if (cfg is null) return 78;

        try { _ = Sha3tNative.AbiVersion(); }
        catch (DllNotFoundException)
        {
            log.LogError("sha3t: sha3t_capi.dll not found next to the miner binary — this build has no SHA3-256t kernel");
            return 78;
        }

        var explicitIdx = cfg.GpuIndex is int gi ? new[] { gi } : null;
        var devices = GpuSelection.EnumerateMiningDevices(
            Sha3tNative.DeviceCount(), Sha3tNative.DeviceNameAt, explicitIdx, log, "sha3t");
        if (devices.Count == 0)
        {
            log.LogError("sha3t: no eligible GPU found — integrated GPUs are skipped; pass --igpu, or set ARC_SHA3T_GPU_INDEX");
            return 78;
        }
        var names = devices.Select(i => { try { return Sha3tNative.DeviceNameAt(i); } catch { return $"GPU {i}"; } }).ToArray();
        log.LogInformation("sha3t: mining BitcoinIII on {N} GPU(s), capi abi v{Abi}", devices.Count, Sha3tNative.AbiVersion());

        Akoya.Miner.Observability.Metrics.Init(devices.Count, new long[devices.Count]);
        Akoya.Miner.Observability.Metrics.SetSessionInfo($"{cfg.Host}:{cfg.Port}", cfg.Worker);
        Akoya.Miner.Observability.Metrics.SetGpuNames(names);
        Akoya.Miner.Observability.GpuIdentity.RecordPciAddresses(devices);
        Akoya.Miner.Observability.Metrics.SetPoolConnected(false);

        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var client = new Sha3tStratumClient(
                    new Sha3tStratumClient.PoolConfig(cfg.Host, cfg.Port, cfg.Address, cfg.Worker, devices, cfg.UseTls), log);
                await client.RunSessionAsync(ct).ConfigureAwait(false);
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning("sha3t: {Msg} — retry in {Delay:F0}s (attempt {Attempt})", ex.Message, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return 0;
    }
}
