using System.Management;

namespace Thermal.Telemetry;

/// <summary>
/// HP Omen/Victus BIOS WMI interface (root\wmi : hpqBIntM). This is the only reliable source of
/// fan speed on these laptops — the fans hang off the embedded controller, not a Super I/O chip.
/// Command codes follow the OmenMon project's reverse-engineering notes. Requires elevation.
/// </summary>
internal sealed class HpBios : IDisposable
{
    private const uint CmdDefault = 0x20008;
    private static readonly byte[] Sign = "SECU"u8.ToArray();
    private ManagementObject? _bios;
    private ManagementClass? _inClass;

    public bool Available { get; private set; }
    public string? Error { get; private set; }

    public HpBios()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();
            _inClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM hpqBIntM"));
            _bios = search.Get().Cast<ManagementObject>().FirstOrDefault();
            Available = _bios != null && FanLevels() != null;
        }
        catch (Exception e) { Error = e.Message; Available = false; }
    }

    /// <summary>Fan speeds in RPM: [CPU fan, GPU fan]. The BIOS reports hundreds of RPM.</summary>
    public int[]? FanLevels()
    {
        var d = Send(0x2D, 128);
        return d == null ? null : new[] { d[0] * 100, d[1] * 100 };
    }

    /// <summary>The BIOS thermal sensor (°C) — tracks the CPU closely on Victus boards.</summary>
    public int? Temperature()
    {
        var d = Send(0x23, 4, 0x01);
        return d == null || d[0] == 0 ? null : d[0];
    }

    /// <summary>Raw call for diagnostics: return code and first bytes.</summary>
    public string Probe(uint type, int outSize, byte arg0)
    {
        if (_bios == null || _inClass == null) return "no hpqBIntM";
        try
        {
            using var input = _inClass.CreateInstance();
            input["Sign"] = Sign; input["Command"] = CmdDefault; input["CommandType"] = type; input["Size"] = 4u;
            input["hpqBData"] = new byte[] { arg0, 0, 0, 0 };
            var method = $"hpqBIOSInt{outSize}";
            using var p = _bios.GetMethodParameters(method);
            p["InData"] = input;
            using var r = _bios.InvokeMethod(method, p, null);
            var o = (ManagementBaseObject)r["OutData"];
            var data = o["Data"] as byte[];
            return $"0x{type:X2}/{outSize}/{arg0}: rc={o["rwReturnCode"]} data={string.Join(",", (data ?? Array.Empty<byte>()).Take(8))}";
        }
        catch (Exception e) { return $"0x{type:X2}/{outSize}: {e.Message}"; }
    }

    private byte[]? Send(uint type, int outSize, byte arg0 = 0)
    {
        if (_bios == null || _inClass == null) return null;
        try
        {
            using var input = _inClass.CreateInstance();
            input["Sign"] = Sign;
            input["Command"] = CmdDefault;
            input["CommandType"] = type;
            input["Size"] = 4u;
            input["hpqBData"] = new byte[] { arg0, 0, 0, 0 };
            var method = $"hpqBIOSInt{outSize}";
            using var p = _bios.GetMethodParameters(method);
            p["InData"] = input;
            using var r = _bios.InvokeMethod(method, p, null);
            var o = (ManagementBaseObject)r["OutData"];
            if (Convert.ToUInt32(o["rwReturnCode"]) != 0) return null;
            return (byte[])o["Data"];
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _bios?.Dispose();
        _inClass?.Dispose();
    }
}
