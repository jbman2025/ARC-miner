// Human-readable number formatting for the console log, the dashboard and the
// stats API.
//
// These two lived as internal statics on GpuWorker — a 2,600-line Pearl-only
// class — which meant btx, csd, gr, nm and rx all took a reference to Pearl's
// mining engine purely to format a difficulty. Phase 4 of docs/SLIM-PLAN.md
// moves GpuWorker into Algos/Prl/ where it belongs, and this is what has to come
// out first so the other five algos are not dragged along with it.

namespace Akoya.Miner.Observability;

internal static class DisplayFormat
{
    /// <summary>Raw difficulty (a number, not a compact nBits) with K/M/G
    /// suffixes. Used by the stratum algos whose pool sends a plain double.</summary>
    internal static string DiffValue(double diff)
    {
        if (!double.IsFinite(diff) || diff <= 0) return "—";
        return diff switch
        {
            >= 1e9 => $"{diff / 1e9:F2}G",
            >= 1e6 => $"{diff / 1e6:F2}M",
            >= 1e3 => $"{diff / 1e3:F1}K",
            _      => $"{diff:F0}",
        };
    }

    /// <summary>Hashes per second, scaled to the largest unit that keeps the
    /// mantissa above 1.</summary>
    internal static string HashRate(double hps)
    {
        if (!double.IsFinite(hps) || hps <= 0) return "0 H/s";
        string[] units = { "H/s", "kH/s", "MH/s", "GH/s", "TH/s", "PH/s", "EH/s" };
        int i = 0;
        while (hps >= 1000.0 && i < units.Length - 1) { hps /= 1000.0; i++; }
        return $"{hps:F2} {units[i]}";
    }
}
