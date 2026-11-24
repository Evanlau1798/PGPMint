using System.Text;

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
    nuint MaxParameterSize);

internal static unsafe class OpenClEnumerator
{
    public static IReadOnlyList<OpenClAdapterDescriptor> TryEnumerateGpuAdapters()
    {
        var results = new List<OpenClAdapterDescriptor>();
        if (!OpenClNative.IsAvailable())
        {
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
                    results.Add(new OpenClAdapterDescriptor(dev, name, vendorId, driver, globalMem, luid, supportsFill, maxWorkgroupSize, maxParameterSize));
                }
            }
        }
        catch
        {
            // OpenCL is optional accelerator; fail softly and allow DirectCompute fallback
        }

        return results;
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
        if (!extensions.Contains("cl_khr_device_uuid", StringComparison.OrdinalIgnoreCase))
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
