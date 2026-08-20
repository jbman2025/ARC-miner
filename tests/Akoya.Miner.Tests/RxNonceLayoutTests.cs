using System.Buffers.Binary;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Nonce placement for the two blob layouts that share the Monero/XMRig stratum
/// dialect. Getting this wrong does not fail loudly — the miner hashes a
/// corrupted header and the pool rejects every share as "low-difficulty", which
/// looks like a difficulty or target bug and is not one.
///
/// The 80-byte case below is a REAL job captured from bloz.suprnova.cc:7305
/// (rx/blockzero), not a hand-built header — the same discipline the GhostRider
/// notes insist on.
/// </summary>
public class RxNonceLayoutTests
{
    private static (int Offset, bool FullWidth) Layout(byte[] blob, string algo)
        => Akoya.Miner.Algos.Rx.RxPoolClient.NonceLayout(blob, algo);

    // Captured 2026-07-29 from bloz.suprnova.cc:7305, algo "rx/blockzero".
    private const string BlozBlobHex =
        "00000020" +                                                          // version
        "c1b01c4984c3d78b4ad74353ebdd80e81931d0fac67f7f66b8918ff1fceb5a26" +  // prevhash
        "998afda17fb18a2f9e0fcfaddd16980b94acd93a1b7ba892332d1c82b3b2a5b1" +  // merkle root
        "30ac696a" +                                                          // ntime
        "08020f1d" +                                                          // nbits
        "00000000";                                                           // nonce

    private static byte[] BlozBlob() => Convert.FromHexString(BlozBlobHex);

    [Fact]
    public void CapturedBlozJobIsAnEightyByteBitcoinHeader()
    {
        Assert.Equal(80, BlozBlob().Length);
    }

    [Fact]
    public void EightyByteBlobPutsTheNonceLastAndSearchesAllThirtyTwoBits()
    {
        var (offset, fullWidth) = Layout(BlozBlob(), "rx/blockzero");
        Assert.Equal(76, offset);
        Assert.True(fullWidth);
    }

    [Fact]
    public void MoneroStyleBlobKeepsOffset39AndThe24BitSearch()
    {
        // A 76-byte CryptoNote hashing blob — the layout rx has always used.
        var (offset, fullWidth) = Layout(new byte[76], "rx/0");
        Assert.Equal(39, offset);
        Assert.False(fullWidth);
    }

    [Fact]
    public void WritingTheNonceAtOffset76LeavesTheHeaderIntact()
    {
        // The actual defect: offset 39 lands inside the merkle root, so the
        // header we hash is not the header the pool reconstructs.
        var blob = BlozBlob();
        var merkleBefore = blob.AsSpan(36, 32).ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(76, 4), 0xDEADBEEF);

        Assert.Equal(merkleBefore, blob.AsSpan(36, 32).ToArray());
        Assert.Equal(0xDEADBEEF, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(76, 4)));
    }

    [Fact]
    public void TheOldOffsetWouldHaveCorruptedTheMerkleRoot()
    {
        // Pins down why this failed, so nobody "simplifies" the offset back.
        var blob = BlozBlob();
        var merkleBefore = blob.AsSpan(36, 32).ToArray();

        blob[39] = 0x11; blob[40] = 0x22; blob[41] = 0x33;

        Assert.NotEqual(merkleBefore, blob.AsSpan(36, 32).ToArray());
        // ...and the real nonce field would still be zero.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(76, 4)));
    }

    [Fact]
    public void ATooShortBlobIsRejectedRatherThanIndexedOutOfBounds()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Layout(new byte[20], "rx/0"));
        Assert.Contains("20-byte blob", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvOverrideForcesTheOffset()
    {
        const string key = "ARC_RX_NONCE_OFFSET";
        var prev = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "12");
            var (offset, fullWidth) = Layout(new byte[76], "rx/0");
            Assert.Equal(12, offset);
            Assert.True(fullWidth);
        }
        finally { Environment.SetEnvironmentVariable(key, prev); }
    }

    [Fact]
    public void AnOutOfRangeOverrideIsIgnored()
    {
        const string key = "ARC_RX_NONCE_OFFSET";
        var prev = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "999");
            Assert.Equal(76, Layout(BlozBlob(), "rx/blockzero").Offset);
        }
        finally { Environment.SetEnvironmentVariable(key, prev); }
    }
}
