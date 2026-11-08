using System.Runtime.InteropServices;

namespace PhotoLibrary.AI.Clip;

public sealed record GpuDevice(int Index, string Name, long DedicatedMemoryBytes)
{
    public string Label => DedicatedMemoryBytes > 0 ? $"GPU {Index}: {Name} ({DedicatedMemoryBytes / 1_073_741_824.0:0} GB)" : $"GPU {Index}: {Name}";
}

/// <summary>
/// Lists the DirectX 12 adapters DirectML can run on, in DXGI order, which is how ONNX Runtime's DirectML
/// provider numbers them. Software adapters are skipped. Talks to DXGI through raw vtable calls rather than
/// COM interop: the RCW marshaller refuses to QueryInterface the adapters it hands back, and two function
/// pointers are all we need.
/// </summary>
public static unsafe class GpuDevices
{
    public static IReadOnlyList<GpuDevice> List()
    {
        var list = new List<GpuDevice>();
        if (!OperatingSystem.IsWindows()) return list;
        void* factory = null;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(&iid, &factory) != 0 || factory is null) return list;
            var factoryVt = *(void***)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<void*, uint, void**, int>)factoryVt[12]; // IDXGIFactory1::EnumAdapters1
            for (uint i = 0; i < 32; i++)
            {
                void* adapter;
                if (enumAdapters1(factory, i, &adapter) != 0 || adapter is null) break;
                var adapterVt = *(void***)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<void*, DXGI_ADAPTER_DESC1*, int>)adapterVt[10]; // IDXGIAdapter1::GetDesc1
                DXGI_ADAPTER_DESC1 desc;
                var ok = getDesc1(adapter, &desc) == 0;
                ((delegate* unmanaged[Stdcall]<void*, uint>)adapterVt[2])(adapter); // IUnknown::Release
                if (!ok || (desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                list.Add(new GpuDevice((int)i, new string(desc.Description).TrimEnd('\0'), (long)desc.DedicatedVideoMemory));
            }
        }
        catch (DllNotFoundException)
        {
            // No DXGI (a server SKU without a display stack): DirectML won't work either.
        }
        finally
        {
            if (factory is not null) ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)factory)[2])(factory);
        }
        return list;
    }

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* riid, void** factory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
