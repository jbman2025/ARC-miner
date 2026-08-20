// P/Invoke surface for sha3t_capi.dll (native/sha3t-keccak/sha3t_capi.cpp).
// SHA3-256t search kernel: given the 80-byte header as ten little-endian u64
// lanes and a four-lane target, report the nonces whose triple SHA3-256 lands
// at or under it.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Sha3t;

internal static partial class Sha3tNative
{
    public const string Lib = "sha3t_capi";

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_open")]
    public static partial int Open(int deviceIndex);

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_device_count")]
    public static partial int DeviceCount();

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_device_name")]
    private static partial nint DeviceNamePtr();
    public static string DeviceName() => Marshal.PtrToStringUTF8(DeviceNamePtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_device_name_at")]
    private static partial nint DeviceNameAtPtr(int index);

    /// <summary>Name of the GPU at <paramref name="index"/> without opening it —
    /// for host-side enumeration/filtering before assigning threads to devices.</summary>
    public static string DeviceNameAt(int index) => Marshal.PtrToStringUTF8(DeviceNameAtPtr(index)) ?? "";

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_last_error")]
    private static partial nint LastErrorPtr();
    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "sha3t_capi_close")]
    public static partial void Close();

    /// <summary>Scan [nonceBase, nonceBase+count). hdr10 = the 80-byte header as
    /// ten little-endian u64 lanes (lane 9's high half is the nonce slot);
    /// target4 = four little-endian u64 lanes, index 3 most significant. Winning
    /// nonces fill foundOut up to foundCap; foundTotal is the full count and may
    /// exceed it. Returns 0, or &lt;0 with <see cref="LastError"/> set.</summary>
    [LibraryImport(Lib, EntryPoint = "sha3t_capi_search")]
    public static partial int Search(
        ulong[] hdr10, ulong[] target4,
        uint nonceBase, uint count,
        uint[] foundOut, uint foundCap, out uint foundTotal);

    /// <summary>Hash one header on the DEVICE and return the four digest lanes.
    /// Not a mining path — it exists so a test can prove the GPU kernel and the
    /// host implementation agree on a real block.</summary>
    [LibraryImport(Lib, EntryPoint = "sha3t_capi_hash_one")]
    public static partial int HashOne(ulong[] hdr10, uint nonce, ulong[] out4);
}
