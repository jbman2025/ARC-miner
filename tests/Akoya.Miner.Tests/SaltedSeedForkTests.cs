using Akoya.Crypto;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Consensus tests for the salted noise-seed hardfork (pearl PR #280).
///
/// These seeds decide the noise the GEMM searches over. A wrong byte does not
/// degrade anything — it invalidates every share while the rig keeps looking
/// perfectly healthy — so the derivation is pinned from both ends: the legacy
/// path must not have moved, and the new path must be the specified function.
/// </summary>
public class SaltedSeedForkTests
{
    private static byte[] Fill(byte seed)
    {
        var b = new byte[32];
        for (int i = 0; i < 32; i++) b[i] = (byte)(seed + i * 7);
        return b;
    }

    private static readonly byte[] ARoot  = Fill(0x11);
    private static readonly byte[] BRoot  = Fill(0x40);
    private static readonly byte[] JobKey = Fill(0x90);
    private const int M = 131072, N = 131072;

    // The legacy chain, written out independently of the production code. If
    // DeriveNoiseSeeds ever drifts pre-fork, this fails — which matters because
    // the fork edit touched that exact function while the network was still V2.
    private static (byte[] B, byte[] A) LegacyReference(
        ReadOnlySpan<byte> jobKey, ReadOnlySpan<byte> hashA, ReadOnlySpan<byte> hashB)
    {
        Span<byte> buf = stackalloc byte[64];
        jobKey.CopyTo(buf); hashB.CopyTo(buf[32..]);
        var b = new byte[32];
        Blake3.Hash(buf, b);
        b.CopyTo(buf); hashA.CopyTo(buf[32..]);
        var a = new byte[32];
        Blake3.Hash(buf, a);
        return (b, a);
    }

    [Fact]
    public void LegacyDerivationIsUnchanged()
    {
        var (refB, refA) = LegacyReference(JobKey, ARoot, BRoot);
        var (gotB, gotA) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: false);
        Assert.Equal(refB, gotB);
        Assert.Equal(refA, gotA);
    }

    // Default arguments must keep the legacy behaviour: any call site that was
    // not updated for the fork has to stay pre-fork correct rather than silently
    // pick up V3 before its activation height.
    [Fact]
    public void DefaultArgumentsAreTheLegacyChain()
    {
        var (defB, defA) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot);
        var (refB, refA) = LegacyReference(JobKey, ARoot, BRoot);
        Assert.Equal(refB, defB);
        Assert.Equal(refA, defA);
    }

    // The V3 spec, rebuilt from the PR: salts are blake3 of the context strings,
    // and each root is bound in a single 64-byte block carrying its dimension.
    [Fact]
    public void SaltedDerivationMatchesTheSpecification()
    {
        static byte[] Bind(byte[] root, int dim, string ctx)
        {
            var salt = new byte[32];
            Blake3.Hash(System.Text.Encoding.ASCII.GetBytes(ctx), salt);
            Span<byte> block = stackalloc byte[64];   // root(32) ‖ dim LE(4) ‖ 0^28
            block.Clear();
            root.CopyTo(block);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block[32..36], (uint)dim);
            var bound = new byte[32];
            Blake3.KeyedHash(salt, block, bound);
            return bound;
        }

        var boundA = Bind(ARoot, M, "pearl/cert-v3/noise-seed/A");
        var boundB = Bind(BRoot, N, "pearl/cert-v3/noise-seed/B");
        var (refB, refA) = LegacyReference(JobKey, boundA, boundB);   // same chain, bound roots

        var (gotB, gotA) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: true);
        Assert.Equal(refB, gotB);
        Assert.Equal(refA, gotA);
    }

    [Fact]
    public void SaltedAndLegacyDiffer()
    {
        var (b2, a2) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: false);
        var (b3, a3) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: true);
        Assert.NotEqual(a2, a3);
        Assert.NotEqual(b2, b3);
    }

    // The whole point of the fork is that the roots are bound to their
    // dimensions, so the dimensions must actually reach the hash.
    [Fact]
    public void DimensionsAreCommitted()
    {
        var (_, aBase) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: true);
        var (_, aOtherM) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M + 1, N, salted: true);
        var (bBase, _) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: true);
        var (bOtherN, _) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N + 1, salted: true);
        Assert.NotEqual(aBase, aOtherM);
        Assert.NotEqual(bBase, bOtherN);
    }

    // A and B must not be interchangeable: distinct salts, and each root bound to
    // its OWN dimension. Swapping them has to change the answer.
    [Fact]
    public void TheTwoSidesAreDomainSeparated()
    {
        var (bNormal, aNormal) = CommitmentHasher.DeriveNoiseSeeds(JobKey, ARoot, BRoot, M, N, salted: true);
        var (bSwap, aSwap) = CommitmentHasher.DeriveNoiseSeeds(JobKey, BRoot, ARoot, M, N, salted: true);
        Assert.NotEqual(aNormal, aSwap);
        Assert.NotEqual(bNormal, bSwap);
    }

    [Theory]
    [InlineData(98_999, false)]   // one block before
    [InlineData(99_000, true)]    // the activation block itself is post-fork
    [InlineData(99_001, true)]
    [InlineData(98_900, false)]   // #280's original height, moved out by #282
    [InlineData(0, false)]        // no job seen yet is not evidence of pre-fork
    [InlineData(-1, false)]
    public void ActivationBoundaryIsInclusive(long height, bool expected)
        => Assert.Equal(expected, Akoya.Miner.Algos.Prl.SaltedSeedFork.IsActive(height));

    [Fact]
    public void MainnetHeightMatchesTheNodeParameter()
        // node/chaincfg/params.go on master, MainNetParams.SaltedSeedForkHeight.
        // #280 shipped 98,900; #282 delayed it to 99,000. Read the file, not the
        // feature PR.
        => Assert.Equal(99_000, Akoya.Miner.Algos.Prl.SaltedSeedFork.MainnetActivationHeight);
}
