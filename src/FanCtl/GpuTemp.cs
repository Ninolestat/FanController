using System.Runtime.InteropServices;

namespace FanCtl;

/// <summary>
/// GPU temperature straight from the WDDM kernel interface (the source Task Manager uses).
/// No vendor DLLs: replaces NVAPI/NVML, which cost ~23 MB and ~70 ms per read.
/// </summary>
internal sealed unsafe partial class GpuTemp : IDisposable
{
    public string Id { get; }
    public string Name { get; }
    readonly uint _handle;

    GpuTemp(uint handle, string id, string name) { _handle = handle; Id = id; Name = name; }

    [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint hAdapter; public uint LuidLow; public int LuidHigh; public uint NumOfSources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] struct EnumAdapters2 { public uint NumAdapters; public AdapterInfo* pAdapters; }
    [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint hAdapter; public int Type; public void* pData; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] struct PerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature; // Temperature: deci-°C
        public byte PowerStateOverride;
    }

    const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8, KMTQAITYPE_ADAPTERPERFDATA = 62;

    [LibraryImport("gdi32.dll")] private static partial int D3DKMTEnumAdapters2(EnumAdapters2* p);
    [LibraryImport("gdi32.dll")] private static partial int D3DKMTQueryAdapterInfo(QueryInfo* p);
    [LibraryImport("gdi32.dll")] private static partial int D3DKMTCloseAdapter(uint* p);

    public static List<GpuTemp> Enumerate()
    {
        var list = new List<GpuTemp>();
        try
        {
            var e = new EnumAdapters2();
            if (D3DKMTEnumAdapters2(&e) != 0 || e.NumAdapters == 0) return list;
            var buf = new AdapterInfo[e.NumAdapters];
            fixed (AdapterInfo* p = buf)
            {
                e.pAdapters = p;
                if (D3DKMTEnumAdapters2(&e) != 0) return list;
            }
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var a = buf[i];
                string? name = AdapterName(a.hAdapter);
                if (name != null && Read(a.hAdapter) is > 0 && !name.StartsWith("Microsoft", StringComparison.Ordinal))
                    // Use stable enumeration index rather than LUID, which changes across reboots.
                    list.Add(new GpuTemp(a.hAdapter, $"/gpu-wddm/{list.Count}/temperature/0", name));
                else
                {
                    uint h = a.hAdapter;
                    D3DKMTCloseAdapter(&h);
                }
            }
        }
        catch { /* pre-WDDM 2.4 */ }
        return list;
    }

    static string? AdapterName(uint h)
    {
        const int Max = 260;
        char* reg = stackalloc char[Max * 4];
        var q = new QueryInfo { hAdapter = h, Type = KMTQAITYPE_ADAPTERREGISTRYINFO, pData = reg, Size = Max * 4 * sizeof(char) };
        return D3DKMTQueryAdapterInfo(&q) == 0 ? new string(reg).Trim() : null;
    }

    static float? Read(uint h)
    {
        PerfData d;
        var q = new QueryInfo { hAdapter = h, Type = KMTQAITYPE_ADAPTERPERFDATA, pData = &d, Size = (uint)sizeof(PerfData) };
        return D3DKMTQueryAdapterInfo(&q) == 0 ? d.Temperature / 10f : null;
    }

    public float? Read() => Read(_handle);

    public void Dispose()
    {
        uint h = _handle;
        D3DKMTCloseAdapter(&h);
    }
}
