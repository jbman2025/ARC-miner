// P/Invoke surface for ghostrider_capi.{dll,so} (native/randomx-xmrig/ghostrider_capi.cpp),
// a C ABI over XMRig's GhostRider (Raptoreum). CPU algo — no device handle; each
// worker thread owns one opaque ctx (8 CryptoNight contexts over a 16 MiB
// scratchpad).
//
// GhostRider hashes 8 nonces per call (<see cref="Lanes"/>): the native side is
// XMRig's own ghostrider::hash_octa, which packs the 8 lanes into shared
// scratchpads according to each CryptoNight variant's batch step. Mining loops
// must use <see cref="HashOcta"/>; <see cref="Hash"/> costs a full octa call and
// exists only for tests and one-off share verification.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Gr;

internal static partial class GrNative
{
    public const string Lib = "ghostrider_capi";

    /// <summary>GhostRider hash output width in bytes.</summary>
    public const int HashBytes = 32;

    /// <summary>Nonces hashed per native call. GhostRider is fixed at 8
    /// (XMRig's <c>Algorithm::GHOSTRIDER_RTM</c> min/max intensity).</summary>
    public const int Lanes = 8;

    /// <summary>Block header width in bytes.</summary>
    public const int HeaderBytes = 80;

    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_last_error")]
    private static partial nint LastErrorPtr();

    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? "";

    /// <summary>0 if the canonical GhostRider test vector matches; &lt;0 on
    /// mismatch or allocation failure (see <see cref="LastError"/>).</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_selftest")]
    public static partial int Selftest();

    /// <summary>Number of nonces hashed per <see cref="HashOcta"/> call, as
    /// reported by the native library. Should equal <see cref="Lanes"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_lanes")]
    public static partial int NativeLanes();

    /// <summary>1 if the worker scratchpads got huge pages, 0 if they fell back to
    /// normal pages, -1 if no context exists yet. Each worker random-walks 16 MiB
    /// of CryptoNight scratchpads, so the fallback roughly halves hashrate.</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_huge_pages")]
    public static partial int HugePages();

    /// <summary>Allocate a per-thread context (8 CryptoNight ctxs + a 16 MiB
    /// scratchpad), or <see cref="nint.Zero"/> on failure.</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_create_ctx")]
    public static partial nint CreateCtx();

    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_destroy_ctx")]
    public static partial void DestroyCtx(nint ctx);

    /// <summary>Hash <see cref="Lanes"/> headers at once. <paramref name="input"/>
    /// is <c>Lanes * size</c> bytes (8 copies of the 80-byte header differing
    /// only in the nonce at offset 76); <paramref name="output"/> receives
    /// <c>Lanes * 32</c> bytes, lane <c>i</c>'s hash at <c>i * 32</c>.</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_hash_octa")]
    public static unsafe partial void HashOcta(nint ctx, byte* input, uint size, byte* output);

    /// <summary>One GhostRider hash of <paramref name="input"/> (the block
    /// header; the algo-selecting seed is bytes [4,36)) into a 32-byte
    /// <paramref name="out32"/>. Internally a full 8-lane call — use
    /// <see cref="HashOcta"/> in mining loops.</summary>
    [LibraryImport(Lib, EntryPoint = "ghostrider_capi_hash")]
    public static unsafe partial void Hash(nint ctx, byte* input, uint size, byte* out32);
}
