using System.Text;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal sealed record OpenClAdapterDescriptor(
    nint DeviceId,
    string Name,
    uint VendorId,
    string DriverVersion,
    ulong GlobalMemoryBytes,
    long? Luid,
    bool SupportsFillBuffer,
    nuint MaxWorkgroupSize,
    nuint MaxParameterSize)
{
    public string? DeviceUuid { get; init; }
    public string? PciAddress { get; init; }
    public bool IsDiscrete { get; init; }

    internal GpuDeviceInfo ToDeviceInfo(int workgroupSize = 128)
    {
        string key = GpuDeviceIdentity.Key(DeviceUuid, PciAddress, Luid);
        return new GpuDeviceInfo(key.Length == 0 ? $"opencl-session:{DeviceId:x}" : key,
            Luid ?? 0, Name, VendorId, 0, 0, 0, IsDiscrete ? GlobalMemoryBytes : 0, DriverVersion, IsDiscrete,
            "OpenCL", OpenClKernelSource.KernelVersion)
        {
            DeviceUuid = DeviceUuid, PciAddress = PciAddress, BackendDriverVersion = DriverVersion,
            KernelConfiguration = OpenClDevice.ConfigurationFor(MaxWorkgroupSize, MaxParameterSize >= OpenClDevice.ScheduleParamBytes, workgroupSize)
        };
    }
}

internal static unsafe class OpenClEnumerator
{
    public static IReadOnlyList<OpenClAdapterDescriptor> TryEnumerateGpuAdapters(Action<string>? report = null)
    {
        var results = new List<OpenClAdapterDescriptor>();
        if (!OpenClNative.IsAvailable())
        {
            report?.Invoke("OpenCL loader is unavailable; install the platform OpenCL loader and a hardware GPU ICD.");
            return results;
        }

        try
        {
            uint platformCount = 0;
            if (OpenClNative.clGetPlatformIDs(0, null, &platformCount) != OpenClNative.ClSuccess || platformCount == 0)
            {
                return results;
            }

            nint[] platforms = new nint[platformCount];
            fixed (nint* platPtr = platforms)
            {
                if (OpenClNative.clGetPlatformIDs(platformCount, platPtr, null) != OpenClNative.ClSuccess)
                {
                    return results;
                }
            }

            foreach (nint platform in platforms)
            {
                uint devCount = 0;
                int err = OpenClNative.clGetDeviceIDs(platform, OpenClNative.ClDeviceTypeGpu, 0, null, &devCount);
                if (err != OpenClNative.ClSuccess || devCount == 0)
                {
                    continue;
                }

                nint[] devs = new nint[devCount];
                fixed (nint* devPtr = devs)
                {
                    if (OpenClNative.clGetDeviceIDs(platform, OpenClNative.ClDeviceTypeGpu, devCount, devPtr, null) != OpenClNative.ClSuccess)
                    {
                        continue;
                    }
                }

                foreach (nint dev in devs)
                {
                    string name = GetDeviceString(dev, OpenClNative.ClDeviceName).Trim();
                    string driver = GetDeviceString(dev, OpenClNative.ClDriverVersion).Trim();
                    string version = GetDeviceString(dev, OpenClNative.ClDeviceVersion).Trim();
                    string extensions = GetDeviceString(dev, OpenClNative.ClDeviceExtensions);
                    ulong deviceType = 0;
                    if (OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceType, sizeof(ulong), &deviceType, null) != 0 ||
                        (deviceType & OpenClNative.ClDeviceTypeGpu) == 0 || (deviceType & 2UL) != 0 || IsSoftware(name)) continue;

                    uint vendorId = 0;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceVendorId, (nuint)sizeof(uint), &vendorId, null);

                    ulong globalMem = 0;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceGlobalMemSize, (nuint)sizeof(ulong), &globalMem, null);

                    nuint maxWorkgroupSize = OpenClDevice.DefaultWorkgroupSize;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceMaxWorkGroupSize, (nuint)sizeof(nuint),
                        &maxWorkgroupSize, null);

                    nuint maxParameterSize = 0;
                    if (OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceMaxParameterSize, (nuint)sizeof(nuint),
                        &maxParameterSize, null) != OpenClNative.ClSuccess)
                    {
                        maxParameterSize = 1024;
                    }

                    long? luid = TryReadLuid(dev, extensions);
                    bool supportsFill = OpenClNative.SupportsFillBufferExport() && IsOpenCl12OrNewer(version);
                    uint unifiedMemory = 1;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceHostUnifiedMemory, sizeof(uint), &unifiedMemory, null);
                    results.Add(new OpenClAdapterDescriptor(dev, name, vendorId, driver, globalMem, luid, supportsFill, maxWorkgroupSize, maxParameterSize)
                    {
                        DeviceUuid = TryReadUuid(dev, extensions), PciAddress = TryReadPci(dev, extensions),
                        IsDiscrete = unifiedMemory == 0
                    });
                }
            }
        }
        catch (Exception ex)
        {
            report?.Invoke($"OpenCL enumeration failed: {ex.Message}");
        }

        return results;
    }

    internal static bool IsSoftware(string name) =>
        new[] { "llvmpipe", "lavapipe", "swiftshader", "software rasterizer" }
            .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool HasExtension(string extensions, string extension) =>
        extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(extension, StringComparer.Ordinal);

    private static string? TryReadUuid(nint device, string extensions)
    {
        if (!HasExtension(extensions, "cl_khr_device_uuid")) return null;
        byte* bytes = stackalloc byte[16];
        return OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDeviceUuidKhr, 16, bytes, null) == 0
            ? GpuDeviceIdentity.NormalizeUuid(new ReadOnlySpan<byte>(bytes, 16)) : null;
    }

    private static string? TryReadPci(nint device, string extensions)
    {
        if (!HasExtension(extensions, "cl_khr_pci_bus_info")) return null;
        uint* pci = stackalloc uint[4];
        if (OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDevicePciBusInfoKhr, 16, pci, null) != 0) return null;
        return $"{pci[0]:x4}:{pci[1]:x2}:{pci[2]:x2}.{pci[3]:x}";
    }

    private static string GetDeviceString(nint device, uint paramName)
    {
        nuint size = 0;
        if (OpenClNative.clGetDeviceInfo(device, paramName, 0, null, &size) != OpenClNative.ClSuccess || size == 0)
        {
            return string.Empty;
        }

        byte[] buf = new byte[(int)size];
        fixed (byte* ptr = buf)
        {
            if (OpenClNative.clGetDeviceInfo(device, paramName, size, ptr, null) != OpenClNative.ClSuccess)
            {
                return string.Empty;
            }
        }

        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len >= 0 ? len : buf.Length);
    }

    private static long? TryReadLuid(nint device, string extensions)
    {
        if (!HasExtension(extensions, "cl_khr_device_uuid"))
        {
            return null;
        }

        uint valid = 0;
        if (OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDeviceLuidValidKhr, (nuint)sizeof(uint), &valid, null) != OpenClNative.ClSuccess || valid == 0)
        {
            return null;
        }

        byte[] luidBytes = new byte[8];
        fixed (byte* ptr = luidBytes)
        {
            if (OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDeviceLuidKhr, 8, ptr, null) != OpenClNative.ClSuccess)
            {
                return null;
            }
        }

        uint low = BitConverter.ToUInt32(luidBytes, 0);
        int high = BitConverter.ToInt32(luidBytes, 4);
        return ((long)high << 32) | low;
    }

    private static bool IsOpenCl12OrNewer(string deviceVersion)
    {
        // Standard OpenCL format: "OpenCL <major>.<minor> ..."
        string[] parts = deviceVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        string[] ver = parts[1].Split('.');
        if (ver.Length < 2 || !int.TryParse(ver[0], out int major) || !int.TryParse(ver[1], out int minor))
        {
            return false;
        }

        return major > 1 || (major == 1 && minor >= 2);
    }
}
