using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Observability;

internal static class Metrics
{
    private static long[] _iters             = Array.Empty<long>();
    private static long[] _triggers          = Array.Empty<long>();
    private static long[] _blocksAccepted    = Array.Empty<long>();
    private static long[] _blocksRejected    = Array.Empty<long>();
    private static long[] _itersPerSec       = Array.Empty<long>();
    private static long[] _tmadsPerSec       = Array.Empty<long>();
    private static long[] _hashesPerSec      = Array.Empty<long>();
    private static long[] _tilesPerSec       = Array.Empty<long>();
    private static long[] _expectedOpensPerSec = Array.Empty<long>();
    private static long[] _iterMs            = Array.Empty<long>();
    private static long[] _sigmaRotations    = Array.Empty<long>();
    private static long[] _sigmaRotationLatestMs = Array.Empty<long>();
    private static long[] _sigmaRotationMaxMs = Array.Empty<long>();
    private static long[] _sigmaRotationDrainMs = Array.Empty<long>();
    private static long[] _sigmaRotationInstallMs = Array.Empty<long>();
    private static long[] _sigmaRotationBMerkleMs = Array.Empty<long>();
    private static long[] _sigmaRotationLostIters = Array.Empty<long>();
    private static long[] _sigmaRotationBSeedChanged = Array.Empty<long>();

    private static long[] _heartbeatTicks    = Array.Empty<long>();

    private static long   _blockFinds;
    private static long   _poolConnected;
    private static long   _poolLatencyMsBits;

    // Session metadata for the JSON stats API (set once at startup; the
    // strings are replaced atomically so no locking is needed).
    private static string   _poolUrl   = "";
    private static string   _workerName = "";
    private static string[] _gpuNames  = Array.Empty<string>();
    private static long     _startedUtcTicks;

    private static int    _gpuCount;
    private static HttpListener? _listener;
    private static Thread? _serverThread;

    public static void SetSessionInfo(string poolUrl, string workerName)
    {
        _poolUrl    = poolUrl;
        _workerName = workerName;
    }

    // Pre-rendered pool_info JSON object (pool-info/v1 fee transparency), or null
    // when the pool advertised nothing. Additive field in the stats payload.
    private static volatile string? _poolInfoJson;

    public static void SetPoolInfo(double feePercent, string scheme, string? minPayout, string trustLabel)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(96);
        sb.Append("{\"fee_percent\":").Append(feePercent.ToString("G", inv));
        sb.Append(",\"payout_scheme\":\"").Append(Esc(scheme)).Append('"');
        if (!string.IsNullOrEmpty(minPayout))
            sb.Append(",\"min_payout\":\"").Append(Esc(minPayout)).Append('"');
        sb.Append(",\"trust\":\"").Append(Esc(trustLabel)).Append("\"}");
        _poolInfoJson = sb.ToString();
    }

    public static void SetGpuNames(string[] names) => _gpuNames = names;

    public static void Init(int gpuCount, long[] heartbeats)
    {
        Interlocked.CompareExchange(ref _startedUtcTicks, DateTime.UtcNow.Ticks, 0);
        _gpuCount         = gpuCount;
        _iters            = new long[gpuCount];
        _triggers         = new long[gpuCount];
        _blocksAccepted   = new long[gpuCount];
        _blocksRejected   = new long[gpuCount];
        _itersPerSec      = new long[gpuCount];
        _tmadsPerSec      = new long[gpuCount];
        _hashesPerSec     = new long[gpuCount];
        _tilesPerSec      = new long[gpuCount];
        _expectedOpensPerSec = new long[gpuCount];
        _iterMs           = new long[gpuCount];
        _sigmaRotations   = new long[gpuCount];
        _sigmaRotationLatestMs = new long[gpuCount];
        _sigmaRotationMaxMs = new long[gpuCount];
        _sigmaRotationDrainMs = new long[gpuCount];
        _sigmaRotationInstallMs = new long[gpuCount];
        _sigmaRotationBMerkleMs = new long[gpuCount];
        _sigmaRotationLostIters = new long[gpuCount];
        _sigmaRotationBSeedChanged = new long[gpuCount];
        _heartbeatTicks   = heartbeats;
    }

    /// <summary>Record worker liveness. Called from GpuWorker.TouchProgress on
    /// every observable progress event (~iters/s rate), so /metrics
    /// heartbeat_age_seconds and the JSON stats heartbeat field reflect real
    /// worker activity — a wedged GPU shows a growing age within seconds.</summary>
    public static void TouchHeartbeat(int gpu)
    {
        if ((uint)gpu < (uint)_heartbeatTicks.Length)
            Volatile.Write(ref _heartbeatTicks[gpu], DateTime.UtcNow.Ticks);
    }

    public static void IncIters(int gpu, long n)
    {
        if ((uint)gpu < (uint)_iters.Length)        Interlocked.Add(ref _iters[gpu], n);
    }
    public static void IncTriggers(int gpu)
    {
        if ((uint)gpu < (uint)_triggers.Length)     Interlocked.Increment(ref _triggers[gpu]);
    }
    public static void IncShareAccepted(int gpu)
    {
        if ((uint)gpu < (uint)_blocksAccepted.Length) Interlocked.Increment(ref _blocksAccepted[gpu]);
    }
    public static void IncShareRejected(int gpu)
    {
        if ((uint)gpu < (uint)_blocksRejected.Length) Interlocked.Increment(ref _blocksRejected[gpu]);
    }
    public static void IncBlockFind()                   => Interlocked.Increment(ref _blockFinds);

    /// <summary>Cumulative pool-confirmed share totals across all GPUs (process
    /// lifetime, not reset on reconnect). Used by the share-result line and the
    /// session summary.</summary>
    public static (long Accepted, long Rejected) ShareTotals()
    {
        long a = 0, r = 0;
        for (int i = 0; i < _blocksAccepted.Length; i++) a += Interlocked.Read(ref _blocksAccepted[i]);
        for (int i = 0; i < _blocksRejected.Length; i++) r += Interlocked.Read(ref _blocksRejected[i]);
        return (a, r);
    }

    /// <summary>Sum of the latest per-GPU hashes/s gauges (whole-rig hashrate).</summary>
    public static double TotalHashesPerSec()
    {
        double sum = 0;
        for (int i = 0; i < _hashesPerSec.Length; i++)
            sum += BitConverter.Int64BitsToDouble(Volatile.Read(ref _hashesPerSec[i]));
        return double.IsFinite(sum) ? sum : 0.0;
    }

    public static void SetThroughput(
        int gpu,
        double itersPerSec,
        double tmadsPerSec,
        double hashesPerSec,
        double iterMs,
        double tilesPerSec = 0.0,
        double expectedOpensPerSec = 0.0)
    {
        if ((uint)gpu >= (uint)_itersPerSec.Length) return;
        Interlocked.Exchange(ref _itersPerSec[gpu],          BitConverter.DoubleToInt64Bits(itersPerSec));
        Interlocked.Exchange(ref _tmadsPerSec[gpu],          BitConverter.DoubleToInt64Bits(tmadsPerSec));
        Interlocked.Exchange(ref _hashesPerSec[gpu],         BitConverter.DoubleToInt64Bits(hashesPerSec));
        Interlocked.Exchange(ref _tilesPerSec[gpu],          BitConverter.DoubleToInt64Bits(tilesPerSec));
        Interlocked.Exchange(ref _expectedOpensPerSec[gpu],  BitConverter.DoubleToInt64Bits(expectedOpensPerSec));
        Interlocked.Exchange(ref _iterMs[gpu],               BitConverter.DoubleToInt64Bits(iterMs));
    }

    public static void RecordSigmaRotation(
        int gpu,
        double totalMs,
        double drainMs,
        double installMs,
        double bMerkleMs,
        double lostIters,
        bool bSeedChanged)
    {
        if ((uint)gpu >= (uint)_sigmaRotations.Length) return;

        Interlocked.Increment(ref _sigmaRotations[gpu]);
        Interlocked.Exchange(ref _sigmaRotationLatestMs[gpu], BitConverter.DoubleToInt64Bits(totalMs));
        Interlocked.Exchange(ref _sigmaRotationDrainMs[gpu], BitConverter.DoubleToInt64Bits(drainMs));
        Interlocked.Exchange(ref _sigmaRotationInstallMs[gpu], BitConverter.DoubleToInt64Bits(installMs));
        Interlocked.Exchange(ref _sigmaRotationBMerkleMs[gpu], BitConverter.DoubleToInt64Bits(bMerkleMs));
        Interlocked.Exchange(ref _sigmaRotationLostIters[gpu], BitConverter.DoubleToInt64Bits(lostIters));
        Interlocked.Exchange(ref _sigmaRotationBSeedChanged[gpu], BitConverter.DoubleToInt64Bits(bSeedChanged ? 1.0 : 0.0));

        long nextBits = BitConverter.DoubleToInt64Bits(totalMs);
        while (true)
        {
            long curBits = Volatile.Read(ref _sigmaRotationMaxMs[gpu]);
            double cur = BitConverter.Int64BitsToDouble(curBits);
            if (double.IsFinite(cur) && cur >= totalMs) break;
            if (Interlocked.CompareExchange(ref _sigmaRotationMaxMs[gpu], nextBits, curBits) == curBits) break;
        }
    }

    public static void SetPoolConnected(bool connected)
        => Interlocked.Exchange(ref _poolConnected, connected ? 1L : 0L);

    public static void SetPoolLatencyMs(double ms)
        => Interlocked.Exchange(ref _poolLatencyMsBits, BitConverter.DoubleToInt64Bits(ms));

    public static double GetPoolLatencyMs()
    {
        var v = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _poolLatencyMsBits));
        return double.IsFinite(v) ? v : 0.0;
    }

    public static bool IsPoolConnected => Interlocked.Read(ref _poolConnected) == 1L;

    public static bool TryStart(int port, ILogger log, CancellationToken ct)
    {
        // http://*:{port}/ needs a URL ACL (admin) on Windows; the localhost
        // prefix does not. Try the wide bind first (rig dashboards scraping
        // over the LAN), fall back to localhost-only — which is all a bundling
        // launcher like Kryptex polling the JSON stats API needs.
        // http.sys matches the Host header against the prefix, so the loopback
        // fallback must register BOTH localhost and 127.0.0.1 or pollers using
        // the numeric address get 400 Invalid Hostname.
        string bound = "";
        var prefixSets = new[]
        {
            new[] { $"http://*:{port}/" },
            new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" },
        };
        foreach (var prefixes in prefixSets)
        {
            try
            {
                _listener = new HttpListener();
                foreach (var prefix in prefixes) _listener.Prefixes.Add(prefix);
                _listener.Start();
                bound = string.Join(" ", prefixes);
                break;
            }
            catch (Exception e)
            {
                log.LogDebug("metrics: bind {Prefix} failed ({Err})", string.Join(" ", prefixes), e.Message);
                try { _listener?.Close(); } catch { }
                _listener = null;
            }
        }
        if (_listener is null)
        {
            log.LogWarning("metrics: failed to bind port {Port} — stats API disabled", port);
            return false;
        }

        _serverThread = new Thread(() => ServeLoop(log, ct)) { IsBackground = true, Name = "metrics-http" };
        _serverThread.Start();
        log.LogInformation("metrics: stats API on {Bound} (/api/stats JSON, /metrics Prometheus)", bound);
        return true;
    }

    public static void Stop()
    {
        try { _listener?.Stop(); _listener?.Close(); } catch { /* shutdown */ }
    }

    public readonly record struct Snapshot(
        int GpuCount,
        long[] Accepted,
        long[] Rejected,
        double[] TmadsPerSec,
        double[] HashesPerSec,
        double[] ItersPerSec,
        double[] TilesPerSec,
        double[] ExpectedOpensPerSec);

    public static Snapshot GetSnapshot()
    {
        int n = _gpuCount;
        var accepted    = new long[n];
        var rejected    = new long[n];
        var tmads       = new double[n];
        var hashes      = new double[n];
        var iters       = new double[n];
        var tiles       = new double[n];
        var expected    = new double[n];
        for (int g = 0; g < n; g++)
        {
            accepted[g] = Volatile.Read(ref _blocksAccepted[g]);
            rejected[g] = Volatile.Read(ref _blocksRejected[g]);
            tmads[g]    = BitConverter.Int64BitsToDouble(Volatile.Read(ref _tmadsPerSec[g]));
            hashes[g]   = BitConverter.Int64BitsToDouble(Volatile.Read(ref _hashesPerSec[g]));
            iters[g]    = BitConverter.Int64BitsToDouble(Volatile.Read(ref _itersPerSec[g]));
            tiles[g]    = BitConverter.Int64BitsToDouble(Volatile.Read(ref _tilesPerSec[g]));
            expected[g] = BitConverter.Int64BitsToDouble(Volatile.Read(ref _expectedOpensPerSec[g]));
            if (!double.IsFinite(tmads[g]))  tmads[g] = 0;
            if (!double.IsFinite(hashes[g])) hashes[g] = 0;
            if (!double.IsFinite(iters[g]))  iters[g] = 0;
            if (!double.IsFinite(tiles[g]))  tiles[g] = 0;
            if (!double.IsFinite(expected[g])) expected[g] = 0;
        }
        return new Snapshot(n, accepted, rejected, tmads, hashes, iters, tiles, expected);
    }

    // ─── Live dashboard snapshot ───────────────────────────────────────────
    // A single, allocation-light read of everything the in-place TUI dashboard
    // renders, so the render loop touches the volatile fields exactly once per
    // tick rather than calling a dozen accessors (each of which re-reads).
    public readonly record struct DashGpu(
        int Id, string Name, double HashesPerSec, double IterMs,
        long Accepted, long Rejected, double HeartbeatAgeSec);

    public readonly record struct DashSnapshot(
        string PoolUrl, string Worker, bool Connected, double LatencyMs,
        long Accepted, long Rejected, long BlockFinds,
        double TotalHashesPerSec, string? PoolInfoJson, DashGpu[] Gpus);

    public static DashSnapshot GetDashboardSnapshot()
    {
        var hashesArr = _hashesPerSec; var iterArr = _iterMs;
        var accArr = _blocksAccepted; var rejArr = _blocksRejected;
        var hbArr = _heartbeatTicks;  var names = _gpuNames;
        int n = Math.Min(hashesArr.Length,
                Math.Min(iterArr.Length, Math.Min(accArr.Length, rejArr.Length)));

        long nowTicks = DateTime.UtcNow.Ticks;
        var rows = new DashGpu[n];
        double totalHs = 0;
        long acc = 0, rej = 0;
        for (int g = 0; g < n; g++)
        {
            double hs = BitConverter.Int64BitsToDouble(Volatile.Read(ref hashesArr[g]));
            double ms = BitConverter.Int64BitsToDouble(Volatile.Read(ref iterArr[g]));
            if (!double.IsFinite(hs)) hs = 0;
            if (!double.IsFinite(ms)) ms = 0;
            long a = Volatile.Read(ref accArr[g]);
            long r = Volatile.Read(ref rejArr[g]);
            long hb = hbArr.Length > g ? Interlocked.Read(ref hbArr[g]) : 0;
            double hbAge = hb == 0 ? 0.0 : (nowTicks - hb) / (double)TimeSpan.TicksPerSecond;
            string name = g < names.Length ? names[g] : $"GPU {g}";
            rows[g] = new DashGpu(g, name, hs, ms, a, r, hbAge);
            totalHs += hs; acc += a; rej += r;
        }

        return new DashSnapshot(
            _poolUrl, _workerName, IsPoolConnected, GetPoolLatencyMs(),
            acc, rej, Interlocked.Read(ref _blockFinds),
            double.IsFinite(totalHs) ? totalHs : 0.0, _poolInfoJson, rows);
    }

    private static void ServeLoop(ILogger log, CancellationToken ct)
    {
        var l = _listener!;
        while (!ct.IsCancellationRequested && l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); }
            catch { break; }

            try
            {
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                if (path == "/metrics")
                {
                    var body = Encoding.UTF8.GetBytes(Render());
                    ctx.Response.ContentType = "text/plain; version=0.0.4";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path is "/" or "/stats" or "/api/stats" or "/summary")
                {
                    var body = Encoding.UTF8.GetBytes(RenderJson());
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
            catch (Exception e) { log.LogDebug("metrics: serve err {Err}", e.Message); }
        }
    }

    /// <summary>JSON stats document for bundling launchers (Kryptex etc.) and
    /// dashboards. Served at /, /stats, /api/stats, /summary. Hashrate fields
    /// are in hashes/s (the protocol unit shown as TH/s in the console: CTA
    /// tiles × difficulty-adjustment factor). Schema is additive-only: fields
    /// may be added in later versions but never renamed or removed.</summary>
    private static string RenderJson()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(1024);

        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        static double Fin(double v) => double.IsFinite(v) ? v : 0.0;

        long started = Interlocked.Read(ref _startedUtcTicks);
        double uptimeSec = started == 0 ? 0 : (DateTime.UtcNow.Ticks - started) / (double)TimeSpan.TicksPerSecond;

        // Snapshot array references: Init() swaps them after the HTTP server is
        // already up, so derive the GPU count from the arrays themselves.
        var hashesArr = _hashesPerSec; var tmadsArr = _tmadsPerSec; var iterArr = _iterMs;
        var accArr = _blocksAccepted; var rejArr = _blocksRejected;
        var hbArr = _heartbeatTicks;  var names = _gpuNames;
        int n = Math.Min(hashesArr.Length, Math.Min(tmadsArr.Length,
                Math.Min(iterArr.Length, Math.Min(accArr.Length, rejArr.Length))));

        double totalHs = 0, totalTmads = 0;
        for (int g = 0; g < n; g++)
        {
            totalHs    += Fin(BitConverter.Int64BitsToDouble(Volatile.Read(ref hashesArr[g])));
            totalTmads += Fin(BitConverter.Int64BitsToDouble(Volatile.Read(ref tmadsArr[g])));
        }
        var (acc, rej) = ShareTotals();

        sb.Append('{');
        sb.Append("\"miner\":\"arc-miner\",");
        sb.Append("\"version\":\"").Append(Esc(VersionInfo.MinerVersion)).Append("\",");
        sb.Append("\"git_sha\":\"").Append(Esc(VersionInfo.GitSha)).Append("\",");
        sb.Append("\"algorithm\":\"pearl\",");
        sb.Append("\"uptime_seconds\":").Append(uptimeSec.ToString("F0", inv)).Append(',');
        sb.Append("\"pool\":{");
        sb.Append("\"url\":\"").Append(Esc(_poolUrl)).Append("\",");
        sb.Append("\"worker\":\"").Append(Esc(_workerName)).Append("\",");
        sb.Append("\"connected\":").Append(IsPoolConnected ? "true" : "false").Append(',');
        sb.Append("\"latency_ms\":").Append(GetPoolLatencyMs().ToString("F1", inv));
        sb.Append("},");
        var poolInfoJson = _poolInfoJson;
        if (poolInfoJson != null)
            sb.Append("\"pool_info\":").Append(poolInfoJson).Append(',');
        sb.Append("\"hashrate_total_hs\":").Append(totalHs.ToString("G", inv)).Append(',');
        sb.Append("\"tmads_total\":").Append(totalTmads.ToString("G", inv)).Append(',');
        sb.Append("\"shares\":{");
        sb.Append("\"accepted\":").Append(acc.ToString(inv)).Append(',');
        sb.Append("\"rejected\":").Append(rej.ToString(inv)).Append(',');
        sb.Append("\"block_finds\":").Append(Interlocked.Read(ref _blockFinds).ToString(inv));
        sb.Append("},");
        sb.Append("\"gpus\":[");
        long nowTicks = DateTime.UtcNow.Ticks;
        for (int g = 0; g < n; g++)
        {
            if (g > 0) sb.Append(',');
            string name = g < names.Length ? names[g] : "";
            long hb = hbArr.Length > g ? Interlocked.Read(ref hbArr[g]) : 0;
            double hbAge = hb == 0 ? 0.0 : (nowTicks - hb) / (double)TimeSpan.TicksPerSecond;
            sb.Append('{');
            sb.Append("\"id\":").Append(g.ToString(inv)).Append(',');
            sb.Append("\"name\":\"").Append(Esc(name)).Append("\",");
            sb.Append("\"hashrate_hs\":").Append(Fin(BitConverter.Int64BitsToDouble(Volatile.Read(ref hashesArr[g]))).ToString("G", inv)).Append(',');
            sb.Append("\"tmads_per_sec\":").Append(Fin(BitConverter.Int64BitsToDouble(Volatile.Read(ref tmadsArr[g]))).ToString("G", inv)).Append(',');
            sb.Append("\"iter_ms\":").Append(Fin(BitConverter.Int64BitsToDouble(Volatile.Read(ref iterArr[g]))).ToString("F1", inv)).Append(',');
            sb.Append("\"accepted\":").Append(Volatile.Read(ref accArr[g]).ToString(inv)).Append(',');
            sb.Append("\"rejected\":").Append(Volatile.Read(ref rejArr[g]).ToString(inv)).Append(',');
            sb.Append("\"heartbeat_age_seconds\":").Append(hbAge.ToString("F1", inv));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string Render()
    {
        var sb = new StringBuilder(4096);
        var inv = CultureInfo.InvariantCulture;

        sb.Append("# HELP arc_miner_info Build metadata.\n");
        sb.Append("# TYPE arc_miner_info gauge\n");
        sb.Append("arc_miner_info{git_sha=\"").Append(VersionInfo.GitSha).Append("\"} 1\n");

        Counter(sb, "arc_miner_iters_total",            "Total host-signal poll iterations.",     _iters);
        Counter(sb, "arc_miner_triggers_total",         "Total GPU triggers (tile met σ target).", _triggers);
        Counter(sb, "arc_miner_sigma_rotations_total",  "Total observed sigma installs or retargets.", _sigmaRotations);

        sb.Append("# HELP arc_miner_blocks_submitted_total Submitted shares by pool result (V2: shares; V1: blocks).\n");
        sb.Append("# TYPE arc_miner_blocks_submitted_total counter\n");
        for (int g = 0; g < _gpuCount; g++)
        {
            sb.Append("arc_miner_blocks_submitted_total{gpu=\"").Append(g).Append("\",result=\"accepted\"} ")
              .Append(Volatile.Read(ref _blocksAccepted[g]).ToString(inv)).Append('\n');
            sb.Append("arc_miner_blocks_submitted_total{gpu=\"").Append(g).Append("\",result=\"rejected\"} ")
              .Append(Volatile.Read(ref _blocksRejected[g]).ToString(inv)).Append('\n');
        }

        Gauge(sb, "arc_miner_iters_per_second",  "Per-worker iterations per second (gauge).", _itersPerSec);
        Gauge(sb, "arc_miner_tmads_per_second",  "Per-worker TMADs/s (gauge).",                _tmadsPerSec);
        Gauge(sb, "arc_miner_hashes_per_second", "Per-worker hashes/s (gauge, tiles*DAF).",    _hashesPerSec);
        Gauge(sb, "arc_miner_expected_opens_per_second", "Per-worker expected opens/s at current adjusted target.", _expectedOpensPerSec);
        Gauge(sb, "arc_miner_tiles_per_second",  "Per-worker CTA output tiles/s (diagnostic; target-normalized opens track TMADs/s).", _tilesPerSec);
        Gauge(sb, "arc_miner_iter_ms",           "Per-worker mean iteration latency (ms).",    _iterMs);
        Gauge(sb, "arc_miner_sigma_rotation_latest_ms", "Latest worker-observed sigma rotation wall time from job observation to first new batch queued.", _sigmaRotationLatestMs);
        Gauge(sb, "arc_miner_sigma_rotation_max_ms", "Maximum worker-observed sigma rotation wall time in this process.", _sigmaRotationMaxMs);
        Gauge(sb, "arc_miner_sigma_rotation_drain_ms", "Latest old-batch drain time before sigma install.", _sigmaRotationDrainMs);
        Gauge(sb, "arc_miner_sigma_rotation_install_ms", "Latest sigma install time excluding old-batch drain and first queue.", _sigmaRotationInstallMs);
        Gauge(sb, "arc_miner_sigma_rotation_b_merkle_ms", "Latest B Merkle handle build time during sigma install.", _sigmaRotationBMerkleMs);
        Gauge(sb, "arc_miner_sigma_rotation_lost_iters", "Latest sigma rotation time expressed as mean iterations lost.", _sigmaRotationLostIters);
        Gauge(sb, "arc_miner_sigma_rotation_bseed_changed", "1 if the latest sigma rotation changed BSeed, else 0.", _sigmaRotationBSeedChanged);

        sb.Append("# HELP arc_miner_block_finds_total Shares that the pool flagged is_block_find=true.\n");
        sb.Append("# TYPE arc_miner_block_finds_total counter\n");
        sb.Append("arc_miner_block_finds_total ").Append(Volatile.Read(ref _blockFinds).ToString(inv)).Append('\n');

        if (_heartbeatTicks.Length > 0)
        {
            sb.Append("# HELP arc_miner_heartbeat_age_seconds Wall seconds since worker last ticked.\n");
            sb.Append("# TYPE arc_miner_heartbeat_age_seconds gauge\n");
            long nowTicks = DateTime.UtcNow.Ticks;
            for (int g = 0; g < _gpuCount; g++)
            {
                long hb = Interlocked.Read(ref _heartbeatTicks[g]);
                double ageSec = hb == 0 ? 0.0 : (nowTicks - hb) / (double)TimeSpan.TicksPerSecond;
                sb.Append("arc_miner_heartbeat_age_seconds{gpu=\"").Append(g).Append("\"} ")
                  .Append(ageSec.ToString("F3", inv)).Append('\n');
            }
        }

        sb.Append("# HELP arc_miner_pool_connected 1 if the gRPC MiningStream is currently open, 0 otherwise.\n");
        sb.Append("# TYPE arc_miner_pool_connected gauge\n");
        sb.Append("arc_miner_pool_connected ").Append(Interlocked.Read(ref _poolConnected).ToString(inv)).Append('\n');

        sb.Append("# HELP arc_miner_pool_latency_ms Last Ping/Pong round-trip time in milliseconds.\n");
        sb.Append("# TYPE arc_miner_pool_latency_ms gauge\n");
        double rtt = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _poolLatencyMsBits));
        sb.Append("arc_miner_pool_latency_ms ")
          .Append(double.IsFinite(rtt) ? rtt.ToString("F3", inv) : "0").Append('\n');

        return sb.ToString();
    }

    private static void Counter(StringBuilder sb, string name, string help, long[] arr)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" counter\n");
        for (int g = 0; g < arr.Length; g++)
            sb.Append(name).Append("{gpu=\"").Append(g).Append("\"} ")
              .Append(Volatile.Read(ref arr[g]).ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void Gauge(StringBuilder sb, string name, string help, long[] bitsArr)
    {
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
        sb.Append("# TYPE ").Append(name).Append(" gauge\n");
        for (int g = 0; g < bitsArr.Length; g++)
        {
            double v = BitConverter.Int64BitsToDouble(Volatile.Read(ref bitsArr[g]));
            sb.Append(name).Append("{gpu=\"").Append(g).Append("\"} ")
              .Append(double.IsFinite(v) ? v.ToString("G", CultureInfo.InvariantCulture) : "0").Append('\n');
        }
    }
}
