using Akoya.Miner.Observability;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// The collector is pointed at a fake /sys tree built to mirror what a real
/// 2x Arc B580 rig publishes (kernel 6.18, xe driver) — labels, units and file
/// names copied from the live rig, including the detail that matters most:
/// there is no instantaneous-power file, only a monotonic energy counter.
/// </summary>
public sealed class HwmonSensorsTests : IDisposable
{
    private readonly string _root;
    private readonly string _raplRoot;

    public HwmonSensorsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arc-hwmon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _raplRoot = _root + "-rapl";
        Directory.CreateDirectory(_raplRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_raplRoot, recursive: true); } catch { }
    }

    private string AddNode(string hwmon, string name, string pci,
                           long pkgMilliC = 45_000, long vramMilliC = 50_000,
                           long fanRpm = 1200, long energyMicroJ = 120_198_547_119)
    {
        var dir = Path.Combine(_root, hwmon);
        Directory.CreateDirectory(Path.Combine(dir, "device"));
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");
        File.WriteAllText(Path.Combine(dir, "device", "uevent"),
            $"DRIVER=xe\nPCI_CLASS=30000\nPCI_ID=8086:E20B\nPCI_SLOT_NAME={pci}\n");
        // Real rig numbering: temp2=pkg, temp3=vram. Deliberately NOT temp1, so
        // a collector that assumed temp1 would fail this.
        File.WriteAllText(Path.Combine(dir, "temp2_label"), "pkg\n");
        File.WriteAllText(Path.Combine(dir, "temp2_input"), pkgMilliC + "\n");
        File.WriteAllText(Path.Combine(dir, "temp3_label"), "vram\n");
        File.WriteAllText(Path.Combine(dir, "temp3_input"), vramMilliC + "\n");
        File.WriteAllText(Path.Combine(dir, "fan1_input"), fanRpm + "\n");
        File.WriteAllText(Path.Combine(dir, "energy1_label"), "card\n");
        File.WriteAllText(Path.Combine(dir, "energy1_input"), energyMicroJ + "\n");
        File.WriteAllText(Path.Combine(dir, "energy2_label"), "pkg\n");
        File.WriteAllText(Path.Combine(dir, "energy2_input"), (energyMicroJ / 2) + "\n");
        File.WriteAllText(Path.Combine(dir, "power1_cap"), "205000000\n");
        return dir;
    }

    private static void SetEnergy(string dir, long microJ)
        => File.WriteAllText(Path.Combine(dir, "energy1_input"), microJ + "\n");

    // A fake clock, so the power derivation is tested deterministically rather
    // than by sleeping.
    private sealed class Clock
    {
        public long Ticks;
        public void Advance(double seconds) => Ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ReadsTemperaturesAndFanKeyedByPciAddress()
    {
        AddNode("hwmon1", "xe", "0000:05:00.0", pkgMilliC: 62_000, vramMilliC: 58_000, fanRpm: 1450);
        AddNode("hwmon2", "xe", "0000:0c:00.0", pkgMilliC: 25_000, vramMilliC: 26_000, fanRpm: 0);
        // A CPU sensor that must be ignored — the real rig has coretemp at hwmon0.
        var cpu = Path.Combine(_root, "hwmon0");
        Directory.CreateDirectory(cpu);
        File.WriteAllText(Path.Combine(cpu, "name"), "coretemp\n");

        var s = new HwmonSensors(_root);
        Assert.True(s.Available);
        Assert.Equal(new[] { "0000:05:00.0", "0000:0c:00.0" }, s.PciAddresses);

        var all = s.SampleAll();
        Assert.Equal(62.0, all["0000:05:00.0"].PkgTempC);
        Assert.Equal(58.0, all["0000:05:00.0"].VramTempC);
        Assert.Equal(1450, all["0000:05:00.0"].FanRpm);

        // Zero RPM is a real reading on these cards (fans stop at idle), not a
        // missing sensor — it must survive as 0, not become null.
        Assert.Equal(0, all["0000:0c:00.0"].FanRpm);
        Assert.Equal(25.0, all["0000:0c:00.0"].PkgTempC);
    }

    [Fact]
    public void PowerIsDifferentiatedFromTheEnergyCounter()
    {
        var dir = AddNode("hwmon1", "xe", "0000:05:00.0", energyMicroJ: 1_000_000_000);
        var clock = new Clock();
        var s = new HwmonSensors(_root, () => clock.Ticks);

        // First sample only establishes a baseline — there is nothing to divide.
        Assert.Null(s.SampleAll()["0000:05:00.0"].PowerW);

        // +150 J over 1 s = 150 W.
        clock.Advance(1.0);
        SetEnergy(dir, 1_000_000_000 + 150_000_000);
        Assert.Equal(150.0, s.SampleAll()["0000:05:00.0"].PowerW!.Value, precision: 3);

        // +9 J over 3 s = 3 W (idle-ish, like the rig at rest).
        clock.Advance(3.0);
        SetEnergy(dir, 1_000_000_000 + 150_000_000 + 9_000_000);
        Assert.Equal(3.0, s.SampleAll()["0000:05:00.0"].PowerW!.Value, precision: 3);
    }

    [Fact]
    public void ACounterResetReportsNothingRatherThanANonsenseSpike()
    {
        var dir = AddNode("hwmon1", "xe", "0000:05:00.0", energyMicroJ: 5_000_000_000);
        var clock = new Clock();
        var s = new HwmonSensors(_root, () => clock.Ticks);
        s.SampleAll();

        clock.Advance(1.0);
        SetEnergy(dir, 1_000);            // wrapped / driver reload
        Assert.Null(s.SampleAll()["0000:05:00.0"].PowerW);

        // ...and it re-baselines rather than staying broken.
        clock.Advance(1.0);
        SetEnergy(dir, 1_000 + 100_000_000);
        Assert.Equal(100.0, s.SampleAll()["0000:05:00.0"].PowerW!.Value, precision: 3);
    }

    [Fact]
    public void RepeatedCallsInsideTheWindowDoNotRereadTheFiles()
    {
        var dir = AddNode("hwmon1", "xe", "0000:05:00.0", pkgMilliC: 40_000);
        var clock = new Clock();
        var s = new HwmonSensors(_root, () => clock.Ticks);
        s.SampleAll();

        // Change the file but do not advance the clock: the cached value stands.
        File.WriteAllText(Path.Combine(dir, "temp2_input"), "90000\n");
        Assert.Equal(40.0, s.SampleAll()["0000:05:00.0"].PkgTempC);

        clock.Advance(1.5);
        Assert.Equal(90.0, s.SampleAll()["0000:05:00.0"].PkgTempC);
    }

    [Fact]
    public void MissingOrUnreadableSensorsBlankTheFieldInsteadOfThrowing()
    {
        var dir = AddNode("hwmon1", "xe", "0000:05:00.0");
        File.Delete(Path.Combine(dir, "fan1_input"));
        File.Delete(Path.Combine(dir, "temp3_input"));
        File.WriteAllText(Path.Combine(dir, "temp2_input"), "not-a-number\n");

        var s = new HwmonSensors(_root);
        var r = s.SampleAll()["0000:05:00.0"];
        Assert.Null(r.FanRpm);
        Assert.Null(r.VramTempC);
        Assert.Null(r.PkgTempC);   // garbage parses to null, never 0
    }

    private string AddCpuNodes(long pkgMilliC = 63_000, long raplMicroJ = 1_000_000_000,
                               long wrapAt = 262_143_328_850)
    {
        // coretemp: temp1 is the package, temp2+ are individual cores. The
        // package is what an operator watches, and a single core reads lower —
        // so a collector that grabbed temp1 blindly would be right here by luck
        // and wrong on the GPU nodes, where temp1 does not exist at all.
        var dir = Path.Combine(_root, "hwmon0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), "coretemp\n");
        File.WriteAllText(Path.Combine(dir, "temp1_label"), "Package id 0\n");
        File.WriteAllText(Path.Combine(dir, "temp1_input"), pkgMilliC + "\n");
        File.WriteAllText(Path.Combine(dir, "temp2_label"), "Core 0\n");
        File.WriteAllText(Path.Combine(dir, "temp2_input"), (pkgMilliC - 5000) + "\n");

        // Real path is "intel-rapl:0", but a colon is not a legal Windows filename
        // and discovery keys off the `name` file, not the directory name.
        var rapl = Path.Combine(_raplRoot, "intel-rapl-0");
        Directory.CreateDirectory(rapl);
        File.WriteAllText(Path.Combine(rapl, "name"), "package-0\n");
        File.WriteAllText(Path.Combine(rapl, "energy_uj"), raplMicroJ + "\n");
        File.WriteAllText(Path.Combine(rapl, "max_energy_range_uj"), wrapAt + "\n");
        return rapl;
    }

    [Fact]
    public void ReadsCpuPackageTemperatureAndRaplPower()
    {
        var rapl = AddCpuNodes(pkgMilliC: 63_000, raplMicroJ: 1_000_000_000);
        var clock = new Clock();
        var s = new HwmonSensors(_root, () => clock.Ticks, _raplRoot);

        Assert.True(s.CpuAvailable);
        var first = s.SampleCpu();
        Assert.Equal(63.0, first.PkgTempC);       // package, not the cooler core
        Assert.Null(first.PowerW);                // baseline only

        clock.Advance(2.0);
        File.WriteAllText(Path.Combine(rapl, "energy_uj"), (1_000_000_000L + 190_000_000L) + "\n");
        Assert.Equal(95.0, s.SampleCpu().PowerW!.Value, precision: 3);
    }

    [Fact]
    public void RaplCounterWrapIsCorrectedNotDiscarded()
    {
        // RAPL wraps roughly every 44 minutes at 100 W, so a wrap is routine and
        // must not blank the reading the way an unexplained GPU counter reset does.
        const long Wrap = 262_143_328_850;
        var rapl = AddCpuNodes(raplMicroJ: Wrap - 50_000_000, wrapAt: Wrap);
        var clock = new Clock();
        var s = new HwmonSensors(_root, () => clock.Ticks, _raplRoot);
        s.SampleCpu();

        clock.Advance(1.0);
        File.WriteAllText(Path.Combine(rapl, "energy_uj"), 30_000_000L + "\n");   // wrapped
        Assert.Equal(80.0, s.SampleCpu().PowerW!.Value, precision: 3);
    }

    [Fact]
    public void UnreadableRaplBlanksCpuPowerButKeepsTemperature()
    {
        // energy_uj is root-only on modern kernels (a side-channel mitigation),
        // so an unprivileged miner gets temperature but no watts. That is a
        // permission fact, not zero watts.
        AddCpuNodes();
        File.Delete(Path.Combine(_raplRoot, "intel-rapl-0", "energy_uj"));

        var s = new HwmonSensors(_root, null, _raplRoot);
        var r = s.SampleCpu();
        Assert.Equal(63.0, r.PkgTempC);
        Assert.Null(r.PowerW);
    }

    [Fact]
    public void NoGpuNodesMeansUnavailableNotAnException()
    {
        var s = new HwmonSensors(Path.Combine(_root, "does-not-exist"));
        Assert.False(s.Available);
        Assert.Empty(s.SampleAll());
    }
}
