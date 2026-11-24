using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

/// <summary>
/// Universal Windows hardware GPU backend:
/// - Discovers real physical hardware GPUs via DXGI + WDDM (filtering CPU/WARP, remote, and virtual display adapters).
/// - Uses native zero-dependency OpenCL (OpenCL.dll) whenever an OpenCL GPU ICD is present for peak throughput.
/// - Automatically falls back to DirectCompute (D3D11 HLSL) on any Windows GPU without OpenCL ICD or if OpenCL init fails.
/// </summary>
public sealed class HybridGpuBackend : IGpuBackend
{
    private readonly DirectComputeBackend _directCompute;
    private readonly string _openClCacheDirectory;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, OpenClAdapterDescriptor> _openClMap = new(StringComparer.Ordinal);

    public HybridGpuBackend(string? shaderCacheDirectory = null)
    {
        _directCompute = new DirectComputeBackend(shaderCacheDirectory);
        string cacheDirectory = shaderCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint", "shader-cache");
        _openClCacheDirectory = Path.Combine(cacheDirectory, "opencl");
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        IReadOnlyList<GpuDeviceInfo> dxgiHardware = _directCompute.EnumerateHardwareGpus();
        IReadOnlyList<OpenClAdapterDescriptor> openClAdapters = OpenClEnumerator.TryEnumerateGpuAdapters();

        var usedOpenClDevices = new HashSet<nint>();
        var results = new List<GpuDeviceInfo>(dxgiHardware.Count);

        lock (_mapLock)
        {
            _openClMap.Clear();

            foreach (GpuDeviceInfo hw in dxgiHardware)
            {
                OpenClAdapterDescriptor? matched = FindMatchingOpenClAdapter(hw, openClAdapters, usedOpenClDevices);
                if (matched is not null)
                {
                    usedOpenClDevices.Add(matched.DeviceId);
                    _openClMap[hw.DeviceKey] = matched;
                    results.Add(hw with
                    {
                        BackendName = "OpenCL",
                        BackendDriverVersion = matched.DriverVersion,
                        KernelConfiguration = OpenClDevice.ConfigurationFor(matched.MaxWorkgroupSize),
                        KernelVersion = OpenClKernelSource.KernelVersion
                    });
                }
                else
                {
                    results.Add(hw with
                    {
                        BackendName = "DirectCompute",
                        KernelVersion = DirectComputeShaderSource.KernelVersion
                    });
                }
            }
        }

        return results;
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        OpenClAdapterDescriptor? openCl = null;
        lock (_mapLock)
        {
            _openClMap.TryGetValue(deviceInfo.DeviceKey, out openCl);
        }

        if (openCl is not null)
        {
            try
            {
                return new OpenClDevice(openCl.DeviceId, deviceInfo, openCl.SupportsFillBuffer, _openClCacheDirectory);
            }
            catch
            {
                // Permanently drop failed OpenCL pairing for this session so retries go straight to DirectCompute
                lock (_mapLock)
                {
                    _openClMap.Remove(deviceInfo.DeviceKey);
                }
            }
        }

        return _directCompute.CreateDevice(deviceInfo with
        {
            BackendName = "DirectCompute",
            BackendDriverVersion = null,
            KernelConfiguration = DirectComputeDevice.DefaultConfiguration,
            KernelVersion = DirectComputeShaderSource.KernelVersion
        });
    }

    private static OpenClAdapterDescriptor? FindMatchingOpenClAdapter(
        GpuDeviceInfo hw,
        IReadOnlyList<OpenClAdapterDescriptor> candidates,
        HashSet<nint> alreadyClaimed)
    {
        // 1. Exact WDDM AdapterLuid match via cl_khr_device_uuid
        foreach (OpenClAdapterDescriptor c in candidates)
        {
            if (alreadyClaimed.Contains(c.DeviceId))
            {
                continue;
            }

            if (c.Luid.HasValue && c.Luid.Value == hw.AdapterLuid)
            {
                return c;
            }
        }

        // 2. VendorId + normalized adapter name match
        string normHw = NormalizeName(hw.Name);
        foreach (OpenClAdapterDescriptor c in candidates)
        {
            if (alreadyClaimed.Contains(c.DeviceId))
            {
                continue;
            }

            bool vendorMatch = c.VendorId == 0 || c.VendorId == hw.VendorId;
            if (!vendorMatch)
            {
                continue;
            }

            string normCl = NormalizeName(c.Name);
            if (string.IsNullOrEmpty(normCl) || string.IsNullOrEmpty(normHw))
            {
                continue;
            }

            if (string.Equals(normCl, normHw, StringComparison.Ordinal) ||
                normCl.Contains(normHw, StringComparison.Ordinal) ||
                normHw.Contains(normCl, StringComparison.Ordinal))
            {
                return c;
            }
        }

        return null;
    }

    private static string NormalizeName(string name)
    {
        return name
            .Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }
}
