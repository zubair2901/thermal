using System.Management;
using System.Runtime.InteropServices;

namespace Thermal.Telemetry;

/// <summary>
/// Reads a GPU's device power state (D0 = on, D3 = off) from the Plug and Play manager. Unlike asking the NVIDIA
/// driver, this doesn't wake a sleeping GPU, so it can be polled every second on a hybrid-graphics laptop.
/// </summary>
internal sealed class DevicePower
{
    private static readonly Guid PowerDataKey = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private readonly string? _instanceId;

    public DevicePower(ushort vendorId)
    {
        try
        {
            using var s = new ManagementObjectSearcher($"SELECT PNPDeviceID FROM Win32_VideoController WHERE PNPDeviceID LIKE 'PCI\\\\VEN_{vendorId:X4}%'");
            _instanceId = s.Get().Cast<ManagementBaseObject>().Select(o => o["PNPDeviceID"] as string).FirstOrDefault(id => id != null);
        }
        catch { _instanceId = null; }
    }

    public bool Found => _instanceId != null;

    /// <summary>True when the device is in D0 (powered). Null if Windows doesn't report it.</summary>
    public bool? IsOn()
    {
        if (_instanceId == null || CM_Locate_DevNodeW(out var dev, _instanceId, 0) != 0) return null;
        var key = new DEVPROPKEY { fmtid = PowerDataKey, pid = 32 };
        var buf = new byte[64];
        var size = (uint)buf.Length;
        if (CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) != 0 || size < 8) return null;
        // CM_POWER_DATA: PD_Size (4 bytes), then PD_MostRecentPowerState (DEVICE_POWER_STATE: 1 = D0 … 4 = D3).
        return BitConverter.ToInt32(buf, 4) == 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);
}
