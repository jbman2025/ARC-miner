// P/Invoke surface for neuromorph_capi.{dll,so} (native/randomx-xmrig/neuromorph_capi.cpp),
// a C ABI over NeuroMorph (`nm/1`, Cereblix / CRB). CPU algo — no device handle;
// each worker thread owns one opaque ctx (a 2 MiB scratchpad + program buffers).
//
// The 64 MiB per-epoch dataset is process-wide and owned by the native side:
// call SetSeed on every context whenever the pool's seed_hash changes, and the
// first caller for a new epoch builds it while the rest block.
//
// Unlike GhostRider (8 nonces per call), NeuroMorph hashes one nonce per call.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Nm;

internal static partial class NmNative
{
    public const string Lib = "neuromorph_capi";

    /// <summary>NeuroMorph hash output width in bytes.</summary>
    public const int HashBytes = 32;

    /// <summary>Block header width in bytes (PROTOCOL.md HeaderLen).</summary>
    public const int HeaderBytes = 124;

    /// <summary>Byte offset of the 8-byte little-endian nonce field. The miner
    /// iterates only the low 4 bytes; the high 4 are the pool's extranonce1.</summary>
    public const int NonceOffset = 116;

    /// <summary>Epoch seed width in bytes (the pool's <c>seed_hash</c>).</summary>
    public const int SeedBytes = 32;

    [LibraryImport(Lib, EntryPoint = "nm_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "nm_capi_last_error")]
    private static partial nint LastErrorPtr();

    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? "";

    /// <summary>Header length the native side was built with. Checked against
    /// <see cref="HeaderBytes"/> at startup so a stale lib fails loudly.</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_header_len")]
    public static partial int NativeHeaderLen();

    /// <summary>Nonce offset the native side was built with.</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_nonce_offset")]
    public static partial int NativeNonceOffset();

    /// <summary>0 if the hash is self-consistent (deterministic, nonce-sensitive,
    /// context-independent); &lt;0 otherwise (see <see cref="LastError"/>).</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_selftest")]
    public static partial int Selftest();

    /// <summary>1 if the shared 64 MiB dataset got huge pages, 0 if it fell back
    /// to normal pages, -1 if it has not been built yet. NeuroMorph is DRAM-latency
    /// bound, so the fallback costs roughly a third of the hashrate — worth saying
    /// out loud rather than leaving the user to wonder.</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_huge_pages")]
    public static partial int HugePages();

    /// <summary>Allocate a per-thread context, or <see cref="nint.Zero"/> on failure.</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_create_ctx")]
    public static partial nint CreateCtx();

    [LibraryImport(Lib, EntryPoint = "nm_capi_destroy_ctx")]
    public static partial void DestroyCtx(nint ctx);

    /// <summary>Point a context at an epoch: derives the VM parameters from the
    /// 32-byte <paramref name="seed32"/> and attaches the shared 64 MiB dataset,
    /// building it if the epoch changed. Returns 0 on success.</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_set_seed")]
    public static unsafe partial int SetSeed(nint ctx, byte* seed32);

    /// <summary>One NeuroMorph hash of the 124-byte <paramref name="header"/> into
    /// a 32-byte <paramref name="out32"/>. <paramref name="height"/> selects
    /// whether the memory-hard dataset step runs (active at height &gt;= 240).</summary>
    [LibraryImport(Lib, EntryPoint = "nm_capi_hash")]
    public static unsafe partial void Hash(nint ctx, byte* header, ulong height, byte* out32);
}
