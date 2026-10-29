using System.Runtime.InteropServices;

namespace Thermal.Telemetry;

/// <summary>Maps adapter LUIDs (as they appear in the "GPU Engine" perf counters) to adapter names.</summary>
internal static class Dxgi
{
    public record Adapter(string Luid, string Name, ulong DedicatedVideo, ulong SharedSystem, uint VendorId);

    public static List<Adapter> Enumerate()
    {
        var list = new List<Adapter>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref iid, out var factoryObj) != 0) return list;
        var factory = (IDXGIFactory1)factoryObj;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                adapter.GetDesc1(out var d);
                Marshal.ReleaseComObject(adapter);
                if ((d.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE (Basic Render Driver)
                var luid = $"luid_0x{(uint)d.LuidHigh:X8}_0x{d.LuidLow:X8}";
                list.Add(new Adapter(luid, d.Description, (ulong)d.DedicatedVideoMemory, (ulong)d.SharedSystemMemory, d.VendorId));
            }
        }
        finally { Marshal.ReleaseComObject(factory); }
        return list;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
