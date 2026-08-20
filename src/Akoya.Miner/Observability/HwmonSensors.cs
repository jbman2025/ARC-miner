using System.Globalization;

namespace Akoya.Miner.Observability;

/// <summary>
/// Per-GPU temperature / fan / power, read from Linux sysfs hwmon.
///
/// The Intel <c>xe</c> driver publishes one hwmon node per card, keyed by PCI
/// address, readable WITHOUT root (unlike the render nodes needed for compute).
/// Measured on a 2x Arc B580 rig, kernel 6.18:
///
///   temp2_input  label "pkg"   GPU package, millidegrees C
///   temp3_input  label "vram"  VRAM, millidegrees C
///   fan1_input                 RPM (0 at idle — these cards stop their fans)
///   energy1_input label "card" monotonic microjoule counter
///   power1_cap / power1_crit   205 W / 410 W on B580
///
/// There is deliberately no instantaneous-power file on this driver, so power is
/// differentiated from the energy counter between samples — which is why this
/// type holds state instead of being a pile of static file reads.
///
/// Linux + xe/i915 only. Everything degrades to null elsewhere (Windows has no
/// equivalent short of IGCL), and every read is best-effort: a sensor that
/// vanishes mid-run must never do anything worse than blank a column.
/// </summary>
internal sealed class HwmonSensors
{
    /// <summary>All values nullable: absent means "not published by this
    /// driver" or "not yet derivable", never zero. A zero temperature would be
    /// a lie; a blank column is honest.</summary>
    internal readonly record struct Reading(
        double? PkgTempC, double? VramTempC, int? FanRpm, double? PowerW);

    private sealed class Node
    {
        public string Dir = "";
        public string Pci = "";
        public string? PkgTempFile;
        public string? VramTempFile;
        public string? FanFile;
        public string? EnergyFile;
        // Previous energy sample, for differentiation.
        public long PrevEnergyMicroJ = -1;
        public long PrevTicks;
        public double? LastPowerW;
    }

    private readonly string _root;
    private readonly string _raplRoot;
    private readonly Func<long> _ticks;
    private readonly List<Node> _nodes = new();
    private readonly object _gate = new();
    private long _lastSampleTicks;

    // CPU package: temperature from coretemp/k10temp hwmon, power from RAPL.
    private string? _cpuTempFile;
    private string? _raplEnergyFile;
    private long _raplMaxRangeMicroJ;
    private readonly Node _cpuNode = new();

    /// <summary>Minimum gap between real file reads. The panel redraws about
    /// once a second and the JSON API can be scraped far harder than that;
    /// re-reading sysfs per caller would be pointless syscall traffic, and too
    /// short a window makes the differentiated power figure jumpy.</summary>
    private static readonly long MinSampleTicks = TimeSpan.TicksPerSecond;

    public const string DefaultRoot = "/sys/class/hwmon";
    public const string DefaultRaplRoot = "/sys/class/powercap";

    public HwmonSensors(string root = DefaultRoot, Func<long>? ticks = null,
                        string raplRoot = DefaultRaplRoot)
    {
        _root = root;
        _raplRoot = raplRoot;
        _ticks = ticks ?? (() => DateTime.UtcNow.Ticks);
        Discover();
        DiscoverCpu();
    }

    /// <summary>True when at least one GPU hwmon node was found. False on
    /// Windows, on a kernel without the driver's hwmon support, and in any
    /// container that does not bind-mount /sys.</summary>
    public bool Available => _nodes.Count > 0;

    /// <summary>True when a CPU package temperature or RAPL energy domain was
    /// found.</summary>
    public bool CpuAvailable => _cpuTempFile is not null || _raplEnergyFile is not null;

    /// <summary>PCI addresses we can report on, e.g. "0000:05:00.0".</summary>
    public IReadOnlyList<string> PciAddresses
    {
        get
        {
            var outp = new List<string>(_nodes.Count);
            foreach (var n in _nodes) outp.Add(n.Pci);
            return outp;
        }
    }

    private void Discover()
    {
        // Guard the whole scan: an unreadable or absent /sys is the normal case
        // off Linux and must be silent, not an exception on a hot path.
        try
        {
            // The OS gate applies only to the real sysfs path. An explicitly
            // supplied root is honoured anywhere, so the parsing and the power
            // differentiation are testable on the machine we develop on rather
            // than only on a rig.
            if (string.Equals(_root, DefaultRoot, StringComparison.Ordinal)
                && !OperatingSystem.IsLinux()) return;
            if (!Directory.Exists(_root)) return;

            foreach (var dir in Directory.GetDirectories(_root))
            {
                var name = ReadText(Path.Combine(dir, "name"));
                // i915 publishes a similar (not identical) node; accept it and
                // let the per-file probing below decide what is actually there.
                if (name is not ("xe" or "i915")) continue;

                var pci = ReadPciAddress(dir);
                if (string.IsNullOrEmpty(pci)) continue;

                var node = new Node { Dir = dir, Pci = pci };

                // Sensor numbering is not stable across drivers or revisions —
                // temp1 is the package on one and something else on another — so
                // match on the *label* rather than assuming temp2 == pkg.
                for (int i = 1; i <= 8; i++)
                {
                    var label = ReadText(Path.Combine(dir, $"temp{i}_label"));
                    if (label is null) continue;
                    var input = Path.Combine(dir, $"temp{i}_input");
                    if (!File.Exists(input)) continue;
                    if (label.Equals("pkg", StringComparison.OrdinalIgnoreCase)) node.PkgTempFile = input;
                    else if (label.Equals("vram", StringComparison.OrdinalIgnoreCase)) node.VramTempFile = input;
                }
                // Fall back to temp1 when the driver publishes no labels at all.
                if (node.PkgTempFile is null)
                {
                    var t1 = Path.Combine(dir, "temp1_input");
                    if (File.Exists(t1)) node.PkgTempFile = t1;
                }

                var fan = Path.Combine(dir, "fan1_input");
                if (File.Exists(fan)) node.FanFile = fan;

                for (int i = 1; i <= 4; i++)
                {
                    var label = ReadText(Path.Combine(dir, $"energy{i}_label"));
                    var input = Path.Combine(dir, $"energy{i}_input");
                    if (!File.Exists(input)) continue;
                    // "card" is whole-board draw, which is what an operator
                    // compares against the PSU; "pkg" excludes VRAM and fans.
                    if (label is null || label.Equals("card", StringComparison.OrdinalIgnoreCase))
                    {
                        node.EnergyFile = input;
                        break;
                    }
                    node.EnergyFile ??= input;
                }

                _nodes.Add(node);
            }

            // Stable, reproducible order so any ordinal-based fallback mapping
            // is at least deterministic between runs.
            _nodes.Sort((a, b) => string.CompareOrdinal(a.Pci, b.Pci));
        }
        catch (Exception) { /* no sensors is a fine outcome */ }
    }

    /// <summary>Find the CPU package temperature and the RAPL package energy
    /// domain. Separate from GPU discovery because they come from two different
    /// subsystems: coretemp/k10temp under hwmon, but power under powercap.</summary>
    private void DiscoverCpu()
    {
        try
        {
            // Same gate as Discover: on Windows the default roots resolve to
            // C:\sys\... and would be pointlessly probed. An explicitly supplied
            // root is still honoured anywhere, which is what makes this testable
            // off Linux.
            if (string.Equals(_root, DefaultRoot, StringComparison.Ordinal)
                && !OperatingSystem.IsLinux()) return;

            if (Directory.Exists(_root))
            {
                foreach (var dir in Directory.GetDirectories(_root))
                {
                    var name = ReadText(Path.Combine(dir, "name"));
                    // coretemp = Intel, k10temp/zenpower = AMD.
                    if (name is not ("coretemp" or "k10temp" or "zenpower")) continue;

                    // Prefer the package sensor over an individual core: "Package
                    // id 0" on Intel, "Tctl"/"Tdie" on AMD. A single core's
                    // temperature is noisier and lower than the package.
                    for (int i = 1; i <= 16 && _cpuTempFile is null; i++)
                    {
                        var label = ReadText(Path.Combine(dir, $"temp{i}_label"));
                        var input = Path.Combine(dir, $"temp{i}_input");
                        if (!File.Exists(input)) continue;
                        if (label is null
                            || label.StartsWith("Package", StringComparison.OrdinalIgnoreCase)
                            || label.Equals("Tctl", StringComparison.OrdinalIgnoreCase)
                            || label.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            _cpuTempFile = input;
                        }
                    }
                    if (_cpuTempFile is not null) break;
                }
            }

            // RAPL: /sys/class/powercap/intel-rapl:0 with name "package-0".
            // NOTE energy_uj is root-only on modern kernels (a side-channel
            // mitigation), so an unprivileged miner gets no CPU power. That is a
            // permission fact, not a missing sensor — it must blank the field,
            // not be reported as zero watts.
            if (Directory.Exists(_raplRoot))
            {
                foreach (var dir in Directory.GetDirectories(_raplRoot))
                {
                    var name = ReadText(Path.Combine(dir, "name"));
                    if (name is null || !name.StartsWith("package-", StringComparison.OrdinalIgnoreCase)) continue;
                    var energy = Path.Combine(dir, "energy_uj");
                    if (!File.Exists(energy)) continue;
                    _raplEnergyFile = energy;
                    _raplMaxRangeMicroJ = ReadLong(Path.Combine(dir, "max_energy_range_uj")) ?? 0;
                    break;
                }
            }
        }
        catch (Exception) { /* no CPU sensors is a fine outcome */ }
    }

    /// <summary>CPU package temperature and power. Both nullable; RAPL power is
    /// null unless the process can read energy_uj (root).</summary>
    public Reading SampleCpu()
    {
        lock (_gate)
        {
            long now = _ticks();
            if (_cpuCache is not null && now - _lastCpuTicks < MinSampleTicks) return _cpuCache.Value;

            double? watts = null;
            if (_raplEnergyFile is not null)
            {
                _cpuNode.EnergyFile = _raplEnergyFile;
                watts = ReadPower(_cpuNode, now, _raplMaxRangeMicroJ);
            }
            var r = new Reading(ReadMilli(_cpuTempFile), null, null, watts);
            _lastCpuTicks = now;
            _cpuCache = r;
            return r;
        }
    }

    private Reading? _cpuCache;
    private long _lastCpuTicks;

    // /sys/class/hwmon/hwmonN/device/uevent carries PCI_SLOT_NAME=0000:05:00.0.
    private static string ReadPciAddress(string hwmonDir)
    {
        var uevent = ReadText(Path.Combine(hwmonDir, "device", "uevent"));
        if (uevent is null) return "";
        foreach (var line in uevent.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("PCI_SLOT_NAME=", StringComparison.Ordinal))
                return t["PCI_SLOT_NAME=".Length..].Trim();
        }
        return "";
    }

    /// <summary>Read every node, no more often than <see cref="MinSampleTicks"/>.
    /// Returns readings keyed by PCI address.</summary>
    public IReadOnlyDictionary<string, Reading> SampleAll()
    {
        lock (_gate)
        {
            long now = _ticks();
            if (_cache is not null && now - _lastSampleTicks < MinSampleTicks) return _cache;

            var outp = new Dictionary<string, Reading>(_nodes.Count, StringComparer.Ordinal);
            foreach (var n in _nodes)
            {
                outp[n.Pci] = new Reading(
                    ReadMilli(n.PkgTempFile),
                    ReadMilli(n.VramTempFile),
                    ReadInt(n.FanFile),
                    ReadPower(n, now));
            }
            _lastSampleTicks = now;
            _cache = outp;
            return outp;
        }
    }

    private Dictionary<string, Reading>? _cache;

    /// <summary>Differentiate the monotonic energy counter into watts.</summary>
    /// <param name="wrapRangeMicroJ">Counter modulus, when the source publishes
    /// one (RAPL's max_energy_range_uj). RAPL wraps roughly every 44 minutes at
    /// 100 W, so a wrap there is routine and worth correcting for rather than
    /// discarding — unlike the GPU counter, which has no published range and
    /// only goes backwards if the driver reloaded.</param>
    private static double? ReadPower(Node n, long nowTicks, long wrapRangeMicroJ = 0)
    {
        var micro = ReadLong(n.EnergyFile);
        if (micro is null) return null;

        long prev = n.PrevEnergyMicroJ;
        long prevTicks = n.PrevTicks;
        n.PrevEnergyMicroJ = micro.Value;
        n.PrevTicks = nowTicks;

        // First sample establishes the baseline; there is nothing to divide yet.
        if (prev < 0) return null;

        long dE = micro.Value - prev;
        long dT = nowTicks - prevTicks;
        if (dT <= 0) return n.LastPowerW;
        // Recover a known-modulus wrap; otherwise a negative delta means the
        // driver reset the counter, so re-baseline and report nothing rather
        // than a nonsense spike.
        if (dE < 0 && wrapRangeMicroJ > 0) dE += wrapRangeMicroJ;
        if (dE < 0) return n.LastPowerW = null;

        double seconds = (double)dT / TimeSpan.TicksPerSecond;
        double watts = (dE / 1e6) / seconds;   // microjoules → joules → J/s
        // Clamp obvious nonsense (a stalled counter that jumps, a suspended box)
        // rather than printing a four-digit wattage next to a 205 W card.
        if (!double.IsFinite(watts) || watts < 0 || watts > 2000) return n.LastPowerW = null;
        return n.LastPowerW = watts;
    }

    private static double? ReadMilli(string? path)
    {
        var v = ReadLong(path);
        return v is null ? null : v.Value / 1000.0;
    }

    private static int? ReadInt(string? path)
    {
        var v = ReadLong(path);
        return v is null ? null : (int)v.Value;
    }

    private static long? ReadLong(string? path)
    {
        var s = ReadText(path);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ReadText(string? path)
    {
        if (path is null) return null;
        try { return File.ReadAllText(path).Trim(); }
        catch { return null; }   // sensor removed, permissions, transient EIO
    }
}
