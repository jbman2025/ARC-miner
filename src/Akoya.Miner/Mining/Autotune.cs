// Autotune — finds the best per-SKU kernel config on the local Intel Arc GPU(s)
// by sweeping the tgemm tuning knobs (NB / MB / SEARCH_M) through the same
// GpuWorker.RunBenchmark the miner uses at startup, then caching the winner.
//
// No telemetry: results are printed (screenshot-friendly for community
// hashrate DBs) and written to a local cache next to the session file. The
// mining path can later auto-apply the cached profile for zero-config tuning.
//
// All three knobs are read fresh by the SYCL kernel on each launch
// (AKOYA_TGEMM_NB / _MB via getenv per dispatch; AKOYA_SEARCH_M at
// workspace_alloc, and RunBenchmark allocates a fresh workspace per call), so
// the whole sweep runs in-process with no rebuild.

using System.Text;
using Akoya.Cuda;
using Akoya.Crypto;
using Akoya.Miner.Config;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Mining;

internal static class Autotune
{

    private readonly record struct Config(int Nb, int Mb, int SearchM)
    {
        public override string ToString() => $"NB={Nb} MB={Mb} SEARCH_M={SearchM}";
    }

    private readonly record struct Sample(Config Cfg, double Tmads, double IterMs, bool Ok);

    // ── built-in tuned defaults ───────────────────────────────────────────────
    // Per-SKU optima we've already characterized, so users never wait on autotune
    // for a known card. Alchemist (sg8) is register-bound with a tiny L2 → a small
    // SEARCH_M window; confirmed on A750 (3.8 TH/s @ 256/NB2/MB2). A770/A580/A380
    // share the die + small-window regime, seeded from it (re-tune to refine).
    // Battlemage B580/B570 (sg16) want the 4096 L2-resident window (B580 34.8 TH/s).
    // B70 (BMG-G31) has a big L2 and peaks higher — intentionally absent so it
    // autotunes. Keyed by the model token in the GPU name (A750, B580, …).
    private static readonly Dictionary<string, Config> SkuDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A770"] = new Config(2, 2, 256),
        ["A750"] = new Config(2, 2, 256),
        ["A580"] = new Config(2, 2, 256),
        ["A380"] = new Config(2, 2, 256),
        ["B580"] = new Config(4, 2, 4096),
        ["B570"] = new Config(4, 2, 4096),
    };

    // Extract the Arc model token (A750 / B580 / …) from a device name.
    private static string? ModelToken(string? gpuName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(gpuName ?? "", @"\b([AB]\d{2,3})\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    // "acm" (Alchemist / sg8) | "bmg" (Battlemage / sg16) | "" (unknown).
    private static string ArcFamily(string? gpuName)
    {
        var t = ModelToken(gpuName);
        return t is null ? "" : (t[0] == 'A' ? "acm" : "bmg");
    }

    private static Config? SkuDefaultFor(string? gpuName)
        => ModelToken(gpuName) is { } tok && SkuDefaults.TryGetValue(tok, out var c) ? c : null;

    /// <summary>The effective tuned config for a SKU: the autotune cache if
    /// present, else the built-in default, else null (truly unknown card).
    /// Single source of truth for both the first-run hook and the mine-path
    /// apply step.</summary>
    internal static (int Nb, int Mb, int SearchM)? ResolveTunedConfig(string cachePath, string sku)
    {
        if (TuneCache.Lookup(cachePath, sku) is { } t) return t;
        if (SkuDefaultFor(sku) is { } d) return (d.Nb, d.Mb, d.SearchM);
        return null;
    }

    private static bool HasFlag(string[] args, string flag)
        => Array.IndexOf(args, flag) >= 0;

    public static int Run(string[] args, ILoggerFactory lf, CancellationToken ct)
    {
        var log = lf.CreateLogger("autotune");
        int durSec = Math.Max(4, ArgInt(args, "--autotune-duration", 12));

        List<WorkerOrchestrator.GpuInfo> gpus;
        try { gpus = EnumerateGpus(); }
        catch (Exception e) { log.LogError("autotune: GPU enumeration failed — {Err}", e.Message); return 1; }
        if (gpus.Count == 0) { log.LogError("autotune: no GPUs found"); return 1; }

        // Build the exact mine profile production uses (canonical shape + the
        // per-Arc kernel knobs SyclKSub/BM/BN). EnvVarBindings.Load requires a
        // wallet; autotune never connects to a pool, so seed a placeholder.
        if (MinerEnv.Get("AKOYA_POOL_WALLET") is null)
            Environment.SetEnvironmentVariable("AKOYA_POOL_WALLET", "prl1autotune");
        MinerOptions opts;
        try { opts = EnvVarBindings.Load(log); }
        catch (Exception e) { log.LogError("autotune: config load failed — {Err}", e.Message); return 1; }
        var profile = WorkerOrchestrator.ApplyGpuProfileDefaults(
            opts.Mine, gpus, opts.Mine.ShapeOverridePresent, out var profileName);

        // Remember any knobs the user set explicitly so we restore them after
        // the sweep instead of clobbering their environment.
        var savedNb = Environment.GetEnvironmentVariable("AKOYA_TGEMM_NB");
        var savedMb = Environment.GetEnvironmentVariable("AKOYA_TGEMM_MB");
        var savedSm = Environment.GetEnvironmentVariable("AKOYA_SEARCH_M");

        // Arch-aware bounds. sg8 (Alchemist) is register-bound with a tiny L2 →
        // its optimum is a SMALL window, so start the probe low and cap the ladder
        // low: never measure the catastrophically slow large windows (4096 ≈ 16s/
        // iter on an A750, and the up-climb to 8192+ risks the Windows TDR). sg16
        // (Battlemage) wants the 4096 L2-resident window; B70 (BMG-G31) peaks
        // higher and its DefaultMaxSearchM cap lets the climb reach it.
        string family = ArcFamily(gpus[0].Name);
        bool deep = HasFlag(args, "--autotune-deep");
        int defMaxSm = family == "acm" ? 2048 : DefaultMaxSearchM;
        int defProbe = family == "acm" ? 256  : ProbeSearchM;
        int maxSm = Math.Max(64, ArgInt(args, "--autotune-max-search-m", defMaxSm));
        int probe = AlignSm(Math.Min(defProbe, maxSm), profile.M);
        if (deep)
            log.LogInformation("autotune: DEEP mode — full NB·MB·SEARCH_M grid (≤{Max}); characterization sweep, slower.", maxSm);
        var winners = new List<(WorkerOrchestrator.GpuInfo Gpu, Sample Best, List<Sample> All)>();

        try
        {
            foreach (var gpu in gpus)
            {
                if (ct.IsCancellationRequested) break;
                var samples = new List<Sample>();

                Sample Measure(Config cfg)
                {
                    Apply(cfg);
                    try
                    {
                        var b = GpuWorker.RunBenchmark(
                            gpu.Ordinal, profile, TimeSpan.FromSeconds(durSec), log, ct);
                        var s = new Sample(cfg, b.TmadsPerSec, b.IterMs, Ok: true);
                        log.LogInformation("  {Cfg}: {T:F1} TMADs/s  iter={I:F1}ms", cfg, b.TmadsPerSec, b.IterMs);
                        samples.Add(s);
                        return s;
                    }
                    catch (Exception e)
                    {
                        var s = new Sample(cfg, 0, 0, Ok: false);
                        log.LogWarning("  {Cfg}: failed ({Err})", cfg, e.Message);
                        samples.Add(s);
                        return s;
                    }
                }

                if (deep)
                {
                    // DEEP / characterization: exhaustive NB·MB·SEARCH_M grid over
                    // the arch-appropriate region — no adaptive pruning, so the full
                    // landscape lands in the report. The GRF axis is covered
                    // implicitly: MB=2 ⇒ the kernel's RM>=2 path carries
                    // grf_size<256> (large GRF); MB=1 ⇒ 128-GRF. This CONFIRMS the
                    // max within the runtime-tunable space; it can't beat the
                    // adaptive winner on the current kernel.
                    var rungs = SearchMLadder.Where(x => x <= maxSm)
                                             .Select(x => AlignSm(x, profile.M))
                                             .Distinct().ToArray();
                    log.LogInformation(
                        "autotune: GPU{Ord} \"{Name}\" — DEEP grid: {N} SEARCH_M rungs × MB{{1,2}} × NB{{1,2,4}} ({Sec}s each)",
                        gpu.Ordinal, gpu.Name, rungs.Length, durSec);
                    foreach (var sm in rungs)
                        foreach (var mb in new[] { 1, 2 })
                            foreach (var nb in new[] { 1, 2, 4 })
                            {
                                if (ct.IsCancellationRequested) break;
                                Measure(new Config(nb, mb, sm));
                            }
                    if (!samples.Any(s => s.Ok)) { log.LogWarning("autotune: GPU{Ord} produced no valid samples", gpu.Ordinal); continue; }
                }
                else
                {
                    // Phase 1 — rank NB/MB at the probe window. These register/reuse
                    // levers are ~orthogonal to SEARCH_M (verified on B580: NB=4 MB=2
                    // led at every window), so ranking them once is enough.
                    log.LogInformation(
                        "autotune: GPU{Ord} \"{Name}\" — phase 1/2: NB·MB @ SEARCH_M={Sm} ({Sec}s each)",
                        gpu.Ordinal, gpu.Name, probe, durSec);
                    foreach (var mb in new[] { 1, 2 })
                        foreach (var nb in new[] { 2, 4 })
                        {
                            if (ct.IsCancellationRequested) break;
                            Measure(new Config(nb, mb, probe));
                        }

                    if (!samples.Any(s => s.Ok)) { log.LogWarning("autotune: GPU{Ord} produced no valid samples", gpu.Ordinal); continue; }
                    var lead = PickBest(samples);
                    int pnb = lead.Cfg.Nb, pmb = lead.Cfg.Mb;

                    // Phase 2 — climb SEARCH_M from the probe in BOTH directions with
                    // the winning NB/MB. The L2-residency optimum sits below the probe
                    // on small-L2 parts (A-series) and well above it on big-L2 parts
                    // (Pro B70 / BMG-G31), so we walk up AND down, stopping each
                    // direction at the cliff (throughput drops >DropTol below that
                    // direction's running peak), the TDR guard, or an alloc failure.
                    log.LogInformation("autotune: GPU{Ord} — phase 2/2: climbing SEARCH_M from {P} with NB={Nb} MB={Mb}",
                        gpu.Ordinal, probe, pnb, pmb);
                    var above = SearchMLadder.Where(x => x > probe).OrderBy(x => x).ToArray();
                    var below = SearchMLadder.Where(x => x < probe).OrderByDescending(x => x).ToArray();
                    foreach (var dir in new[] { above, below })
                    {
                        double peak = lead.Tmads;
                        foreach (var smRaw in dir)
                        {
                            if (ct.IsCancellationRequested) break;
                            if (smRaw > maxSm) break;                       // up-direction cap (never trips going down)
                            int sm = AlignSm(smRaw, profile.M);
                            if (samples.Any(s => s.Cfg.SearchM == sm && s.Cfg.Nb == pnb && s.Cfg.Mb == pmb)) continue;
                            var s = Measure(new Config(pnb, pmb, sm));
                            if (!s.Ok) break;                               // OOM / alloc cap → stop this direction
                            if (s.IterMs > TdrGuardMs) break;               // nearing the ~2s Windows TDR
                            if (s.Tmads < peak * (1.0 - DropTol)) break;    // past the L2 cliff
                            if (s.Tmads > peak) peak = s.Tmads;
                        }
                    }
                }

                winners.Add((gpu, PickBest(samples), samples));
            }
        }
        finally
        {
            Restore("AKOYA_TGEMM_NB", savedNb);
            Restore("AKOYA_TGEMM_MB", savedMb);
            Restore("AKOYA_SEARCH_M", savedSm);
        }

        if (winners.Count == 0) { log.LogError("autotune: no GPU produced a result"); return 1; }

        PrintReport(winners, profileName, profile);

        var cachePath = TuneCache.PathFor(opts.Session.FilePath);
        try
        {
            TuneCache.Write(cachePath, winners.Select(w =>
                (w.Gpu.Name, w.Best.Cfg.Nb, w.Best.Cfg.Mb, w.Best.Cfg.SearchM, w.Best.Tmads)));
            Console.WriteLine($"  Cached to {cachePath}");
        }
        catch (Exception e) { log.LogWarning("autotune: could not write cache — {Err}", e.Message); }

        return 0;
    }

    /// <summary>
    /// Zero-config tuning hook for the mine path. If this GPU has no cached tune
    /// profile yet, run the autotune sweep once (caching the result) BEFORE
    /// mining, so a first run — A-series especially — does not mine at the
    /// B-series default window (~25× slower). On later launches the cache hit
    /// makes this a no-op and the mine path applies the cached profile.
    ///
    /// Opt out with <c>--no-autotune</c> / <c>AKOYA_AUTOTUNE_ON_FIRST_RUN=0</c>;
    /// skipped if the user pinned any kernel knob (NB/MB/SEARCH_M). Never fatal —
    /// a sweep failure just falls through to mining with defaults.
    /// </summary>
    public static void EnsureTunedOrSweep(
        string[] args, MinerOptions opts, ILoggerFactory lf, CancellationToken ct)
    {
        var log = lf.CreateLogger("autotune");

        if (MinerEnv.Get("AKOYA_AUTOTUNE_ON_FIRST_RUN") == "0")
            return;
        if (!string.IsNullOrEmpty(MinerEnv.Get("AKOYA_TGEMM_NB"))
            || !string.IsNullOrEmpty(MinerEnv.Get("AKOYA_TGEMM_MB"))
            || !string.IsNullOrEmpty(MinerEnv.Get("AKOYA_SEARCH_M")))
        {
            log.LogInformation("autotune: kernel knobs set manually — skipping first-run autotune");
            return;
        }

        List<WorkerOrchestrator.GpuInfo> gpus;
        try { gpus = EnumerateGpus(); }
        catch { return; }                  // non-Arc / enumeration issue → skip silently
        if (gpus.Count == 0) return;

        var cachePath = TuneCache.PathFor(opts.Session.FilePath);
        var sku = gpus[0].Name;

        // Cache wins; then the built-in per-SKU default (known cards mine
        // optimally with NO sweep — no autotune wait). Only a truly unknown card
        // falls through to the one-time sweep. The mine path's ApplyTunedProfile
        // resolves the same way, so we don't need to seed the cache here.
        if (TuneCache.Lookup(cachePath, sku) is { } hit)
        {
            log.LogInformation(
                "autotune: cached profile for \"{Sku}\" — NB={Nb} MB={Mb} SEARCH_M={Sm} "
                + "(run `arc-miner autotune` to re-tune)",
                sku, hit.Nb, hit.Mb, hit.SearchM);
            return;
        }
        if (SkuDefaultFor(sku) is { } baked)
        {
            log.LogInformation(
                "autotune: built-in tuned default for \"{Sku}\" — {Cfg} (no sweep needed; "
                + "run `arc-miner autotune` to re-tune for your exact card)",
                sku, baked);
            return;
        }

        log.LogInformation(
            "autotune: no tuned profile for \"{Sku}\" yet — running a one-time autotune sweep before mining "
            + "(cached for next launch; untuned A-series cards can be ~25× slower). Disable with --no-autotune.",
            sku);
        try { Run(args, lf, ct); }
        catch (OperationCanceledException) { throw; }   // honour Ctrl-C / shutdown
        catch (Exception e)
        {
            log.LogWarning("autotune: first-run sweep failed ({Err}) — mining with defaults", e.Message);
        }
    }

    // ── adaptive search ──────────────────────────────────────────────────────
    private const int ProbeSearchM = 4096;        // window used to rank NB/MB
    private const int DefaultMaxSearchM = 32768;  // up-climb cap (--autotune-max-search-m overrides)
    private const double DropTol = 0.04;          // >4% below a direction's peak = past the cliff
    private const double TdrGuardMs = 1500;       // stop before the ~2s Windows TDR
    // Ascending SEARCH_M ladder the climb walks (both directions from the probe).
    // Tiny rungs (32-256) matter A LOT on register-bound A-series sg8: the A750
    // keeps climbing all the way down — 1024→1.45, 512→2.4, 256→3.7 TH/s — so
    // its peak is a VERY small window (tiny L2 + sg8 register pressure want a hot
    // working set). The eventual floor is the fixed per-iter overhead (lcg fill +
    // tensor_hash run over the FULL A matrix, independent of window), so the cliff
    // guard finds where shrinking stops paying. High rungs cover Big-Battlemage
    // (BMG-G31). B-series break at their cliff long before the tiny rungs.
    private static readonly int[] SearchMLadder = { 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };

    // Highest measured throughput wins. On parts where MB/NB matter (register-
    // bound A-series, Pro B70) the gap is large, so the raw max is unambiguous;
    // where they don't (B580, MB=1≈MB=2 within sampling jitter) either is equally
    // optimal, so we don't over-engineer a tie-break that would muddy the report.
    private static Sample PickBest(IEnumerable<Sample> samples)
        => samples.Where(s => s.Ok).OrderByDescending(s => s.Tmads).First();

    // Tile-align to 16 and clamp to [16, m] — matches the C-side compute_search_m.
    private static int AlignSm(int sm, int m)
    {
        if (sm > m) sm = m;
        sm = (sm / 16) * 16;
        if (sm < 16) sm = 16;
        return sm;
    }

    private static void Apply(Config c)
    {
        NativeEnv.Set("AKOYA_TGEMM_NB", c.Nb.ToString());
        NativeEnv.Set("AKOYA_TGEMM_MB", c.Mb.ToString());
        NativeEnv.Set("AKOYA_SEARCH_M", c.SearchM.ToString());
    }

    // Restore the user's original value, or truly delete it.
    private static void Restore(string name, string? saved)
        => NativeEnv.Set(name, saved);

    // ── reporting ───────────────────────────────────────────────────────────
    private static void PrintReport(
        List<(WorkerOrchestrator.GpuInfo Gpu, Sample Best, List<Sample> All)> winners,
        string profileName, MineOptions shape)
    {
        const string bar = "══════════════════════════════════════════════════════════════";
        const string mid = "──────────────────────────────────────────────────────────────";
        foreach (var (gpu, best, all) in winners)
        {
            Console.WriteLine();
            Console.WriteLine(bar);
            Console.WriteLine($"  ARC-miner autotune — {gpu.Name}");
            Console.WriteLine($"  profile={profileName}  shape=M{shape.M} N{shape.N} K{shape.K} R{shape.NoiseRank}");
            Console.WriteLine(mid);
            Console.WriteLine("  NB  MB  SEARCH_M   TMADs/s   iter_ms   vs best");
            foreach (var s in all.OrderByDescending(x => x.Ok).ThenByDescending(x => x.Tmads))
            {
                if (!s.Ok)
                {
                    Console.WriteLine($"  {s.Cfg.Nb,2}  {s.Cfg.Mb,2}  {s.Cfg.SearchM,8}      failed");
                    continue;
                }
                string tag = s.Cfg.Equals(best.Cfg)
                    ? "  best"
                    : $"{(s.Tmads - best.Tmads) / best.Tmads * 100,6:F1}%";
                Console.WriteLine(
                    $"  {s.Cfg.Nb,2}  {s.Cfg.Mb,2}  {s.Cfg.SearchM,8}   {s.Tmads,7:F1}   {s.IterMs,7:F1}   {tag}");
            }
            Console.WriteLine(mid);
            Console.WriteLine($"  ✓ Best: {best.Cfg}  →  {best.Tmads:F1} TMADs/s  (iter {best.IterMs:F1}ms)");
            Console.WriteLine("  Apply by adding to your start script (.bat):");
            Console.WriteLine(
                $"     set \"AKOYA_TGEMM_NB={best.Cfg.Nb}\" & set \"AKOYA_TGEMM_MB={best.Cfg.Mb}\" & set \"AKOYA_SEARCH_M={best.Cfg.SearchM}\"");
            Console.WriteLine(bar);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static List<WorkerOrchestrator.GpuInfo> EnumerateGpus()
    {
        CudaDriver.Check(CudaDriver.Init(0), "cuInit");
        CudaDriver.Check(CudaDriver.DeviceGetCount(out var n), "cuDeviceGetCount");
        var list = new List<WorkerOrchestrator.GpuInfo>(n);
        Span<byte> nameBuf = stackalloc byte[128];
        for (int ord = 0; ord < n; ord++)
        {
            CudaDriver.Check(CudaDriver.DeviceGet(out var dev, ord), "cuDeviceGet");
            nameBuf.Clear();
            CudaDriver.Check(CudaDriver.DeviceGetName(nameBuf, nameBuf.Length, dev), "cuDeviceGetName");
            CudaDriver.Check(CudaDriver.DeviceComputeCapability(out var major, out var minor, dev),
                "cuDeviceComputeCapability");
            int len = nameBuf.IndexOf((byte)0); if (len < 0) len = nameBuf.Length;
            var name = Encoding.UTF8.GetString(nameBuf[..len]);
            var uuid = $"{Environment.MachineName}:{ord}:{name}";
            list.Add(new WorkerOrchestrator.GpuInfo(ord, name, uuid, major, minor));
        }
        return list;
    }

    private static int ArgInt(string[] args, string flag, int def)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag && int.TryParse(args[i + 1], out var v)) return v;
        return def;
    }

    // ── per-SKU cache ─────────────────────────────────────────────────────────
    // Flat, pipe-delimited, one line per GPU model. Deliberately not JSON: the
    // miner is NativeAOT (no reflection-based deserializer), and a 6-field
    // record needs no ceremony. GPU marketing names never contain '|'.
    internal static class TuneCache
    {
        private const string Header =
            "# arc-miner autotune cache v1 | sku|nb|mb|search_m|tmads|version|utc";

        public static string PathFor(string sessionFilePath)
        {
            var dir = Path.GetDirectoryName(sessionFilePath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            return Path.Combine(dir, "arc-tune.conf");
        }

        public static void Write(string path, IEnumerable<(string Sku, int Nb, int Mb, int SearchM, double Tmads)> rows)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Merge with any existing rows so tuning one GPU doesn't drop the
            // cached profiles of others on a multi-card rig.
            var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith('#') || line.Length == 0) continue;
                    int bar = line.IndexOf('|');
                    if (bar > 0) byKey[line[..bar]] = line;
                }

            var ver = Observability.VersionInfo.MinerVersion;
            var utc = DateTime.UtcNow.ToString("o");
            foreach (var (sku, nb, mb, searchM, tmads) in rows)
                byKey[sku] = $"{sku}|{nb}|{mb}|{searchM}|{tmads:F1}|{ver}|{utc}";

            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var v in byKey.Values) sb.AppendLine(v);
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Look up a cached (NB, MB, SEARCH_M) for a GPU model name.
        /// Returns null if absent. Used by the mining path to auto-apply.</summary>
        public static (int Nb, int Mb, int SearchM)? Lookup(string path, string sku)
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith('#') || line.Length == 0) continue;
                var f = line.Split('|');
                if (f.Length >= 4 && f[0] == sku
                    && int.TryParse(f[1], out var nb)
                    && int.TryParse(f[2], out var mb)
                    && int.TryParse(f[3], out var sm))
                    return (nb, mb, sm);
            }
            return null;
        }
    }
}
