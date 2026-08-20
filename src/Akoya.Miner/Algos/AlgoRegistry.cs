namespace Akoya.Miner.Algos;

// Static, NativeAOT-safe algorithm registry (no reflection / no Assembly.Load).
// New algorithms are added here as one dictionary entry; `--algo <name>` /
// ARC_ALGO selects one. Name matching is case-insensitive.
internal static class AlgoRegistry
{
    private static readonly Dictionary<string, IMiningAlgo> _algos =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["prl"] = new Prl.PrlAlgo(),
            // btx removed 2026-08-14: the chain is broken and pools have dropped
            // it. Dual-mining combos are derived from this table, so the btx duals
            // (rx+btx, btx+gr, …) disappear with the entry — nothing else to undo.
            ["csd"] = new Csd.CsdAlgo(),
            // BitcoinIII (BC3). Stock Bitcoin consensus with SHA3-256t as the
            // block hash; "bc3" is accepted as the coin-name alias.
            ["sha3t"] = new Sha3t.Sha3tAlgo(),
            ["bc3"] = new Sha3t.Sha3tAlgo(),
            ["rx"]  = new Rx.RxAlgo(),
            ["gr"]  = new Gr.GrAlgo(),
            ["nm"]  = new Nm.NmAlgo(),
        };

    public static IMiningAlgo? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.Contains('+'))
        {
            var parts = name.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var algo1 = _algos.GetValueOrDefault(parts[0].Trim());
                var algo2 = _algos.GetValueOrDefault(parts[1].Trim());
                if (algo1 is not null && algo2 is not null)
                {
                    return new DualMiningAlgo(algo1, algo2);
                }
            }
            return null;
        }

        return _algos.GetValueOrDefault(name);
    }

    public static string RegisteredNames => string.Join(", ", _algos.Keys);
}
