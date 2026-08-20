using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Cpu;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class MsrTweaker
{
    private const string ServiceName = "WinRing0_1_2_0";
    private const string DeviceName = "\\\\.\\WinRing0_1_2_0";

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_KERNEL_DRIVER = 0x00000001;
    private const uint SERVICE_DEMAND_START = 0x00000003;
    private const uint SERVICE_ERROR_NORMAL = 0x00000001;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;

    private const uint IOCTL_OLS_READ_MSR = 0x9C402084;
    private const uint IOCTL_OLS_WRITE_MSR = 0x9C402088;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OlsMsrInput
    {
        public uint RegisterAddress;
        public uint ValueEAX; // Low 32 bits
        public uint ValueEDX; // High 32 bits
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateServiceW(
        IntPtr hSCManager,
        string lpServiceName,
        string lpDisplayName,
        uint dwDesiredAccess,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string lpBinaryPathName,
        string lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string lpDependencies,
        string lpServiceStartName,
        string lpPassword);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref uint lpInBuffer,
        uint nInBufferSize,
        out ulong lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref OlsMsrInput lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentProcessorNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

    private struct MsrBackup
    {
        public int CoreIndex;
        public uint Register;
        public ulong Value;
    }

    private static readonly List<MsrBackup> _backup = new();
    private static bool _applied;
    private static IntPtr _driverHandle = IntPtr.Zero;
    // The (register → intended value) pairs written by the last Apply, so we can
    // re-read them during active mining and detect if the CPU silently reverted
    // them (e.g. a config-register reset on a C-state transition).
    private static (uint Reg, ulong Val)[] _appliedTargets = Array.Empty<(uint, ulong)>();

    public static void Apply(ILogger log)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log.LogDebug("msr: Windows target platform only, skipping MSR modifications.");
            return;
        }

        if (_applied) return;

        if (!IsAdministrator())
        {
            log.LogWarning("⚠️ [WRN] msr: Miner is not running as Administrator. MSR tweaks cannot be applied.");
            return;
        }

        try
        {
            string driverPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinRing0x64.sys");
            if (!File.Exists(driverPath))
            {
                var extPath = global::NativeLibs.ExtractedPath;
                if (extPath != null)
                {
                    var extractedDriver = Path.Combine(extPath, "WinRing0x64.sys");
                    if (File.Exists(extractedDriver))
                    {
                        driverPath = extractedDriver;
                    }
                }
            }

            if (!File.Exists(driverPath))
            {
                log.LogError("❌ [ERR] msr: WinRing0x64.sys driver not found in path: {Path}", driverPath);
                return;
            }

            if (!LoadDriver(driverPath, log))
            {
                log.LogError("❌ [ERR] msr: failed to load or start WinRing0 driver service.");
                return;
            }

            _driverHandle = CreateFileW(
                DeviceName,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (_driverHandle == IntPtr.Zero || _driverHandle.ToInt64() == -1)
            {
                log.LogError("❌ [ERR] msr: failed to open device handle to {DeviceName}. Error: {Error}", DeviceName, Marshal.GetLastWin32Error());
                UnloadDriver();
                return;
            }

            var (cpuVendor, family) = DetectCpu();
            List<(uint Reg, ulong Val, ulong Mask)> targets;

            if (cpuVendor == "AMD")
            {
                if (family >= 25) // Zen 3 / Zen 4 (Family 19h / 25 dec)
                {
                    log.LogInformation("msr: applying AMD Zen 3/4 register optimizations...");
                    // Exact XMRig Zen3 preset (scripts/randomx_boost.sh). All raw
                    // writes — XMRig does not read-modify-write these. The key one
                    // is 0xc0011022 (DC_CFG): bits 0x70000 disable the hardware data
                    // prefetcher so it stops evicting the RandomX scratchpad — the
                    // largest single MSR win. (Was 0x...0150000, missing those bits.)
                    targets = new List<(uint, ulong, ulong)>
                    {
                        (0xc0011020, 0x4480000000000UL, 0UL),
                        (0xc0011021, 0x1c000200000040UL, 0UL),
                        (0xc0011022, 0xc000000401570000UL, 0UL),
                        (0xc001102b, 0x2000cc10UL, 0UL)
                    };
                }
                else if (family == 23) // Zen 1 / Zen 2 (Family 17h / 23 dec)
                {
                    log.LogInformation("msr: applying AMD Zen 1/2 register optimizations...");
                    targets = new List<(uint, ulong, ulong)>
                    {
                        (0xc0011020, 0x0UL, 0UL),
                        (0xc0011021, 0x40UL, 0xffffffffffffffdfUL),
                        (0xc0011022, 0x1510000UL, 0UL),
                        (0xc001102b, 0x2000cc16UL, 0UL)
                    };
                }
                else
                {
                    log.LogWarning("msr: AMD CPU Family {Family} is not supported for MSR tweaks.", family);
                    CloseHandle(_driverHandle);
                    _driverHandle = IntPtr.Zero;
                    UnloadDriver();
                    return;
                }
            }
            else if (cpuVendor == "Intel")
            {
                log.LogInformation("msr: applying Intel prefetcher modifications...");
                targets = new List<(uint, ulong, ulong)>
                {
                    (0x1a4, 15UL, 0UL)
                };
            }
            else
            {
                log.LogWarning("msr: unsupported CPU vendor {Vendor} for MSR presets.", cpuVendor);
                CloseHandle(_driverHandle);
                _driverHandle = IntPtr.Zero;
                UnloadDriver();
                return;
            }

            int logicalCores = Environment.ProcessorCount;
            IntPtr curThread = GetCurrentThread();

            // WinRing0 writes the MSR on whatever core the DeviceIoControl call
            // runs on, so the writer thread must actually be executing on the
            // target core. SetThreadAffinityMask only *requests* a move; a bare
            // Sleep(0) can return on the old core, landing the write on the wrong
            // CPU (a silent source of "MSR enabled but no speedup"). Spin until
            // GetCurrentProcessorNumber() confirms we're on the target core.
            int verified = 0, writeAttempts = 0, wrongCore = 0;
            var mismatches = new List<(uint Reg, ulong Want, ulong Got)>();

            lock (_backup)
            {
                _backup.Clear();

                for (int c = 0; c < logicalCores; c++)
                {
                    IntPtr mask = new IntPtr(1L << c);
                    IntPtr oldMask = SetThreadAffinityMask(curThread, mask);
                    if (oldMask == IntPtr.Zero) continue;

                    try
                    {
                        // Wait until we're genuinely running on core c (bounded spin).
                        bool onCore = false;
                        for (int spin = 0; spin < 1000; spin++)
                        {
                            if (GetCurrentProcessorNumber() == (uint)c) { onCore = true; break; }
                            System.Threading.Thread.Sleep(0);
                        }
                        if (!onCore) { wrongCore++; continue; }

                        foreach (var target in targets)
                        {
                            if (!ReadMsr(target.Reg, out ulong originalVal))
                            {
                                log.LogError("❌ [ERR] msr: failed to read register 0x{Reg:X} on core {Core}. Error: {Error}", target.Reg, c, Marshal.GetLastWin32Error());
                                continue;
                            }
                            _backup.Add(new MsrBackup { CoreIndex = c, Register = target.Reg, Value = originalVal });

                            ulong newVal = target.Mask != 0 ? (originalVal & target.Mask) | target.Val : target.Val;
                            writeAttempts++;
                            if (!WriteMsr(target.Reg, newVal))
                            {
                                log.LogError("❌ [ERR] msr: failed to write register 0x{Reg:X} on core {Core}. Error: {Error}", target.Reg, c, Marshal.GetLastWin32Error());
                                continue;
                            }

                            // Read back and confirm the write actually stuck. This is
                            // what tells us MSR is really taking effect vs silently
                            // no-op'ing (write-protected reg, wrong core, blocked driver).
                            if (ReadMsr(target.Reg, out ulong afterVal) && afterVal == newVal)
                            {
                                verified++;
                            }
                            else if (c == 0)
                            {
                                mismatches.Add((target.Reg, newVal, afterVal));
                            }
                        }
                    }
                    finally
                    {
                        SetThreadAffinityMask(curThread, oldMask);
                    }
                }
            }

            _applied = true;
            _appliedTargets = targets.Select(t => (t.Reg, t.Mask != 0 ? 0UL : t.Val)).Where(t => t.Item2 != 0).ToArray();

            if (wrongCore > 0)
                log.LogWarning("msr: could not bind writer thread to {Count} core(s) — those cores were skipped.", wrongCore);
            foreach (var m in mismatches)
                log.LogWarning("msr: register 0x{Reg:X} did not stick (wrote 0x{Want:X}, read back 0x{Got:X}) — likely write-protected or overridden.", m.Reg, m.Want, m.Got);

            if (verified == 0 && writeAttempts > 0)
            {
                log.LogError("❌ [ERR] msr: {Attempts} write(s) attempted but NONE verified on read-back — MSR tweaks are NOT active (hashrate will be at the no-MSR level). Check the WinRing0 driver loaded and isn't blocked by the Windows vulnerable-driver blocklist.", writeAttempts);
            }
            else
            {
                log.LogInformation("msr: register modifications verified — {Verified}/{Attempts} writes stuck across {Cores} logical cores.", verified, writeAttempts, logicalCores);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "❌ [ERR] msr: error occurred while applying MSR overrides");
            Restore();
        }
    }

    /// <summary>Re-read the applied MSRs across all cores while mining is live and
    /// report how many still hold the intended value. Diagnoses the "verified at
    /// apply-time but no hashrate gain" case: if the values reverted, some CPU
    /// event (e.g. a C-state transition on a briefly-idle core) is resetting them.
    /// Best-effort, Windows-only; does nothing if MSR wasn't applied.</summary>
    public static void Reverify(ILogger log)
    {
        if (!_applied || _driverHandle == IntPtr.Zero || _appliedTargets.Length == 0) return;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            int logicalCores = Environment.ProcessorCount;
            IntPtr curThread = GetCurrentThread();
            foreach (var (reg, want) in _appliedTargets)
            {
                int hold = 0, checkedCores = 0;
                for (int c = 0; c < logicalCores; c++)
                {
                    IntPtr oldMask = SetThreadAffinityMask(curThread, new IntPtr(1L << c));
                    if (oldMask == IntPtr.Zero) continue;
                    try
                    {
                        bool onCore = false;
                        for (int spin = 0; spin < 1000; spin++)
                        {
                            if (GetCurrentProcessorNumber() == (uint)c) { onCore = true; break; }
                            System.Threading.Thread.Sleep(0);
                        }
                        if (!onCore) continue;
                        checkedCores++;
                        if (ReadMsr(reg, out ulong v) && v == want) hold++;
                    }
                    finally { SetThreadAffinityMask(curThread, oldMask); }
                }

                if (hold == checkedCores)
                    log.LogInformation("msr: re-check 0x{Reg:X} — still set on all {N} cores.", reg, checkedCores);
                else
                    log.LogWarning("msr: re-check 0x{Reg:X} — REVERTED on {Lost}/{N} cores (only {Hold} still hold 0x{Want:X}). A CPU event is resetting it.", reg, checkedCores - hold, checkedCores, hold, want);
            }
        }
        catch { /* diagnostic only */ }
    }

    public static void Restore()
    {
        if (!_applied) return;

        try
        {
            if (_driverHandle != IntPtr.Zero && _driverHandle.ToInt64() != -1)
            {
                int logicalCores = Environment.ProcessorCount;
                IntPtr curThread = GetCurrentThread();

                lock (_backup)
                {
                    foreach (var backup in _backup)
                    {
                        IntPtr mask = new IntPtr(1L << backup.CoreIndex);
                        IntPtr oldMask = SetThreadAffinityMask(curThread, mask);
                        if (oldMask == IntPtr.Zero) continue;

                        try
                        {
                            System.Threading.Thread.Sleep(0);
                            WriteMsr(backup.Register, backup.Value);
                        }
                        finally
                        {
                            SetThreadAffinityMask(curThread, oldMask);
                        }
                    }
                    _backup.Clear();
                }

                CloseHandle(_driverHandle);
                _driverHandle = IntPtr.Zero;
            }

            UnloadDriver();
            _applied = false;
        }
        catch
        {
            // Suppress exception during process teardown
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static (string Vendor, int Family) DetectCpu()
    {
        string vendor = "Unknown";
        int family = 0;

        try
        {
            string? name = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
            if (name != null)
            {
                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)) vendor = "AMD";
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) vendor = "Intel";
            }

            string? id = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier", null) as string;
            if (id != null)
            {
                // Format: "Intel64 Family 6 Model 158 Stepping 10" or "AMD64 Family 25 Model 1 Stepping 1"
                string[] parts = id.Split(' ');
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Equals("Family", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(parts[i + 1], out family);
                        break;
                    }
                }
            }
        }
        catch
        {
            // Fallback defaults
        }

        return (vendor, family);
    }

    private static bool LoadDriver(string path, ILogger log)
    {
        IntPtr scm = OpenSCManagerW(null!, null!, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return false;

        try
        {
            IntPtr service = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (service == IntPtr.Zero)
            {
                service = CreateServiceW(
                    scm,
                    ServiceName,
                    ServiceName,
                    SERVICE_ALL_ACCESS,
                    SERVICE_KERNEL_DRIVER,
                    SERVICE_DEMAND_START,
                    SERVICE_ERROR_NORMAL,
                    path,
                    null!,
                    IntPtr.Zero,
                    null!,
                    null!,
                    null!);
            }

            if (service == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 183) // 183 = ERROR_ALREADY_EXISTS, which is okay
                {
                    log.LogError("msr: failed to create SCM service entry. Error={Error}", err);
                    return false;
                }
                service = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
            }

            if (service != IntPtr.Zero)
            {
                try
                {
                    bool started = StartServiceW(service, 0, IntPtr.Zero);
                    if (!started)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 1056) // 1056 = Service already running, which is okay
                        {
                            log.LogError("msr: failed to start WinRing0 driver service. Error={Error}", err);
                            return false;
                        }
                    }
                    return true;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
        return false;
    }

    private static void UnloadDriver()
    {
        IntPtr scm = OpenSCManagerW(null!, null!, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return;

        try
        {
            IntPtr service = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (service != IntPtr.Zero)
            {
                try
                {
                    var status = new SERVICE_STATUS();
                    ControlService(service, SERVICE_CONTROL_STOP, ref status);
                    DeleteService(service);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static bool ReadMsr(uint reg, out ulong val)
    {
        val = 0;
        if (_driverHandle == IntPtr.Zero || _driverHandle.ToInt64() == -1) return false;

        uint bytesReturned;
        return DeviceIoControl(
            _driverHandle,
            IOCTL_OLS_READ_MSR,
            ref reg,
            sizeof(uint),
            out val,
            sizeof(ulong),
            out bytesReturned,
            IntPtr.Zero);
    }

    private static bool WriteMsr(uint reg, ulong val)
    {
        if (_driverHandle == IntPtr.Zero || _driverHandle.ToInt64() == -1) return false;

        var input = new OlsMsrInput
        {
            RegisterAddress = reg,
            ValueEAX = (uint)(val & 0xFFFFFFFFUL),
            ValueEDX = (uint)(val >> 32)
        };

        uint bytesReturned;
        return DeviceIoControl(
            _driverHandle,
            IOCTL_OLS_WRITE_MSR,
            ref input,
            (uint)Marshal.SizeOf<OlsMsrInput>(),
            IntPtr.Zero,
            0,
            out bytesReturned,
            IntPtr.Zero);
    }
}
