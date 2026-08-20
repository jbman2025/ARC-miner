// Shared GPU-selection policy: skip integrated GPUs unless the operator opts in
// with --igpu (ARC_IGPU=1). An iGPU is orders of magnitude slower than a
// discrete Arc/NVIDIA/AMD card and mining on it by default just wastes power and
// picks the wrong device on a laptop/desktop where index 0 is the iGPU.
//
// Integrated detection is best-effort by device name (works with no native
// rebuild). The SYCL algos (CSD) can additionally consult a native
// host_unified_memory flag when their capi exposes it; this name heuristic is
// the portable fallback and the sole signal for the PRL/CUDA-shim path.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Mining;

internal static partial class GpuSelection
{
    /// <summary>True when the operator passed --igpu / ARC_IGPU=1, i.e.
    /// integrated GPUs should be treated as eligible mining devices.</summary>
    public static bool IgpuEnabled =>
        Environment.GetEnvironmentVariable("ARC_IGPU") is "1" or "true" or "TRUE";

    // Discrete Arc marketing tokens (A770, B580, …). Presence ⇒ discrete.
    [GeneratedRegex(@"\b[AB]\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArcModelToken();

    /// <summary>Best-effort integrated-GPU test from a device name. Discrete
    /// cards (Arc A/B-series, GeForce, Radeon) return false; Intel iGPUs
    /// (UHD/HD/Iris, and the model-less "Intel … Graphics" of Meteor/Lunar Lake)
    /// return true.</summary>
    public static bool IsIntegratedByName(string? name)
    {
        var n = name ?? "";
        if (ArcModelToken().IsMatch(n)) return false;              // Arc A770 / B580 → discrete
        var low = n.ToLowerInvariant();
        if (low.Contains("rtx") || low.Contains("gtx") ||
            low.Contains("radeon") || low.Contains("geforce")) return false; // discrete NV/AMD
        return low.Contains("uhd graphics") || low.Contains("hd graphics") || low.Contains("iris")
            || (low.Contains("graphics") && low.Contains("intel")); // model-less Intel iGPU
    }

    /// <summary>Pick a SYCL device for the single-device algos (CSD), skipping
    /// integrated GPUs unless --igpu. <paramref name="open"/> opens device i
    /// (true on success) and leaves it current; <paramref name="nameOfOpen"/>
    /// returns the currently-open device's name. An explicit index is always
    /// honored. Returns the chosen index (already open), or a negative sentinel:
    /// -1 = no openable device, -2 = only integrated device(s) and --igpu unset.</summary>
    public static int SelectSyclDevice(
        int count, Func<int, bool> open, Func<string> nameOfOpen, int? explicitIndex, ILogger log, string tag)
    {
        if (count <= 0) return -1;
        if (explicitIndex is int ei) return open(ei) ? ei : -1;

        bool igpu = IgpuEnabled;
        bool openedAny = false;
        for (int i = 0; i < count; i++)
        {
            if (!open(i)) continue;
            openedAny = true;
            var name = nameOfOpen();
            if (igpu || !IsIntegratedByName(name))
            {
                log.LogInformation("{Tag}: selected GPU[{I}] {Name}", tag, i, name);
                return i;
            }
            log.LogInformation("{Tag}: GPU[{I}] \"{Name}\" is integrated — skipping (pass --igpu to use it)", tag, i, name);
        }
        return openedAny ? -2 : -1;
    }

    /// <summary>Enumerate the devices to mine on, skipping integrated GPUs unless
    /// --igpu. <paramref name="nameAt"/> returns device i's name without opening
    /// it. An explicit index list (<paramref name="explicitIndices"/>) is honored
    /// verbatim. Returns the chosen device indices (empty if only iGPUs and
    /// --igpu unset). Falls back to a single device if <paramref name="nameAt"/>
    /// is unavailable (old native lib without device_name_at).</summary>
    public static List<int> EnumerateMiningDevices(
        int count, Func<int, string> nameAt, IReadOnlyList<int>? explicitIndices, ILogger log, string tag)
    {
        if (explicitIndices is { Count: > 0 }) return explicitIndices.ToList();
        var chosen = new List<int>();
        if (count <= 0) return chosen;

        bool igpu = IgpuEnabled;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var name = nameAt(i);
                if (!igpu && IsIntegratedByName(name))
                {
                    log.LogInformation("{Tag}: GPU[{I}] \"{Name}\" is integrated — skipping (pass --igpu to use it)", tag, i, name);
                    continue;
                }
                log.LogInformation("{Tag}: GPU[{I}] {Name}", tag, i, name);
                chosen.Add(i);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Native lib predates device_name_at → can't enumerate names; mine on
            // device 0 only. Rebuild the *_capi lib for multi-GPU.
            log.LogWarning("{Tag}: native lib has no device_name_at — multi-GPU/iGPU filtering disabled; using GPU 0. Rebuild the {Tag2}_capi lib to enable.", tag, tag);
            chosen.Add(0);
        }
        return chosen;
    }
}
