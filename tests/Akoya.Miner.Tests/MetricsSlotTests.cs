using System.Reflection;
using Akoya.Miner.Observability;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Metrics is process-global static state, so each test must start from a clean
/// slate. Resetting every static array field back to empty (plus the two
/// counters) reproduces the type's initial state without making the production
/// fields visible just for testing.
/// </summary>
internal static class MetricsReset
{
    public static void Reset()
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var f in typeof(Metrics).GetFields(Flags))
        {
            if (f.IsLiteral || f.IsInitOnly) continue;

            if (f.FieldType.IsArray)
            {
                // All of them are declared `= Array.Empty<T>()`.
                f.SetValue(null, Array.CreateInstance(f.FieldType.GetElementType()!, 0));
            }
            else if (f.Name == "_gpuCount")
            {
                f.SetValue(null, 0);
            }
            else if (f.Name == "_cpuIndex")
            {
                f.SetValue(null, -1);
            }
            else if (f.FieldType == typeof(bool))
            {
                f.SetValue(null, false);   // the pool-connected flags
            }
        }
    }
}

// The dual-mining slot collision that produced this session's metric bugs: the
// CPU row is APPENDED after the GPUs, so its index is never 0 when a GPU algo is
// also running. Both startup orders must reach the same layout, because gr/rx
// and the GPU algo start concurrently and either can win the race.
[Collection(nameof(MetricsSlotTests))]
public class MetricsSlotTests
{
    private static long[] Heartbeats(int n) => new long[n];

    public MetricsSlotTests() => MetricsReset.Reset();

    [Fact]
    public void CpuOnlyRunPutsTheCpuAtIndexZero()
    {
        Metrics.InitCpu(threads: 8, cpuName: "CPU · 8T RandomX");
        Assert.Equal(0, Metrics.CpuIndex);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Single(snap.Gpus);
        Assert.Equal("CPU · 8T RandomX", snap.Gpus[0].Name);
    }

    [Fact]
    public void GpuFirstThenCpuAppendsTheCpuAfterTheGpus()
    {
        Metrics.Init(2, Heartbeats(2));
        Metrics.SetGpuNames(new[] { "GPU 0", "GPU 1" });
        Metrics.InitCpu(threads: 8, cpuName: "CPU · 8T");

        Assert.Equal(2, Metrics.CpuIndex);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(3, snap.Gpus.Length);
        Assert.Equal("GPU 0", snap.Gpus[0].Name);
        Assert.Equal("GPU 1", snap.Gpus[1].Name);
        Assert.Equal("CPU · 8T", snap.Gpus[2].Name);
    }

    // The dashboard prints the GPU and CPU rates side by side rather than one
    // sum, because when dual-mining the two halves run different algorithms —
    // adding a pearl MH/s to a RandomX KH/s produces a number that means nothing.
    [Fact]
    public void DashboardSnapshotSplitsGpuAndCpuHashrates()
    {
        Metrics.Init(2, Heartbeats(2));
        Metrics.SetGpuNames(new[] { "GPU 0", "GPU 1" });
        Metrics.InitCpu(threads: 8, cpuName: "CPU · 8T");

        Metrics.SetHashRate(0, 1_000);
        Metrics.SetHashRate(1, 2_000);
        Metrics.SetHashRate(Metrics.CpuIndex, 11);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(3_000, snap.GpuHashesPerSec);
        Assert.Equal(11, snap.CpuHashesPerSec);
        Assert.Equal(3_011, snap.TotalHashesPerSec);   // JSON API's field, unchanged

        Assert.False(snap.Gpus[0].IsCpu);
        Assert.False(snap.Gpus[1].IsCpu);
        Assert.True(snap.Gpus[2].IsCpu);
    }

    [Fact]
    public void GpuOnlyRunReportsNoCpuHashrate()
    {
        Metrics.Init(1, Heartbeats(1));
        Metrics.SetHashRate(0, 500);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(0, snap.CpuHashesPerSec);
        Assert.Equal(500, snap.GpuHashesPerSec);
        Assert.Equal(500, snap.TotalHashesPerSec);
        Assert.False(snap.Gpus[0].IsCpu);
    }

    [Fact]
    public void CpuFirstThenGpuRelocatesTheCpuAfterTheGpus()
    {
        // The order that actually bites: the CPU algo wins the startup race and
        // takes slot 0, then the GPU algo registers. If the CPU slot were left
        // where it was, rx/gr and GPU 0 would overwrite each other's stats.
        Metrics.InitCpu(threads: 8, cpuName: "CPU · 8T");
        Assert.Equal(0, Metrics.CpuIndex);

        Metrics.Init(2, Heartbeats(2));
        Metrics.SetGpuNames(new[] { "GPU 0", "GPU 1" });

        Assert.Equal(2, Metrics.CpuIndex);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(3, snap.Gpus.Length);
        Assert.Equal("GPU 0", snap.Gpus[0].Name);
        Assert.Equal("GPU 1", snap.Gpus[1].Name);
        Assert.Equal("CPU · 8T", snap.Gpus[2].Name);
    }

    [Fact]
    public void BothStartupOrdersProduceTheSameLayout()
    {
        MetricsReset.Reset();
        Metrics.Init(2, Heartbeats(2));
        Metrics.SetGpuNames(new[] { "GPU 0", "GPU 1" });
        Metrics.InitCpu(8, "CPU · 8T");
        var gpuFirst = (Metrics.CpuIndex, Metrics.GetDashboardSnapshot().Gpus.Select(g => g.Name).ToArray());

        MetricsReset.Reset();
        Metrics.InitCpu(8, "CPU · 8T");
        Metrics.Init(2, Heartbeats(2));
        Metrics.SetGpuNames(new[] { "GPU 0", "GPU 1" });
        var cpuFirst = (Metrics.CpuIndex, Metrics.GetDashboardSnapshot().Gpus.Select(g => g.Name).ToArray());

        Assert.Equal(gpuFirst.Item1, cpuFirst.Item1);
        Assert.Equal(gpuFirst.Item2, cpuFirst.Item2);
    }

    [Fact]
    public void SetGpuNamesDoesNotClobberTheCpuRowName()
    {
        // SetGpuNames runs after InitCpu in the CPU-first order. A blind
        // assignment would size the array back to the GPU count, dropping the
        // CPU name and leaving CpuIndex past the end of the array.
        Metrics.InitCpu(8, "CPU · 8T");
        Metrics.Init(1, Heartbeats(1));
        Metrics.SetGpuNames(new[] { "GPU 0" });

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(2, snap.Gpus.Length);
        Assert.Equal("CPU · 8T", snap.Gpus[Metrics.CpuIndex].Name);
    }

    [Fact]
    public void InitCpuIsIdempotent()
    {
        Metrics.InitCpu(8, "CPU · 8T");
        Metrics.InitCpu(8, "CPU · 8T");
        Assert.Equal(0, Metrics.CpuIndex);
        Assert.Single(Metrics.GetDashboardSnapshot().Gpus);
    }

    // ── the collision itself ─────────────────────────────────────────────────

    [Fact]
    public void CpuAndGpuShareCountsDoNotCollide()
    {
        // This is the assertion the rx bug would have failed: rx wrote share
        // counts to index 0, which under rx+prl is GPU 0.
        Metrics.Init(1, Heartbeats(1));
        Metrics.SetGpuNames(new[] { "GPU 0" });
        Metrics.InitCpu(8, "CPU · 8T");

        Metrics.IncShareAccepted(0);                 // GPU 0
        Metrics.IncShareAccepted(Metrics.CpuIndex);  // CPU
        Metrics.IncShareAccepted(Metrics.CpuIndex);
        Metrics.IncShareRejected(Metrics.CpuIndex);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(1, snap.Gpus[0].Accepted);
        Assert.Equal(0, snap.Gpus[0].Rejected);
        Assert.Equal(2, snap.Gpus[Metrics.CpuIndex].Accepted);
        Assert.Equal(1, snap.Gpus[Metrics.CpuIndex].Rejected);
    }

    [Fact]
    public void CpuAndGpuHashratesDoNotCollide()
    {
        Metrics.Init(1, Heartbeats(1));
        Metrics.SetGpuNames(new[] { "GPU 0" });
        Metrics.InitCpu(8, "CPU · 8T");

        Metrics.SetThroughput(0, 1000, 0, 1000, 1);
        Metrics.SetThroughput(Metrics.CpuIndex, 12500, 0, 12500, 1);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.Equal(1000, snap.Gpus[0].HashesPerSec);
        Assert.Equal(12500, snap.Gpus[Metrics.CpuIndex].HashesPerSec);
        Assert.Equal(13500, snap.TotalHashesPerSec);
    }

    [Fact]
    public void HeartbeatArrayGrowsWithTheCpuSlot()
    {
        // TouchHeartbeat bounds-checks and silently no-ops on an out-of-range
        // index, so an undersized array shows up as a permanently stale CPU row
        // rather than an exception.
        Metrics.Init(1, Heartbeats(1));
        Metrics.InitCpu(8, "CPU · 8T");

        Metrics.TouchHeartbeat(Metrics.CpuIndex);

        var snap = Metrics.GetDashboardSnapshot();
        Assert.NotEqual(0.0, snap.Gpus[Metrics.CpuIndex].HeartbeatAgeSec);
        Assert.Equal(0.0, snap.Gpus[0].HeartbeatAgeSec);   // never touched
    }

    [Fact]
    public void CpuAndGpuPoolConnectionFlagsAreIndependent()
    {
        // rx lit the GPU pool flag while solo-mining (SetPoolConnected instead of
        // SetCpuPoolConnected), showing the GPU row as connected with no pool.
        Metrics.Init(1, Heartbeats(1));
        Metrics.InitCpu(8, "CPU · 8T");

        Metrics.SetCpuPoolConnected(true);
        var snap = Metrics.GetDashboardSnapshot();
        Assert.True(snap.CpuConnected);
        Assert.False(snap.Connected);

        Metrics.SetCpuPoolConnected(false);
        Metrics.SetPoolConnected(true);
        snap = Metrics.GetDashboardSnapshot();
        Assert.False(snap.CpuConnected);
        Assert.True(snap.Connected);
    }
}
