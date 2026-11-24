using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

/// <summary>
/// Universal Windows hardware GPU backend:
/// - Discovers real physical hardware GPUs via DXGI + WDDM (filtering CPU/WARP, remote, and virtual display adapters).
/// - Uses native zero-dependency OpenCL (OpenCL.dll) whenever an OpenCL GPU ICD is present for peak throughput.
/// - Cascades automatically to native Vulkan Compute (vulkan-1.dll + SPIR-V 1.3) when OpenCL is unavailable or fails.
/// - Falls back to DirectCompute (D3D11 HLSL) on any Windows GPU without OpenCL or Vulkan 1.1+ compute support.
/// </summary>
public sealed class HybridGpuBackend : IGpuBackend
{
    private readonly DirectComputeBackend _directCompute;
    private readonly string _openClCacheDirectory;
    private readonly string _vulkanCacheDirectory;
    private readonly bool _enableOpenCl;
    private readonly bool _enableVulkan;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, OpenClAdapterDescriptor> _openClMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VulkanAdapterDescriptor> _vulkanMap = new(StringComparer.Ordinal);
    private bool _enumerated;

    public HybridGpuBackend(string? shaderCacheDirectory = null)
        : this(shaderCacheDirectory, enableOpenCl: true, enableVulkan: true)
    {
    }

    internal HybridGpuBackend(string? shaderCacheDirectory, bool enableOpenCl, bool enableVulkan)
    {
        _directCompute = new DirectComputeBackend(shaderCacheDirectory);
        string cacheDirectory = shaderCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint", "shader-cache");
        _openClCacheDirectory = Path.Combine(cacheDirectory, "opencl");
        _vulkanCacheDirectory = Path.Combine(cacheDirectory, "vulkan");
        _enableOpenCl = enableOpenCl;
        _enableVulkan = enableVulkan;
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        IReadOnlyList<GpuDeviceInfo> dxgiHardware = _directCompute.EnumerateHardwareGpus();
        IReadOnlyList<OpenClAdapterDescriptor> openClAdapters = _enableOpenCl
            ? OpenClEnumerator.TryEnumerateGpuAdapters()
            : Array.Empty<OpenClAdapterDescriptor>();
        IReadOnlyList<VulkanAdapterDescriptor> vulkanAdapters = _enableVulkan
            ? VulkanEnumerator.TryEnumerateGpuAdapters()
            : Array.Empty<VulkanAdapterDescriptor>();

        var usedOpenClDevices = new HashSet<nint>();
        var usedVulkanIndices = new HashSet<int>();
        var results = new List<GpuDeviceInfo>(dxgiHardware.Count);

        lock (_mapLock)
        {
            _enumerated = true;
            _openClMap.Clear();
            _vulkanMap.Clear();

            foreach (GpuDeviceInfo hw in dxgiHardware)
            {
                OpenClAdapterDescriptor? matchedOpenCl = _enableOpenCl
                    ? FindMatchingOpenClAdapter(hw, openClAdapters, usedOpenClDevices)
                    : null;
                if (matchedOpenCl is not null)
                {
                    usedOpenClDevices.Add(matchedOpenCl.DeviceId);
                    _openClMap[hw.DeviceKey] = matchedOpenCl;
                }

                VulkanAdapterDescriptor? matchedVulkan = null;
                if (_enableVulkan)
                {
                    int vulkanIndex = VulkanBackend.FindMatchingAdapterIndex(hw, vulkanAdapters, usedVulkanIndices);
                    if (vulkanIndex >= 0)
                    {
                        usedVulkanIndices.Add(vulkanIndex);
                        matchedVulkan = vulkanAdapters[vulkanIndex];
                        _vulkanMap[hw.DeviceKey] = matchedVulkan;
                    }
                }

                if (matchedOpenCl is not null)
                {
                    results.Add(hw with
                    {
                        BackendName = "OpenCL",
                        BackendDriverVersion = matchedOpenCl.DriverVersion,
                        KernelConfiguration = OpenClDevice.ConfigurationFor(matchedOpenCl.MaxWorkgroupSize, matchedOpenCl.MaxParameterSize >= OpenClDevice.ScheduleParamBytes),
                        KernelVersion = OpenClKernelSource.KernelVersion
                    });
                }
                else if (matchedVulkan is not null)
                {
                    uint wg = Math.Clamp(Math.Min(matchedVulkan.MaxWorkgroupSizeX, matchedVulkan.MaxWorkgroupInvocations),
                        1u, (uint)VulkanSpirvSource.DefaultWorkgroupSize);
                    results.Add(hw with
                    {
                        BackendName = "Vulkan",
                        BackendDriverVersion = matchedVulkan.DriverVersion,
                        KernelConfiguration = VulkanDevice.ConfigurationFor(wg),
                        KernelVersion = VulkanSpirvSource.KernelVersion
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

        bool needsInitialEnumerate;
        lock (_mapLock)
        {
            needsInitialEnumerate = !_enumerated;
        }

        if (needsInitialEnumerate)
        {
            EnumerateHardwareGpus();
        }

        OpenClAdapterDescriptor? openCl = null;
        VulkanAdapterDescriptor? vulkan = null;
        lock (_mapLock)
        {
            if (_enableOpenCl)
            {
                _openClMap.TryGetValue(deviceInfo.DeviceKey, out openCl);
            }

            if (_enableVulkan)
            {
                _vulkanMap.TryGetValue(deviceInfo.DeviceKey, out vulkan);
            }
        }

        if (openCl is not null)
        {
            try
            {
                return new OpenClDevice(openCl.DeviceId, deviceInfo, openCl.SupportsFillBuffer, _openClCacheDirectory, openCl.MaxParameterSize);
            }
            catch
            {
                // Permanently drop failed OpenCL pairing for this session so retries cascade to Vulkan / DirectCompute
                lock (_mapLock)
                {
                    _openClMap.Remove(deviceInfo.DeviceKey);
                }
            }
        }

        if (vulkan is not null)
        {
            try
            {
                return new VulkanDevice(vulkan, deviceInfo, _vulkanCacheDirectory);
            }
            catch
            {
                // Permanently drop failed Vulkan pairing for this session so retries go straight to DirectCompute
                lock (_mapLock)
                {
                    _vulkanMap.Remove(deviceInfo.DeviceKey);
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
