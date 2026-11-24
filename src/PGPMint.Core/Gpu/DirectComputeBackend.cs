using System.Runtime.InteropServices;
using PGPMint.Core.Search;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PGPMint.Core.Gpu;

public sealed class DirectComputeBackend : IGpuBackend
{
    private readonly string? _shaderCacheDirectory;

    public DirectComputeBackend(string? shaderCacheDirectory = null)
    {
        _shaderCacheDirectory = shaderCacheDirectory ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint", "shader-cache");
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        var results = new List<GpuDeviceInfo>();
        var hardwareCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            using (adapter)
            {
                AdapterDescription1 desc = adapter.Description1;

                // Strictly exclude software/CPU renderers (WARP) and remote adapters — GPU-only requirement
                if ((desc.Flags & (AdapterFlags.Software | AdapterFlags.Remote)) != 0)
                {
                    continue;
                }

                if (desc.VendorId == 0x1414 && desc.DeviceId == 0x008C)
                {
                    continue;
                }

                // Exclude WDDM indirect/virtual display adapters (e.g., Virtual Desktop, SuperDisplay, Parsec)
                // and display-only adapters that proxy compute to another physical GPU.
                if (!IsHardwareComputeAdapter(desc.Luid.LowPart, desc.Luid.HighPart))
                {
                    continue;
                }

                // Probe real D3D11 Feature Level 11_0 compute capability
                if (!CanCreateComputeDevice(adapter))
                {
                    continue;
                }

                string hwPrefix = $"{desc.VendorId:X4}:{desc.DeviceId:X4}:{desc.SubsystemId:X8}:{desc.Revision:X2}";
                hardwareCounts.TryGetValue(hwPrefix, out int instance);
                hardwareCounts[hwPrefix] = instance + 1;

                string deviceKey = $"{hwPrefix}:{instance}";
                long luid = ((long)desc.Luid.HighPart << 32) | (uint)desc.Luid.LowPart;
                string driverVersion = TryGetDriverVersion(adapter);
                ulong dedicatedBytes = (ulong)desc.DedicatedVideoMemory;
                bool isDiscrete = dedicatedBytes >= 512UL * 1024UL * 1024UL;

                results.Add(new GpuDeviceInfo(
                    DeviceKey: deviceKey,
                    AdapterLuid: luid,
                    Name: desc.Description.Trim(),
                    VendorId: desc.VendorId,
                    VendorDeviceId: desc.DeviceId,
                    SubSysId: desc.SubsystemId,
                    Revision: desc.Revision,
                    DedicatedVideoMemoryBytes: dedicatedBytes,
                    DriverVersion: driverVersion,
                    IsDiscrete: isDiscrete) { KernelConfiguration = DirectComputeDevice.DefaultConfiguration });
            }
        }

        // Order discrete / higher-VRAM GPUs first, keeping deterministic ordering
        return results
            .OrderByDescending(g => g.IsDiscrete)
            .ThenByDescending(g => g.DedicatedVideoMemoryBytes)
            .ThenBy(g => g.DeviceKey, StringComparer.Ordinal)
            .ToList();
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? matchedAdapter = null;

        for (uint index = 0; factory.EnumAdapters1(index, out IDXGIAdapter1 adapter).Success; index++)
        {
            AdapterDescription1 desc = adapter.Description1;
            long luid = ((long)desc.Luid.HighPart << 32) | (uint)desc.Luid.LowPart;
            if (luid == deviceInfo.AdapterLuid)
            {
                matchedAdapter = adapter;
                break;
            }
            adapter.Dispose();
        }

        if (matchedAdapter is null)
        {
            throw new InvalidOperationException($"GPU adapter '{deviceInfo.Name}' is no longer available.");
        }

        try
        {
            return new DirectComputeDevice(matchedAdapter, deviceInfo, _shaderCacheDirectory);
        }
        catch
        {
            matchedAdapter.Dispose();
            throw;
        }
    }

    private static bool CanCreateComputeDevice(IDXGIAdapter1 adapter)
    {
        try
        {
            FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var hr = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.None,
                levels,
                out ID3D11Device? device,
                out FeatureLevel actualLevel,
                out ID3D11DeviceContext? context);

            context?.Dispose();
            device?.Dispose();
            return hr.Success && actualLevel >= FeatureLevel.Level_11_0;
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice), out long umdVersion) && umdVersion != 0)
            {
                ushort v1 = (ushort)((umdVersion >> 48) & 0xFFFF);
                ushort v2 = (ushort)((umdVersion >> 32) & 0xFFFF);
                ushort v3 = (ushort)((umdVersion >> 16) & 0xFFFF);
                ushort v4 = (ushort)(umdVersion & 0xFFFF);
                return $"{v1}.{v2}.{v3}.{v4}";
            }
        }
        catch
        {
            // Some adapters decline CheckInterfaceSupport; fall back cleanly.
        }

        return "unknown";
    }

    private static bool IsHardwareComputeAdapter(uint luidLow, int luidHigh)
    {
        const int KmtQaiTypeAdapterType = 15;
        const uint FlagRenderSupported = 0x00000001u;
        const uint FlagSoftwareDevice = 0x00000004u;
        const uint FlagIndirectDisplayDevice = 0x00000040u;
        const uint FlagComputeOnly = 0x00000800u;

        try
        {
            var openArg = new D3dKmtOpenAdapterFromLuid
            {
                AdapterLuid = new Win32Luid { LowPart = luidLow, HighPart = luidHigh }
            };

            if (D3DKMTOpenAdapterFromLuid(ref openArg) != 0 || openArg.Handle == 0)
            {
                return true; // Fallback to DXGI + D3D11 probe if kernel query is unavailable
            }

            try
            {
                uint adapterTypeFlags = 0;
                unsafe
                {
                    var query = new D3dKmtQueryAdapterInfo
                    {
                        Handle = openArg.Handle,
                        Type = KmtQaiTypeAdapterType,
                        PrivateDriverData = (IntPtr)(&adapterTypeFlags),
                        PrivateDriverDataSize = sizeof(uint)
                    };

                    if (D3DKMTQueryAdapterInfo(ref query) != 0)
                    {
                        return true;
                    }
                }

                if ((adapterTypeFlags & FlagSoftwareDevice) != 0)
                {
                    return false;
                }

                if ((adapterTypeFlags & FlagIndirectDisplayDevice) != 0)
                {
                    return false;
                }

                return (adapterTypeFlags & (FlagRenderSupported | FlagComputeOnly)) != 0;
            }
            finally
            {
                var closeArg = new D3dKmtCloseAdapter { Handle = openArg.Handle };
                D3DKMTCloseAdapter(ref closeArg);
            }
        }
        catch
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromLuid { public Win32Luid AdapterLuid; public uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryAdapterInfo
    {
        public uint Handle;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtCloseAdapter { public uint Handle; }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3dKmtOpenAdapterFromLuid arg);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dKmtQueryAdapterInfo arg);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3dKmtCloseAdapter arg);
}
