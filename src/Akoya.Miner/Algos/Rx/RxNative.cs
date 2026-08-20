// P/Invoke surface for randomx_capi.{dll,so} (native/randomx-xmrig/randomx_capi.cpp),
// a C ABI over XMRig's RandomX fork. CPU algo — no device handle; the process
// holds one global cache/dataset and each worker thread owns a VM.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Rx;

internal static partial class RxNative
{
    public const string Lib = "randomx_capi";

    /// <summary>RandomX hash output width in bytes.</summary>
    public const int HashBytes = 32;

    [LibraryImport(Lib, EntryPoint = "randomx_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "randomx_capi_last_error")]
    private static partial nint LastErrorPtr();

    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? "";

    /// <summary>0 if the canonical RandomX test vector matches; &lt;0 on mismatch
    /// or allocation failure (see <see cref="LastError"/>).</summary>
    [LibraryImport(Lib, EntryPoint = "randomx_capi_selftest")]
    public static partial int Selftest();

    /// <summary>Allocate + key the cache and, when <paramref name="fullMem"/>, the
    /// dataset (filled in parallel across <paramref name="initThreads"/>).
    /// Returns 0 on success.</summary>
    [LibraryImport(Lib, EntryPoint = "randomx_capi_init")]
    public static partial int Init(byte[] key, uint keyLen, int fullMem, int largePages, int initThreads);

    [LibraryImport(Lib, EntryPoint = "randomx_capi_dataset_item_count")]
    public static partial ulong DatasetItemCount();

    /// <summary>Create a per-thread VM bound to the global cache/dataset, or
    /// <see cref="nint.Zero"/> on failure.</summary>
    [LibraryImport(Lib, EntryPoint = "randomx_capi_create_vm")]
    public static partial nint CreateVm();

    [LibraryImport(Lib, EntryPoint = "randomx_capi_destroy_vm")]
    public static partial void DestroyVm(nint vm);

    [LibraryImport(Lib, EntryPoint = "randomx_capi_hash")]
    public static unsafe partial void Hash(nint vm, byte* input, uint inLen, byte* out32);

    /// <summary>Begin a pipelined hash for <paramref name="input"/>; produces no
    /// output. Pair with <see cref="HashNext"/>/<see cref="HashLast"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "randomx_capi_hash_first")]
    public static unsafe partial void HashFirst(nint vm, byte* input, uint inLen);

    /// <summary>Emit the hash of the PREVIOUS input while beginning the hash of
    /// <paramref name="nextInput"/> — overlaps the next scratchpad fill with the
    /// current program's execution (XMRig-style pipelining). There is no matching
    /// "last": the in-flight hash is flushed by the next <see cref="HashNext"/>.</summary>
    [LibraryImport(Lib, EntryPoint = "randomx_capi_hash_next")]
    public static unsafe partial void HashNext(nint vm, byte* nextInput, uint nextLen, byte* out32);

    [LibraryImport(Lib, EntryPoint = "randomx_capi_shutdown")]
    public static partial void Shutdown();
}
