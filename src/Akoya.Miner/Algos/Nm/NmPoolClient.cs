// Stratum client for NeuroMorph (nm/1, Cereblix / CRB).
//
// The dialect is the Monero/XMRig login style (`login` / `job` / `submit` /
// `keepalived`) that RxPoolClient already speaks, so this is closely modelled on
// it. The NeuroMorph-specific parts, all documented in the fork's PROTOCOL.md:
//
//   * blob is a 124-byte header; the nonce is 8 bytes LE at offset 116. The
//     miner iterates ONLY the low 4 bytes (116..120); the high 4 (120..124) are
//     the pool's per-connection extranonce1 and must be preserved.
//   * submit sends all 8 nonce bytes in their in-blob order (16 hex) plus the
//     32-byte hash as `result`.
//   * target is 256-bit BIG-endian, compared with memcmp — see NmHash.
//   * seed_hash selects the epoch (VM parameters + the shared 64 MiB dataset).
//     Solver threads are stopped across a seed change because the native side
//     rebuilds that dataset in place; see neuromorph_capi.cpp.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Akoya.Miner.Algos.Cpu;
using Akoya.Miner.Mining.Stratum;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Nm;

internal sealed class NmPoolClient : IAsyncDisposable
{
    internal sealed record PoolConfig(
        string Host,
        int Port,
        string Address,
        string Worker,
        string Password,
        int Threads,
        bool UseTls,
        int KeepaliveSec,
        bool Affinity = false);

    private readonly PoolConfig _cfg;
    private readonly ILogger _log;
    // Transport, framing and id/response correlation live in StratumSession.
    private readonly StratumSession _session;

    // login / job pushes / keepalived / submit live in
    // CryptoNoteStratumDialect. What stays here is NeuroMorph's own: the fixed
    // 124-byte blob with its nonce at 116, the BIG-endian target, and the
    // seed-driven VM re-init.
    private CryptoNoteStratumDialect _dialect = null!;
    private byte[]? _currentSeed;

    private readonly long[] _counts;

    // Dashboard slot: the CPU row Metrics.InitCpu appended after the GPUs. Index
    // 0 is a real GPU when dual-mining (prl+nm and friends), so never hardcode it.
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    public NmPoolClient(PoolConfig cfg, ILogger log)
    {
        _cfg = cfg;
        _log = log;
        _counts = new long[cfg.Threads * 8];
        _session = new StratumSession(log, "nm-pool");
    }

    public ulong Accepted => (ulong)_dialect.Accepted;
    public ulong Rejected => (ulong)_dialect.Rejected;

    public async Task RunSessionAsync(CancellationToken ct)
    {
        await _session.ConnectAsync(_cfg.Host, _cfg.Port, _cfg.UseTls, ct).ConfigureAwait(false);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _dialect = new CryptoNoteStratumDialect(_session, _log, "nm-pool", ParsePoolJob);
        _dialect.JobReceived += j =>
        {
            Metrics.SetDiff(CpuIndex, Akoya.Miner.Observability.DisplayFormat.DiffValue(NmHash.DifficultyOf(j.Target)));
            Metrics.SetCpuBlockHeight(j.Height);
            _log.LogInformation("nm-pool: new job={Job} height={Height} diff={Diff}",
                j.JobId, j.Height, (long)Math.Round(NmHash.DifficultyOf(j.Target)));
        };
        // Order matters: the dialect must exist before anything touches it, and
        // the read loop must be running before LoginAsync awaits its response.
        var readerTask = _dialect.ReadLoopAsync(sessionCts.Token);
        var submitQueue = new ConcurrentQueue<(string JobId, ulong Nonce, byte[] Hash)>();
        var submitTask = SubmitLoopAsync(submitQueue, sessionCts.Token);
        var keepaliveTask = _dialect.KeepAliveLoopAsync(_cfg.KeepaliveSec, sessionCts.Token);

        Thread[]? solvers = null;
        CancellationTokenSource? solverCts = null;

        try
        {
            // The pool takes "<address>.<worker>" in the login field, like XMRig.
            // The trailing "algo" member is an ARRAY, so this cannot use
            // StratumJson.Obj wholesale.
            var loginName = string.IsNullOrEmpty(_cfg.Worker) ? _cfg.Address : $"{_cfg.Address}.{_cfg.Worker}";
            await _dialect.LoginAsync("{" +
                $"\"login\":{Str(loginName)}," +
                $"\"pass\":{Str(_cfg.Password)}," +
                $"\"agent\":{Str(VersionInfo.UserAgent)}," +
                $"\"algo\":{StratumJson.StrArray("nm/1")}" +
                "}", sessionCts.Token).ConfigureAwait(false);
            Metrics.SetCpuPoolConnected(true);

            var reportSw = Stopwatch.StartNew();
            long lastTotal = 0; double lastSec = 0;

            while (!sessionCts.Token.IsCancellationRequested)
            {
                var (activeJob, _) = _dialect.Snapshot();

                if (activeJob is null)
                {
                    await Task.Delay(100, sessionCts.Token).ConfigureAwait(false);
                    continue;
                }

                if (_currentSeed is null || !_currentSeed.AsSpan().SequenceEqual(activeJob.SeedHash))
                {
                    // Epoch change. Stop the solvers first: the native side rebuilds
                    // the shared 64 MiB dataset in place, and a hash in flight would
                    // read it half-rewritten.
                    if (solvers is not null && solverCts is not null)
                    {
                        _log.LogInformation("nm-pool: epoch seed changed — stopping solvers to rebuild the dataset");
                        solverCts.Cancel();
                        foreach (var t in solvers) t.Join(TimeSpan.FromSeconds(5));
                        solverCts.Dispose();
                        solvers = null;
                    }

                    _log.LogInformation("nm-pool: epoch seed {Seed} — deriving VM params + 64 MiB dataset ({Threads} threads)",
                        Convert.ToHexString(activeJob.SeedHash)[..12], _cfg.Threads);

                    _currentSeed = activeJob.SeedHash;

                    solverCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                    solvers = new Thread[_cfg.Threads];
                    var seedCopy = (byte[])activeJob.SeedHash.Clone();

                    // Dedicated OS threads, NOT Task.Run — a solver never yields,
                    // so on the thread pool these would starve the reader / submit
                    // / keepalive tasks that share it. Same reasoning as rx and gr.
                    var pinOrder = CpuAffinity.BuildPinOrder(_cfg.Threads);
                    for (int i = 0; i < _cfg.Threads; i++)
                    {
                        int idx = i;
                        int cpu = _cfg.Affinity ? pinOrder[i % pinOrder.Length] : -1;
                        solvers[i] = new Thread(() => SolverThread(idx, _cfg.Threads, cpu, seedCopy, _dialect, _counts, submitQueue, _log, solverCts.Token))
                        {
                            IsBackground = true,
                            Name = $"nm-solver-{idx}",
                            Priority = ThreadPriority.Normal
                        };
                        solvers[i].Start();
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                long total = 0;
                for (int i = 0; i < _cfg.Threads; i++) total += Volatile.Read(ref _counts[i * 8]);
                double now = reportSw.Elapsed.TotalSeconds, dt = now - lastSec;
                double hs = dt > 0 ? (total - lastTotal) / dt : 0;
                lastTotal = total; lastSec = now;
                Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * _cfg.Threads / hs : 0);
                // Redundant with the dashboard's per-worker table — see the note
                // in RxPoolClient. Metrics still feeds the table.
                if (!Akoya.Miner.Observability.Dashboard.Active)
                _log.LogInformation("nm-pool: {Hs:F1} H/s ({Threads}T) a/r={Acc}/{Rej} diff={Diff}",
                    hs, _cfg.Threads, _dialect.Accepted, _dialect.Rejected,
                    (long)Math.Round(CurrentDifficulty()));

                if (readerTask.IsCompleted)
                {
                    await readerTask.ConfigureAwait(false);
                    throw new IOException("pool reader task completed unexpectedly");
                }
                if (submitTask.IsCompleted)
                {
                    await submitTask.ConfigureAwait(false);
                    throw new IOException("pool submit task completed unexpectedly");
                }
            }
        }
        finally
        {
            sessionCts.Cancel();
            if (solverCts is not null)
            {
                solverCts.Cancel();
                if (solvers is not null)
                {
                    foreach (var t in solvers) t.Join(TimeSpan.FromSeconds(5));
                }
                solverCts.Dispose();
            }

            try { await readerTask.ConfigureAwait(false); } catch { }
            try { await submitTask.ConfigureAwait(false); } catch { }
            try { await keepaliveTask.ConfigureAwait(false); } catch { }

            await _session.DisposeAsync().ConfigureAwait(false);
            Metrics.SetCpuPoolConnected(false);
        }
    }

    private async Task SubmitLoopAsync(ConcurrentQueue<(string JobId, ulong Nonce, byte[] Hash)> queue, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!queue.TryDequeue(out var hit))
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                    continue;
                }

                // All 8 nonce bytes, in the order they sit in the blob (LE).
                // rx submits only 4 — that difference is why nonce formatting
                // stayed with the algo rather than moving into the dialect.
                Span<byte> nonceBytes = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(nonceBytes, hit.Nonce);

                bool ok = await _dialect.SubmitAsync(
                    hit.JobId,
                    Convert.ToHexString(nonceBytes).ToLowerInvariant(),
                    Convert.ToHexString(hit.Hash).ToLowerInvariant(),
                    ct).ConfigureAwait(false);

                if (ok)
                {
                    Metrics.IncShareAccepted(CpuIndex);
                    _log.LogInformation("nm-pool: share accepted job={Job} (a/r={Acc}/{Rej})",
                        hit.JobId, _dialect.Accepted, _dialect.Rejected);
                }
                else
                {
                    Metrics.IncShareRejected(CpuIndex);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static CryptoNoteJob ParsePoolJob(JsonElement el)
    {
        var jobId = el.GetProperty("job_id").GetString() ?? "";
        var blob = Convert.FromHexString(el.GetProperty("blob").GetString() ?? "");
        var target = NmHash.ParseTarget(el.GetProperty("target").GetString() ?? "");
        var seedHash = Convert.FromHexString(el.GetProperty("seed_hash").GetString() ?? "");
        long height = el.TryGetProperty("height", out var hEl) ? hEl.GetInt64() : 0;

        if (blob.Length != NmNative.HeaderBytes)
        {
            throw new InvalidOperationException(
                $"nm: pool sent a {blob.Length}-byte blob, expected {NmNative.HeaderBytes}");
        }
        if (seedHash.Length != NmNative.SeedBytes)
        {
            throw new InvalidOperationException(
                $"nm: pool sent a {seedHash.Length}-byte seed_hash, expected {NmNative.SeedBytes}");
        }

        // NeuroMorph's blob is always 124 bytes with the nonce 8 bytes LE at
        // offset 116 — no sniffing, unlike rx which has two blob layouts.
        return new CryptoNoteJob(jobId, blob, target, seedHash, height,
                                 NmNative.NonceOffset, FullWidthNonce: true);
    }

    private double CurrentDifficulty()
    {
        var (job, _) = _dialect.Snapshot();
        return job is null ? 0 : NmHash.DifficultyOf(job.Target);
    }

    private static unsafe void SolverThread(int idx, int threads, int cpu, byte[] seed, CryptoNoteStratumDialect box, long[] counts,
        ConcurrentQueue<(string JobId, ulong Nonce, byte[] Hash)> hits, ILogger log, CancellationToken ct)
    {
        CpuAffinity.PinCurrentThread(cpu);

        nint ctx = NmNative.CreateCtx();
        if (ctx == nint.Zero)
        {
            log.LogError("nm-pool: worker {Idx} ctx create failed — {Err}", idx, NmNative.LastError());
            return;
        }

        try
        {
            fixed (byte* pSeed = seed)
            {
                if (NmNative.SetSeed(ctx, pSeed) != 0)
                {
                    log.LogError("nm-pool: worker {Idx} set_seed failed — {Err}", idx, NmNative.LastError());
                    return;
                }
            }

            long lastGen = -1;
            var blob = new byte[NmNative.HeaderBytes];
            byte[] target = Array.Empty<byte>();
            string jobId = "";
            long height = 0;
            uint nonce = (uint)idx;
            long local = 0;
            var outbuf = new byte[NmNative.HashBytes];

            while (!ct.IsCancellationRequested)
            {
                var (j, gen) = box.Snapshot();
                if (j is null) { Thread.Sleep(25); continue; }

                if (gen != lastGen)
                {
                    bool resetNonce = jobId != j.JobId;
                    Array.Copy(j.Blob, blob, NmNative.HeaderBytes);
                    target = j.Target;
                    jobId = j.JobId;
                    height = j.Height;
                    lastGen = gen;
                    if (resetNonce) nonce = (uint)idx;
                }

                // Iterate ONLY the low 4 bytes of the 8-byte nonce field. The high
                // 4 bytes are the pool's extranonce1 and stay exactly as sent.
                BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(NmNative.NonceOffset, 4), nonce);

                fixed (byte* pBlob = blob)
                fixed (byte* pOut = outbuf)
                    NmNative.Hash(ctx, pBlob, (ulong)height, pOut);

                if (NmHash.MeetsTarget(outbuf, target))
                {
                    // Submit all 8 bytes of the field, in blob order.
                    ulong fullNonce = BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(NmNative.NonceOffset, 8));
                    hits.Enqueue((jobId, fullNonce, (byte[])outbuf.Clone()));
                }

                nonce += (uint)threads;
                if ((++local & 0x3F) == 0) Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x3FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
            Volatile.Write(ref counts[idx * 8], local);
        }
        finally { NmNative.DestroyCtx(ctx); }
    }

    /// <summary>JSON-escape and quote a string. NativeAOT disables
    /// reflection-based serialization, so payloads are built from these.</summary>
    private static string Str(string s) => StratumJson.Str(s);

    private Task<JsonElement> CallAsync(string method, string paramsJson, CancellationToken ct, TimeSpan? timeout = null)
        => _session.CallAsync(method, paramsJson, ct, timeout);

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}
