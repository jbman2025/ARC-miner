using System.Runtime.InteropServices;
using Akoya.PearlGemm;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// WorkspaceParams is passed by pointer to the C ABI, so its layout IS the
/// contract with PearlCapiWorkspaceParams in
/// native/pearl-gemm/csrc/capi/pearl_gemm_capi.h. Nothing in the build checks
/// that the two agree — a field added on one side and not the other compiles
/// clean on both, and the miner then reads a device pointer out of the middle
/// of an unrelated field.
///
/// These tests do not prove the two structs match (only a build against the
/// header could). They pin the managed layout so that changing it is a
/// deliberate act with a red test attached, which is the part that was missing
/// when `salted_seeds` went in.
/// </summary>
public class WorkspaceParamsLayoutTests
{
    // 9 int32 dims + 4 uint32 tensor-hash constants = 52 B, then uint64
    // sigma_seed at the next 8-byte boundary (56), then 26 pointers.
    private const int SigmaSeedOffset = 56;
    private const int FirstPointerOffset = SigmaSeedOffset + 8;
    private const int PointerCount = 26;

    private static int OffsetOf(string field)
        => (int)Marshal.OffsetOf<PearlGemmNative.WorkspaceParams>(field);

    [Fact]
    public void ScalarPrefixMatchesTheCLayout()
    {
        Assert.Equal(0, OffsetOf(nameof(PearlGemmNative.WorkspaceParams.M)));
        Assert.Equal(36, OffsetOf(nameof(PearlGemmNative.WorkspaceParams.ThNumBlocks)));
        Assert.Equal(SigmaSeedOffset, OffsetOf(nameof(PearlGemmNative.WorkspaceParams.SigmaSeed)));
        Assert.Equal(FirstPointerOffset, OffsetOf(nameof(PearlGemmNative.WorkspaceParams.A)));
    }

    // salted_seeds is the LAST field of the C struct: it must land immediately
    // after the pointer block. SyclKSub follows it and is host-only — the native
    // side has never declared or read that field, so it must stay last or it
    // would displace something the kernel does read.
    [Fact]
    public void SaltedSeedsFollowsThePointerBlockAndSyclKSubStaysLast()
    {
        int expected = FirstPointerOffset + PointerCount * IntPtr.Size;
        Assert.Equal(expected, OffsetOf(nameof(PearlGemmNative.WorkspaceParams.SaltedSeeds)));
        Assert.Equal(expected + sizeof(int), OffsetOf(nameof(PearlGemmNative.WorkspaceParams.SyclKSub)));
        Assert.Equal(
            expected + 2 * sizeof(int),
            Marshal.SizeOf<PearlGemmNative.WorkspaceParams>());
    }

    // The point of the field: it is an int, not a bool. A managed bool marshals
    // as 4 bytes here by default, but only by default — being explicit is what
    // keeps `salted_seeds != 0` on the native side meaningful.
    [Fact]
    public void SaltedSeedsIsAnInt32()
        => Assert.Equal(
            typeof(int),
            typeof(PearlGemmNative.WorkspaceParams)
                .GetField(nameof(PearlGemmNative.WorkspaceParams.SaltedSeeds))!.FieldType);
}
