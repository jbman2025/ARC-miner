using Akoya.Miner.Config;
using Akoya.Miner.Mining;
using Akoya.Pool;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Prl;

// Pearl/PRL algorithm adapter. The body of RunAsync is the miner's original
// orchestrator reconnect loop, relocated VERBATIM from Program.MineBlocksAsync
// (the only changes are the CancellationToken plumbing). No PRL logic changed.
internal sealed class PrlAlgo : IMiningAlgo
{
    public string Name => "prl";

    /// <summary>Lowest pearl_gemm ABI whose WorkspaceParams layout matches the
    /// struct this build passes. Mirrors PEARL_CAPI_MIN_ABI in
    /// native/pearl-gemm/csrc/capi/pearl_gemm_capi.h.</summary>
    private const int MinNativeAbi = 4;

    /// <summary>Refuse to mine against a pearl_gemm older than the params struct
    /// we hand it. Returns an exit code to propagate, or null to continue.
    ///
    /// This is not defensive boilerplate. ABI v3 added <c>salted_seeds</c> to
    /// WorkspaceParams; a v2 library ignores the field and derives legacy noise
    /// seeds for every share while the host proves salted ones. Nothing about
    /// that is visible on the dashboard — the GPU stays pegged, the hashrate is
    /// right, and 100% of shares die. A stale .so left in an out/ directory is
    /// exactly how that ships, so it has to be caught at startup, by number.</summary>
    private static int? CheckNativeAbi(ILogger log)
    {
        int abi;
        try { abi = PearlGemm.PearlGemmNative.AbiVersion(); }
        catch (Exception ex)
        {
            // No library at all is a different failure, and the existing load
            // paths report it with more context than we can here.
            log.LogDebug(ex, "prl: could not read pearl_gemm ABI version");
            return null;
        }

        if (abi >= MinNativeAbi) return null;

        log.LogCritical(
            "prl: pearl_gemm reports ABI v{Abi}, this build requires v{Min}. The library is "
            + "older than the miner and does not know about the per-σ salted-seed fields, so "
            + "every share would be proved against the wrong noise field and rejected. "
            + "Rebuild the native library (build.ps1 / build-linux-wsl.sh) and make sure the "
            + "rebuilt one is what gets loaded — a stale copy in out/ or out-linux/ wins.",
            abi, MinNativeAbi);
        return 78;
    }

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("startup");

        if (CheckNativeAbi(log) is int abiExit) return abiExit;

        // Reconnect loop: any unhandled stream exit (graceful, RpcException,
        // stream-watchdog cancellation, worker-watchdog cancellation) triggers a
        // jittered exponential backoff + Resume attempt. Fatal config errors
        // break out. Clean exits (server hangup, ReconnectHint) reconnect
        // immediately with attempt counter reset.
        int attempt = 0;
        // Construct the orchestrator ONCE per process. Inside, per-attempt
        // resources (PoolConnection, MiningSession, GpuWorkers) live in
        // RunAsync's using/await-using scopes and are recreated each loop.
        // What we deliberately keep across reconnects is orchestrator state
        // such as the cached benchmark result — the GPU rig's hashrate and
        // iter_ms don't change between a stream-end and the Resume that
        // follows, so re-benchmarking is wasted GPU time.
        var orchestrator = new WorkerOrchestrator(opts, loggerFactory);
        // We are on Pearl — lets the dashboard's fork counter trust the persisted
        // height, which is Pearl's and would otherwise leak onto other algos.
        Observability.Metrics.MarkPrlActive();

        while (!ct.IsCancellationRequested)
        {
            TimeSpan? hintWait = null;
            try
            {
                await orchestrator.RunAsync(ct).ConfigureAwait(false);
                log.LogInformation("orchestrator: stream ended cleanly — reconnecting");
                attempt = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Register rejected"))
            {
                log.LogError(ex, "fatal: server rejected registration — not retrying");
                return 78;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("build that matches your card"))
            {
                // Wrong-card AOT build (acm on a B-series GPU or vice versa). This is
                // permanent — retrying would loop forever, so surface the one-liner
                // and exit. Message-only (no stack trace — it's an operator problem).
                log.LogError("startup: {Message}", ex.Message);
                return 78;
            }
            catch (PoolUnreachableException ex)
            {
                // Translated TaskCanceledException / RpcException(Unavailable|
                // DeadlineExceeded) from Register/Resume — channel never reached
                // ready state. Almost always wrong host/port or firewall. Skip
                // the stack trace (it's all Grpc internals) and surface just the
                // operator-actionable one-liner, then back off and retry like
                // any other transient failure.
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning(
                    "orchestrator: {Msg} — retry in {Delay:F1}s (attempt {Attempt})",
                    ex.Message, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (StreamIdleException ex)
            {
                // Distinct log line: "gateway is alive but silent" is a very
                // different operational signal from a generic RPC failure.
                // We deliberately don't bypass the backoff path — silent stream
                // = treat-as-failure-attempt, same exp backoff applies.
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning(
                    "orchestrator: stream went silent ({Msg}) — retry in {Delay:F1}s (attempt {Attempt})",
                    ex.Message, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (WorkerTripException ex)
            {
                attempt++;
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning(ex,
                    "orchestrator: local worker trip ({Reason}) — retry in {Delay:F1}s (attempt {Attempt})",
                    ex.Reason, backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                attempt++;
                // Exponential cap + ±25% jitter
                var backoff = ReconnectBackoff.NextDelay(attempt);
                log.LogWarning(ex, "orchestrator: error — retry in {Delay:F1}s (attempt {Attempt})",
                    backoff.TotalSeconds, attempt);
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                // Capture any server-supplied ReconnectHint before disposing.
                if (orchestrator.LastReconnectHint is { WaitSeconds: > 0 } h)
                {
                    if (ReconnectBackoff.HintWasClamped(h.WaitSeconds))
                    {
                        log.LogWarning(
                            "orchestrator: ReconnectHint wait={W}s clamped to {C}s",
                            h.WaitSeconds, ReconnectBackoff.MaxReconnectHintSeconds);
                    }
                    hintWait = ReconnectBackoff.NextHintDelay(h.WaitSeconds);
                }
                await orchestrator.DisposeAsync().ConfigureAwait(false);
            }

            if (hintWait is TimeSpan w && !ct.IsCancellationRequested)
            {
                log.LogInformation("orchestrator: honouring ReconnectHint wait={W:F1}s", w.TotalSeconds);
                try { await Task.Delay(w, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        return 0;
    }
}
