using Akoya.Cuda;

namespace Akoya.Miner.Observability;

/// <summary>
/// Records which physical card sits in each metrics slot, by PCI address.
///
/// This is what lets the sysfs hwmon sensors (temperature, fan, energy) be
/// attached to the right worker row: hwmon keys its nodes by PCI address, and
/// without one we would be matching on enumeration order and could report card
/// A's temperature against card B.
///
/// Lives here, shared, because every GPU algo needs it. The first version wired
/// it into the prl orchestrator only, so a csd or btx run silently showed no
/// sensors at all — the mapping was simply never recorded.
/// </summary>
internal static class GpuIdentity
{
    /// <summary>The shim's "unknown device" answer. An older native build
    /// returns this for every device, so treat it as no answer rather than
    /// mapping every slot onto one card.</summary>
    internal const string PlaceholderPci = "0000:00:00.0";

    /// <summary>Record PCI addresses for a set of device ordinals. The list is
    /// in slot order — index in the list is the metrics slot, the value is the
    /// driver's device ordinal, which differ whenever devices were filtered
    /// (an iGPU skipped, an explicit device list).</summary>
    public static void RecordPciAddresses(IReadOnlyList<int> deviceOrdinals)
    {
        if (deviceOrdinals is null) return;
        for (int slot = 0; slot < deviceOrdinals.Count; slot++)
        {
            var pci = TryGetPciAddress(deviceOrdinals[slot]);
            if (!string.IsNullOrEmpty(pci) && pci != PlaceholderPci)
                Metrics.SetGpuPciAddress(slot, pci);
        }
    }

    /// <summary>PCI address of a device ("0000:05:00.0"), or "" if the driver or
    /// shim will not tell us. Never throws — this is decoration, and a rig must
    /// not fail to start because a sensor mapping was unavailable.</summary>
    public static string TryGetPciAddress(int ordinal)
    {
        try
        {
            if (CudaDriver.DeviceGet(out var dev, ordinal) != CUresult.Success) return "";
            Span<byte> buf = stackalloc byte[64];
            if (CudaDriver.DeviceGetPCIBusId(buf, buf.Length, dev) != CUresult.Success) return "";
            int len = buf.IndexOf((byte)0);
            if (len <= 0) return "";
            return System.Text.Encoding.UTF8.GetString(buf[..len]).Trim();
        }
        catch { return ""; }
    }
}
