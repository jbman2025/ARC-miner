// Worker-thread CPU pinning for RandomX. RandomX is extremely cache-sensitive:
// each VM keeps a 2 MB scratchpad hot in L2/L3, and on multi-CCD chips (e.g. the
// Ryzen 9 5900X, two CCDs each with its own 32 MB L3) an unpinned worker that the
// OS migrates across CCDs re-reads its scratchpad and dataset from a cold cache,
// costing several percent of hashrate. XMRig pins every mining thread; this does
// the same. No-op on non-Windows (workers still run, just unpinned) so the algo
// stays cross-platform; Linux affinity can be layered on later.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Cpu;

internal static class CpuAffinity
{
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint SetThreadAffinityMask(nint hThread, nint dwThreadAffinityMask);

    /// <summary>
    /// A CPU pinning order that fills distinct physical cores before their SMT
    /// siblings. On x86 SMT, logical processors 2k and 2k+1 are the two threads
    /// of physical core k, so the order [0,2,4,…,1,3,5,…] spreads the first N
    /// workers across N separate cores (and, on the 5900X, across both CCDs)
    /// before doubling up. Falls back to identity if we can't reason about SMT.
    /// </summary>
    public static int[] BuildPinOrder(int threadCount)
    {
        int logical = Environment.ProcessorCount;
        if (logical <= 0) logical = threadCount;

        var order = new int[logical];
        // Even logical IDs first (one per physical core), then the odd siblings.
        int w = 0;
        for (int lp = 0; lp < logical; lp += 2) order[w++] = lp;
        for (int lp = 1; lp < logical; lp += 2) order[w++] = lp;
        return order;
    }

    /// <summary>Pin the current thread to a single logical processor. Silent no-op
    /// off Windows or on failure — mining continues, just without the locality win.</summary>
    public static void PinCurrentThread(int logicalCpu)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (logicalCpu < 0 || logicalCpu >= 64) return;   // affinity mask is 64-bit
        try
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nint)(1L << logicalCpu));
        }
        catch
        {
            // Best effort; unpinned is still correct, just slower.
        }
    }
}
