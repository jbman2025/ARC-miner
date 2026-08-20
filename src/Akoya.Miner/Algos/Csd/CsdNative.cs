// P/Invoke surface for csd_capi.dll (native/csd-sha256d/csd_capi.cpp).
// SHA-256d search kernel: given the block-0 midstate, tail words, and an 8-word
// big-endian target, report nonces whose sha256d(header) <= target.

using System.Runtime.InteropServices;

namespace Akoya.Miner.Algos.Csd;

internal static partial class CsdNative
{
    public const string Lib = "csd_capi";

    [LibraryImport(Lib, EntryPoint = "csd_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "csd_capi_open")]
    public static partial int Open(int deviceIndex);

    [LibraryImport(Lib, EntryPoint = "csd_capi_device_count")]
    public static partial int DeviceCount();

    [LibraryImport(Lib, EntryPoint = "csd_capi_device_name")]
    private static partial nint DeviceNamePtr();
    public static string DeviceName() => Marshal.PtrToStringUTF8(DeviceNamePtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "csd_capi_device_name_at")]
    private static partial nint DeviceNameAtPtr(int index);

    /// <summary>Name of the GPU at <paramref name="index"/> without opening it —
    /// for host-side enumeration/filtering before assigning threads to devices.</summary>
    public static string DeviceNameAt(int index) => Marshal.PtrToStringUTF8(DeviceNameAtPtr(index)) ?? "";

    [LibraryImport(Lib, EntryPoint = "csd_capi_last_error")]
    private static partial nint LastErrorPtr();
    public static string LastError() => Marshal.PtrToStringUTF8(LastErrorPtr()) ?? "";

    [LibraryImport(Lib, EntryPoint = "csd_capi_close")]
    public static partial void Close();

    /// <summary>Scan [nonceBase, nonceBase+count). mid8 = block-0 midstate,
    /// tail5 = header words 16..19 (index 4 unused), target8 = 8 big-endian
    /// words. Winning nonces (kernel W[4] values) fill foundOut up to foundCap;
    /// foundTotal is the full count (may exceed cap). Returns 0 / &lt;0 error.</summary>
    [LibraryImport(Lib, EntryPoint = "csd_capi_search")]
    public static partial int Search(
        uint[] mid8, uint[] tail5, uint[] target8,
        uint nonceBase, uint count,
        uint[] foundOut, uint foundCap, out uint foundTotal);
}
