// NativeEnv — set environment variables the NATIVE kernel's getenv() can see.
//
// The SYCL kernel reads its tuning knobs (AKOYA_TGEMM_NB/_MB, AKOYA_SEARCH_M)
// via getenv(), which on Windows resolves to the UCRT (api-ms-win-crt-
// environment → ucrtbase.dll). .NET's Environment.SetEnvironmentVariable only
// updates the Win32 process block, which the UCRT's getenv cache NEVER syncs
// from — so a value set that way is invisible to the kernel and it silently
// runs defaults. Pushing through the UCRT's own _putenv_s (libc setenv on
// Linux) is the only way a RUNTIME change reaches getenv. (The .bat workflow
// is unaffected: vars set before process start are in the block the UCRT
// initializes its copy from at load.)

using System.Runtime.InteropServices;

namespace Akoya.Miner.Mining;

internal static partial class NativeEnv
{
    [LibraryImport("ucrtbase.dll", EntryPoint = "_putenv_s", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UcrtPutEnvS(string name, string value);

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LibcSetEnv(string name, string value, int overwrite);

    [LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LibcUnsetEnv(string name);

    /// <summary>Set <paramref name="name"/> so the native kernel's getenv sees
    /// it. Pass <c>null</c> to delete (an empty value parses as 0 → tiny-window
    /// footgun the C-side guards against, but deleting is cleaner).</summary>
    public static void Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value); // keep .NET reads in sync
        if (OperatingSystem.IsWindows())
        {
            try { UcrtPutEnvS(name, value ?? ""); } catch { /* best-effort */ }
        }
        else
        {
            try
            {
                if (value is null) LibcUnsetEnv(name);
                else LibcSetEnv(name, value, 1);
            }
            catch { /* best-effort */ }
        }
    }
}
