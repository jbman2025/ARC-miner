using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Akoya.Miner.Config;
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
    private static long   _blockHeight;
    private static long   _cpuBlockHeight;
    private static long   _poolConnected;
    private static long   _cpuPoolConnected;
    private static long   _poolLatencyMsBits;

    // Session metadata for the JSON stats API (set once at startup; the
    // strings are replaced atomically so no locking is needed).
    private static string   _poolUrl   = "";
    private static string   _cpuPoolUrl = "";
    private static string   _workerName = "";
    // Reported in the JSON stats "algorithm" field. Defaults to pearl so the
    // schema is unchanged when the PRL algo (or nothing) sets it; btx/csd set
    // their own name at startup. Volatile string swap — no lock needed.
    private static volatile string _algorithm = "pearl";
    private static string[] _gpuNames  = Array.Empty<string>();
    private static long     _startedUtcTicks;

    private static int    _gpuCount;
    private static int    _cpuIndex = -1;
    // Guards device-slot registration (Init / InitCpu). A dedicated object, not
    // _gpuNames — that field is reassigned during registration, so locking on it
    // would let two callers hold different array instances and race.
    private static readonly object _deviceLock = new();
    public static int     CpuIndex => _cpuIndex;
    private static HttpListener? _listener;
    private static Thread? _serverThread;

    // ─── Control API (write) state ─────────────────────────────────────────
    // Enabled only when a password is configured (ARC_API_PASSWORD /
    // --api-password). Write endpoints are additionally loopback-only, so the
    // read-only stats API can stay LAN-visible while control cannot.
    private static string? _apiPassword;
    private static Action? _onRestart;
    private static ILogger? _log;

    /// <summary>Set true by the control handler once a change is saved; Program
    /// reads it after graceful shutdown to relaunch with the new config.</summary>
    public static volatile bool RestartRequested;

    /// <summary>Wire up the write API. A null/empty password leaves control
    /// disabled (endpoints return 403). <paramref name="onRestart"/> is invoked
    /// after a change is persisted to request a graceful shutdown + relaunch.</summary>
    public static void ConfigureControl(string? password, Action onRestart)
    {
        _apiPassword = string.IsNullOrEmpty(password) ? null : password;
        _onRestart   = onRestart;
    }

    /// <summary>Ask the host for a graceful shutdown-and-relaunch. Used by the
    /// control API, and by the rank-penalty fork when it discovers mid-session
    /// that the process was built with the wrong noise rank — the mining shape
    /// is fixed once worker buffers are allocated, so a relaunch is the only way
    /// to change it.
    ///
    /// Returns false when no host hook is wired, so a caller can fall back to
    /// telling the operator rather than assuming a restart is coming.</summary>
    public static bool RequestRestart()
    {
        if (_onRestart is null) return false;
        RestartRequested = true;
        _onRestart.Invoke();
        return true;
    }

    private static bool ControlEnabled => _apiPassword is not null;

    public static void SetSessionInfo(string poolUrl, string workerName)
    {
        _poolUrl    = poolUrl;
        _workerName = workerName;
    }

    public static void SetCpuSessionInfo(string poolUrl, string workerName)
    {
        _cpuPoolUrl = poolUrl;
        _workerName = workerName;
    }

    /// <summary>Set the algorithm name reported by the JSON stats API. Called
    /// once at startup from the resolved algo module ("pearl", "btx", "csd").</summary>
    public static void SetAlgorithm(string algorithm)
    {
        if (!string.IsNullOrEmpty(algorithm)) _algorithm = algorithm;
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

    /// <summary>Name the GPU devices. Preserves a CPU slot registered by
    /// <see cref="InitCpu"/> (dual-mining): a blind assignment here would size the
    /// array back to the GPU count, dropping the CPU row's name and leaving
    /// <c>_cpuIndex</c> past the end.</summary>
    public static void SetGpuNames(string[] names)
    {
        lock (_deviceLock)
        {
            if (_cpuIndex < 0)
            {
                _gpuNames = names;
                return;
            }

            var merged = new string[Math.Max(_gpuCount, names.Length + 1)];
            Array.Copy(names, merged, Math.Min(names.Length, merged.Length));
            if (_cpuIndex < merged.Length)
            {
                var old = _gpuNames;
                merged[_cpuIndex] = _cpuIndex < old.Length ? old[_cpuIndex] : "CPU";
            }
            _gpuNames = merged;
        }
    }

    // Per-GPU formatted difficulty string (e.g. "500.0K") for the dashboard.
    private static string[] _gpuDiff = Array.Empty<string>();
    public static void SetDiff(int gpu, string diff)
    {
        var a = _gpuDiff;
        if ((uint)gpu < (uint)a.Length) a[gpu] = diff;
    }

    /// <summary>Register the GPU devices. Safe to call after <see cref="InitCpu"/>:
    /// when dual-mining (gr+prl, rx+btx, …) the CPU and GPU algos start
    /// concurrently, so this may run second. If a CPU slot already exists it is
    /// relocated to sit after the GPUs rather than left dangling at an index a
    /// GPU now owns — otherwise the two would write over each other's stats.</summary>
    public static void Init(int gpuCount, long[] heartbeats)
    {
        lock (_deviceLock)
        {
            string? cpuName = _cpuIndex >= 0 && _cpuIndex < _gpuNames.Length ? _gpuNames[_cpuIndex] : null;
            InitCore(gpuCount, heartbeats);

            if (cpuName is not null)
            {
                // Blank the name array first so the old CPU-slot string can't bleed
                // into a GPU index; SetGpuNames fills these in right after.
                var blank = new string[gpuCount];
                Array.Fill(blank, "");
                _gpuNames = blank;

                _cpuIndex = -1;      // let InitCpuCore append a fresh slot after the GPUs
                InitCpuCore(cpuName);
            }
        }
    }

    private static void InitCore(int gpuCount, long[] heartbeats)
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
        _gpuDiff          = new string[gpuCount]; Array.Fill(_gpuDiff, "");
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

    public static void InitCpu(int threads, string cpuName)
    {
        lock (_deviceLock)
        {
            if (_cpuIndex >= 0) return; // already registered CPU
            InitCpuCore(cpuName);
        }
    }

    // Caller must hold _deviceLock.
    private static void InitCpuCore(string cpuName)
    {
            Interlocked.CompareExchange(ref _startedUtcTicks, DateTime.UtcNow.Ticks, 0);

            int oldSize = _gpuCount;
            int newSize = oldSize + 1;
            _cpuIndex = oldSize;

            Expand(ref _iters, newSize);
            Expand(ref _triggers, newSize);
            Expand(ref _blocksAccepted, newSize);
            Expand(ref _blocksRejected, newSize);
            Expand(ref _itersPerSec, newSize);
            Expand(ref _tmadsPerSec, newSize);
            Expand(ref _hashesPerSec, newSize);
            Expand(ref _tilesPerSec, newSize);
            Expand(ref _expectedOpensPerSec, newSize);
            Expand(ref _iterMs, newSize);
            Expand(ref _gpuDiff, newSize, defaultValue: "");
            Expand(ref _sigmaRotations, newSize);
            Expand(ref _sigmaRotationLatestMs, newSize);
            Expand(ref _sigmaRotationMaxMs, newSize);
            Expand(ref _sigmaRotationDrainMs, newSize);
            Expand(ref _sigmaRotationInstallMs, newSize);
            Expand(ref _sigmaRotationBMerkleMs, newSize);
            Expand(ref _sigmaRotationLostIters, newSize);
            Expand(ref _sigmaRotationBSeedChanged, newSize);

            var oldHeartbeats = _heartbeatTicks;
            var newHeartbeats = new long[newSize];
            Array.Copy(oldHeartbeats, newHeartbeats, oldHeartbeats.Length);
            _heartbeatTicks = newHeartbeats;

            var oldNames = _gpuNames;
            var newNames = new string[newSize];
            Array.Copy(oldNames, newNames, oldNames.Length);
            newNames[newSize - 1] = cpuName;
            _gpuNames = newNames;

            _gpuCount = newSize;
    }

    private static void Expand<T>(ref T[] array, int newSize, T? defaultValue = default)
    {
        var newArray = new T[newSize];
        if (array.Length > 0)
        {
            Array.Copy(array, newArray, Math.Min(array.Length, newSize));
        }
        if (defaultValue is not null && !defaultValue.Equals(default(T)))
        {
            for (int i = array.Length; i < newSize; i++) newArray[i] = defaultValue;
        }
        array = newArray;
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

    /// <summary>
    /// Report throughput for a slot. <paramref name="hashesPerSec"/> is the
    /// only value the dashboard's headline number reads.
    ///
    /// PEARL-ONLY PARAMETERS: tmadsPerSec, tilesPerSec and expectedOpensPerSec
    /// describe the tgemm proof-of-work and are written only by
    /// Algos/Prl/GpuWorker. Every other algo passes zeros — use
    /// <see cref="SetHashRate"/> instead of spelling those zeros out.
    /// </summary>
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

    /// <summary>
    /// PEARL-ONLY. Sigma rotation is a tgemm-PoW concept; this is called only
    /// from Algos/Prl/GpuWorker and feeds the eight _sigmaRotation* slots.
    /// </summary>
    /// <summary>
    /// Throughput for an algo that just has a hash rate — which is every algo
    /// except Pearl. Exists so that call sites stop reading
    /// `SetThroughput(slot, hs, 0, hs, 0)`, where the zeros are Pearl's tgemm
    /// counters and the duplicated `hs` is easy to get wrong.
    /// </summary>
    public static void SetHashRate(int slot, double hashesPerSec, double iterMs = 0.0)
        => SetThroughput(slot, hashesPerSec, 0, hashesPerSec, iterMs);

    /// <summary>
    /// PEARL-ONLY. Sigma rotation is a tgemm-PoW concept; this is called only
    /// from Algos/Prl/GpuWorker and feeds the eight _sigmaRotation* slots.
    /// </summary>
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

    public static void SetCpuPoolConnected(bool connected)
        => Interlocked.Exchange(ref _cpuPoolConnected, connected ? 1L : 0L);

    /// <summary>Latest block height seen on a job. Both transports carry it
    /// (stratum mining.notify and the V2 JobAssignment), and both funnel through
    /// the orchestrator's job handler, so this is set in exactly one place.</summary>
    public static void SetBlockHeight(long height)
    {
        if (height > 0) Interlocked.Exchange(ref _blockHeight, height);
    }

    /// <summary>Latest GPU-chain height seen this process, or 0 before the
    /// first job.</summary>
    public static long BlockHeight => Interlocked.Read(ref _blockHeight);

    private static long _prlHeight;
    private static long _prlActive;

    /// <summary>Mark this process as mining Pearl. Gates the fork counter, which
    /// must NOT key off a height alone: <see cref="Algos.Prl.PrlHeightStore.BestKnown"/>
    /// falls back to the persisted last-height file, and that file is Pearl's — so
    /// an rx or btx run would inherit a previous Pearl session's fork count and
    /// display it against a chain that has never forked.</summary>
    public static void MarkPrlActive() => Interlocked.Exchange(ref _prlActive, 1L);

    /// <summary>Latest height on the PEARL chain specifically. Separate from
    /// <see cref="BlockHeight"/>, which every GPU algo writes — a btx or rx height
    /// counted against Pearl's fork schedule would be meaningless.</summary>
    public static void SetPrlBlockHeight(long height)
    {
        if (height > 0) Interlocked.Exchange(ref _prlHeight, height);
    }

    /// <summary>Known Pearl forks this rig has mined past, or 0 when we are not
    /// on Pearl / have not seen a job yet. See <see cref="Algos.Prl.PrlForks"/> —
    /// this is a floor, not a total. The persisted-height fallback is what lets
    /// the count survive the first seconds of a cold start, before any job.</summary>
    public static int PrlForksCrossed
        => Interlocked.Read(ref _prlActive) == 0
            ? 0
            : Algos.Prl.PrlForks.CrossedAt(
                  Algos.Prl.PrlHeightStore.BestKnown(Interlocked.Read(ref _prlHeight)));

    /// <summary>Height on the CPU algo's chain. Kept separate from the GPU one:</summary>
    /// when dual mining the two halves follow DIFFERENT chains, and a single
    /// last-writer-wins field showed the Monero height while the GPU party was
    /// mining Pearl — the CPU side simply pulled jobs more often and kept
    /// overwriting it.</summary>
    public static void SetCpuBlockHeight(long height)
    {
        if (height > 0) Interlocked.Exchange(ref _cpuBlockHeight, height);
    }

    // ─── GPU sensors (Linux sysfs hwmon) ───────────────────────────────────
    // Per-slot PCI address, which is how hwmon keys its nodes. An empty entry
    // and the shim's "0000:00:00.0" placeholder both mean "no usable mapping".
    private static string[] _gpuPci = Array.Empty<string>();
    private static readonly HwmonSensors _sensors = new();

    private const string PlaceholderPci = "0000:00:00.0";

    /// <summary>Record which physical card occupies a metrics slot.</summary>
    public static void SetGpuPciAddress(int slot, string pciAddress)
    {
        if (slot < 0 || string.IsNullOrEmpty(pciAddress)) return;
        lock (_deviceLock)
        {
            if (_gpuPci.Length <= slot)
            {
                var grown = new string[slot + 1];
                Array.Copy(_gpuPci, grown, _gpuPci.Length);
                for (int i = 0; i < grown.Length; i++) grown[i] ??= "";
                _gpuPci = grown;
            }
            _gpuPci[slot] = pciAddress;
        }
    }

    /// <summary>Sensor readings for a slot, or a blank reading. Deliberately
    /// returns nothing unless the slot's PCI address is known AND matches a
    /// discovered node — a guessed mapping would attribute one card's
    /// temperature to another, which is worse than showing no temperature.</summary>
    private static HwmonSensors.Reading SensorsFor(
        int slot, IReadOnlyDictionary<string, HwmonSensors.Reading> sample)
    {
        var pci = _gpuPci;
        if (slot >= pci.Length) return default;
        var addr = pci[slot];
        if (string.IsNullOrEmpty(addr) || addr == PlaceholderPci) return default;
        return sample.TryGetValue(addr, out var r) ? r : default;
    }

    public static void SetPoolLatencyMs(double ms)
        => Interlocked.Exchange(ref _poolLatencyMsBits, BitConverter.DoubleToInt64Bits(ms));

    public static double GetPoolLatencyMs()
    {
        var v = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _poolLatencyMsBits));
        return double.IsFinite(v) ? v : 0.0;
    }

    public static bool IsPoolConnected => Interlocked.Read(ref _poolConnected) == 1L;
    public static bool IsCpuPoolConnected => Interlocked.Read(ref _cpuPoolConnected) == 1L;

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

        _log = log;
        _serverThread = new Thread(() => ServeLoop(log, ct)) { IsBackground = true, Name = "metrics-http" };
        _serverThread.Start();
        log.LogInformation(
            "metrics: stats API on {Bound} — JSON http://localhost:{Port}/api/stats, Prometheus /metrics",
            bound, port);
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
    /// <param name="TempC">GPU package temperature, or null when the platform
    /// publishes no sensor (Windows, or a card we could not map). Null means
    /// "unknown" and must render blank — never 0, which would read as a
    /// suspiciously cold card.</param>
    public readonly record struct DashGpu(
        int Id, string Name, double HashesPerSec, double IterMs,
        long Accepted, long Rejected, double HeartbeatAgeSec, string Diff, bool IsCpu,
        double? TempC = null, double? PowerW = null, int? FanRpm = null);

    /// <param name="TotalHashesPerSec">GPU + CPU, kept for the JSON API's
    /// existing single-number field.</param>
    /// <param name="GpuHashesPerSec">GPU rows only.</param>
    /// <param name="CpuHashesPerSec">The CPU row, or 0 when not dual-mining.
    /// Split out because the two halves run different algorithms when dual
    /// mining, so a single summed figure compares H/s that aren't comparable.</param>
    public readonly record struct DashSnapshot(
        string PoolUrl, string CpuPoolUrl, string Worker, bool Connected, bool CpuConnected, double LatencyMs,
        long Accepted, long Rejected, long BlockFinds,
        double TotalHashesPerSec, double GpuHashesPerSec, double CpuHashesPerSec,
        long BlockHeight, long CpuBlockHeight, int PrlForks, string? PoolInfoJson, DashGpu[] Gpus);

    public static DashSnapshot GetDashboardSnapshot()
    {
        var hashesArr = _hashesPerSec; var iterArr = _iterMs;
        var accArr = _blocksAccepted; var rejArr = _blocksRejected;
        var hbArr = _heartbeatTicks;  var names = _gpuNames;
        var diffArr = _gpuDiff;
        int n = Math.Min(hashesArr.Length,
                Math.Min(iterArr.Length, Math.Min(accArr.Length, rejArr.Length)));

        long nowTicks = DateTime.UtcNow.Ticks;
        int cpuIdx = _cpuIndex;
        // Self-throttled: repeated snapshots inside the sample window reuse the
        // last reading rather than re-hitting sysfs per caller.
        var sensors = _sensors.Available
            ? _sensors.SampleAll()
            : (IReadOnlyDictionary<string, HwmonSensors.Reading>)
                  System.Collections.Immutable.ImmutableDictionary<string, HwmonSensors.Reading>.Empty;
        var cpuSensors = _sensors.CpuAvailable ? _sensors.SampleCpu() : default;
        var rows = new DashGpu[n];
        double gpuHs = 0, cpuHs = 0;
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
            string diff = g < diffArr.Length ? (diffArr[g] ?? "") : "";
            bool isCpu = g == cpuIdx;
            // The CPU row gets the CPU package sensors (coretemp + RAPL), never
            // a neighbouring card's numbers.
            var sn = isCpu ? cpuSensors : SensorsFor(g, sensors);
            rows[g] = new DashGpu(g, name, hs, ms, a, r, hbAge, diff, isCpu,
                                  sn.PkgTempC, sn.PowerW, sn.FanRpm);
            if (isCpu) cpuHs += hs; else gpuHs += hs;
            acc += a; rej += r;
        }

        double totalHs = gpuHs + cpuHs;
        return new DashSnapshot(
            _poolUrl, _cpuPoolUrl, _workerName, IsPoolConnected, IsCpuPoolConnected, GetPoolLatencyMs(),
            acc, rej, Interlocked.Read(ref _blockFinds),
            double.IsFinite(totalHs) ? totalHs : 0.0,
            double.IsFinite(gpuHs) ? gpuHs : 0.0,
            double.IsFinite(cpuHs) ? cpuHs : 0.0,
            Interlocked.Read(ref _blockHeight), Interlocked.Read(ref _cpuBlockHeight),
            PrlForksCrossed, _poolInfoJson, rows);
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

                // JSON-only API: "/" always serves the stats JSON, for pollers
                // (Kryptex etc.) and browsers alike.
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
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.ContentLength64 = body.Length;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/api/control/status")
                {
                    // Loopback-only capability probe so a client knows whether
                    // control is available. No auth: reveals only a boolean.
                    if (!ctx.Request.IsLocal) { ctx.Response.StatusCode = 403; }
                    else
                    {
                        var body = Encoding.UTF8.GetBytes(
                            "{\"enabled\":" + (ControlEnabled ? "true" : "false") + "}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-store";
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                    }
                }
                else if (path == "/api/control/config")
                {
                    HandleControlConfig(ctx, log);
                }
                else if (path == "/favicon.ico")
                {
                    // No HTML page is served any more (the API is JSON-only), but
                    // a browser pointed at it still probes for a favicon. Answer
                    // 204 so it isn't a 404 in the log.
                    ctx.Response.StatusCode = 204;
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

    // ─── Control API handler ───────────────────────────────────────────────
    private static void WriteJson(HttpListenerContext ctx, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.OutputStream.Write(body, 0, body.Length);
    }

    private static bool PasswordMatches(string? supplied)
    {
        var expected = _apiPassword;
        if (expected is null || supplied is null) return false;
        var a = Encoding.UTF8.GetBytes(supplied);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>POST /api/control/config — change pool/wallet/worker/algo and
    /// request a restart. Guarded three ways: loopback-only, control must be
    /// enabled (password configured), and the X-Arc-Auth header must match the
    /// password (constant-time). The custom auth header also blocks cross-origin
    /// browser CSRF: it forces a CORS preflight we never approve.</summary>
    private static void HandleControlConfig(HttpListenerContext ctx, ILogger log)
    {
        if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        { WriteJson(ctx, 405, "{\"ok\":false,\"error\":\"POST required\"}"); return; }

        if (!ctx.Request.IsLocal)
        { WriteJson(ctx, 403, "{\"ok\":false,\"error\":\"control is localhost-only\"}"); return; }

        if (!ControlEnabled)
        { WriteJson(ctx, 403, "{\"ok\":false,\"error\":\"control disabled — launch with --api-password to enable\"}"); return; }

        if (!PasswordMatches(ctx.Request.Headers["X-Arc-Auth"]))
        { WriteJson(ctx, 401, "{\"ok\":false,\"error\":\"invalid password\"}"); return; }

        // Read a bounded body (config is tiny; cap defends against a wedged
        // sender streaming forever).
        string bodyText;
        try
        {
            long len = ctx.Request.ContentLength64;
            if (len > 8192) { WriteJson(ctx, 413, "{\"ok\":false,\"error\":\"body too large\"}"); return; }
            using var sr = new StreamReader(ctx.Request.InputStream,
                ctx.Request.ContentEncoding ?? Encoding.UTF8);
            bodyText = sr.ReadToEnd();
        }
        catch { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"unreadable body\"}"); return; }

        var updates = new ControlConfig();
        string? algoDisplay = null;
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"expected a JSON object\"}"); return; }

            if (root.TryGetProperty("algo", out var a) && a.ValueKind == JsonValueKind.String)
            {
                var raw = (a.GetString() ?? "").Trim().ToLowerInvariant();
                var norm = raw == "pearl" ? "prl" : raw;
                if (norm is not ("prl" or "csd"))
                { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"algo must be prl or csd\"}"); return; }
                updates.Algo = norm;
                algoDisplay = norm;
            }
            if (root.TryGetProperty("pool_host", out var h) && h.ValueKind == JsonValueKind.String)
            {
                var host = (h.GetString() ?? "").Trim();
                if (host.Length == 0) { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"pool_host is empty\"}"); return; }
                updates.PoolHost = host;
            }
            if (root.TryGetProperty("pool_port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pv))
            {
                if (pv < 1 || pv > 65535) { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"pool_port out of range\"}"); return; }
                updates.PoolPort = pv;
            }
            if (root.TryGetProperty("use_tls", out var t) && (t.ValueKind == JsonValueKind.True || t.ValueKind == JsonValueKind.False))
                updates.UseTls = t.GetBoolean();
            if (root.TryGetProperty("wallet", out var w) && w.ValueKind == JsonValueKind.String)
            {
                var wallet = (w.GetString() ?? "").Trim();
                if (wallet.Length == 0) { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"wallet is empty\"}"); return; }
                updates.Wallet = wallet;
            }
            if (root.TryGetProperty("worker", out var n) && n.ValueKind == JsonValueKind.String)
            {
                var worker = (n.GetString() ?? "").Trim();
                if (worker.Length == 0) { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"worker is empty\"}"); return; }
                updates.Worker = worker;
            }
        }
        catch { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"invalid JSON\"}"); return; }

        bool any = updates.PoolHost is not null || updates.PoolPort is not null
                   || updates.UseTls is not null || updates.Wallet is not null
                   || updates.Worker is not null || updates.Algo is not null;
        if (!any) { WriteJson(ctx, 400, "{\"ok\":false,\"error\":\"no recognized fields to change\"}"); return; }

        try { ControlConfig.Merge(updates); }
        catch (Exception e)
        {
            log.LogError("control: failed to persist config — {Err}", e.Message);
            WriteJson(ctx, 500, "{\"ok\":false,\"error\":\"failed to save config\"}");
            return;
        }

        // Log WHICH fields changed, but never the wallet value.
        var changed = new List<string>(6);
        if (updates.Algo     is not null) changed.Add("algo=" + algoDisplay);
        if (updates.PoolHost is not null) changed.Add("pool_host");
        if (updates.PoolPort is not null) changed.Add("pool_port");
        if (updates.UseTls   is not null) changed.Add("use_tls");
        if (updates.Wallet   is not null) changed.Add("wallet");
        if (updates.Worker   is not null) changed.Add("worker");
        log.LogWarning("control: config changed via control API ({Fields}) — restarting to apply", string.Join(", ", changed));

        WriteJson(ctx, 200, "{\"ok\":true,\"restarting\":true}");

        // Flush the response before we pull the pipeline down, then request the
        // graceful shutdown + relaunch. Order matters: the client must get its
        // 200 before the listener closes.
        try { ctx.Response.OutputStream.Flush(); ctx.Response.Close(); } catch { }
        RestartRequested = true;
        _onRestart?.Invoke();
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
        sb.Append("\"algorithm\":\"").Append(Esc(_algorithm)).Append("\",");
        sb.Append("\"uptime_seconds\":").Append(uptimeSec.ToString("F0", inv)).Append(',');
        sb.Append("\"pool\":{");
        sb.Append("\"url\":\"").Append(Esc(_poolUrl)).Append("\",");
        sb.Append("\"worker\":\"").Append(Esc(_workerName)).Append("\",");
        sb.Append("\"connected\":").Append(IsPoolConnected ? "true" : "false").Append(',');
        sb.Append("\"latency_ms\":").Append(GetPoolLatencyMs().ToString("F1", inv));
        sb.Append("},");
        // "pool" above is the GPU algo's pool. CPU algos (rx, gr) mine their own
        // pool — the terminal dashboard shows both rows, so surface the CPU one
        // here too. Additive: only emitted once a CPU algo has registered, so the
        // payload is unchanged for GPU-only runs.
        var cpuPoolUrl = _cpuPoolUrl;
        if (!string.IsNullOrEmpty(cpuPoolUrl))
        {
            sb.Append("\"cpu_pool\":{");
            sb.Append("\"url\":\"").Append(Esc(cpuPoolUrl)).Append("\",");
            sb.Append("\"worker\":\"").Append(Esc(_workerName)).Append("\",");
            sb.Append("\"connected\":").Append(IsCpuPoolConnected ? "true" : "false");
            sb.Append("},");
        }
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
