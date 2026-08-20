namespace Akoya.Miner.Algos.Prl;

/// <summary>
/// One-shot dump of the HOST side of the noise-seed derivation, in the same
/// format the SYCL kernel prints for the device side. Enable with
/// <c>ARC_PRL_SEED_TRACE=1</c>; both lines land on stderr so the two can be
/// diffed directly.
///
/// Why this is needed even though <c>arc-miner verify-seeds</c> passes: that
/// check proves the two IMPLEMENTATIONS agree when handed identical inputs. It
/// cannot see whether the running miner hands them identical inputs — and the
/// salted derivation newly depends on <c>m</c> and <c>n</c>, which reach the
/// kernel (from the workspace) and the host (from the share payload) down
/// entirely separate paths. A disagreement there produces exactly the symptom
/// this was written to chase: the GPU triggers on a tile, the host rebuilds it,
/// the hash does not clear, the share is skipped, and the pool never sees
/// anything — no rejects to look at, no error, full power draw.
/// </summary>
internal static class SeedTrace
{
    private static readonly bool Enabled =
        (Akoya.Crypto.MinerEnv.Get("ARC_PRL_SEED_TRACE") ?? "") is "1" or "true";

    private static int _done;

    public static void Dump(
        ReadOnlySpan<byte> jobKey, ReadOnlySpan<byte> hashA, ReadOnlySpan<byte> hashB,
        int m, int n, bool salted,
        ReadOnlySpan<byte> aSeed, ReadOnlySpan<byte> bSeed)
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref _done, 1) != 0) return;

        var w = Console.Error;
        w.WriteLine($"[seed-trace HOST  ] m={m} n={n} salted={(salted ? 1 : 0)}");
        w.WriteLine($"[seed-trace HOST  ]   jobKey={Convert.ToHexString(jobKey)}");
        w.WriteLine($"[seed-trace HOST  ]   A_root={Convert.ToHexString(hashA)}");
        w.WriteLine($"[seed-trace HOST  ]   B_root={Convert.ToHexString(hashB)}");
        w.WriteLine($"[seed-trace HOST  ]   a_seed={Convert.ToHexString(aSeed)}");
        w.WriteLine($"[seed-trace HOST  ]   b_seed={Convert.ToHexString(bSeed)}");
        w.Flush();
    }
}
