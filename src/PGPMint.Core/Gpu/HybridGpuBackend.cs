using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

/// <summary>
/// Universal Windows hardware GPU backend:
/// - Discovers real physical hardware GPUs via DXGI + WDDM (filtering CPU/WARP, remote, and virtual display adapters).
/// - Uses native zero-dependency Vulkan Compute (vulkan-1.dll + SPIR-V 1.3) by default, promoting native OpenCL (OpenCL.dll) when a compute-bound probe shows OpenCL outperforming Vulkan by at least 15%.
/// - Cascades automatically to native OpenCL (OpenCL.dll) when Vulkan is unavailable or fails.
/// - Falls back to DirectCompute (D3D11 HLSL) on any Windows GPU without Vulkan 1.1+ or OpenCL compute support.
/// </summary>
public sealed class HybridGpuBackend : IGpuBackend, IWorkloadGpuBackend
{
    internal const double OpenClPreferenceMargin = 1.15;

    private readonly DirectComputeBackend _directCompute;
    private readonly string _openClCacheDirectory;
    private readonly string _vulkanCacheDirectory;
    private readonly bool _enableOpenCl;
    private readonly bool _enableVulkan;
    private readonly Func<IGpuSearchDevice, KeyIdPattern, double> _probeMeasure;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, OpenClAdapterDescriptor> _openClMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VulkanAdapterDescriptor> _vulkanMap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _preferOpenCl = new(StringComparer.Ordinal);
    private KeyIdPattern _workload = KeyIdPattern.Parse("1145141919810");
    private bool _enumerated;

    public HybridGpuBackend(string? shaderCacheDirectory = null)
        : this(shaderCacheDirectory, enableOpenCl: true, enableVulkan: true)
    {
    }

    internal HybridGpuBackend(
        string? shaderCacheDirectory,
        bool enableOpenCl,
        bool enableVulkan,
        Func<IGpuSearchDevice, KeyIdPattern, double>? probeMeasure = null)
    {
        _directCompute = new DirectComputeBackend(shaderCacheDirectory);
        string cacheDirectory = shaderCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint", "shader-cache");
        _openClCacheDirectory = Path.Combine(cacheDirectory, "opencl");
        _vulkanCacheDirectory = Path.Combine(cacheDirectory, "vulkan");
        _enableOpenCl = enableOpenCl;
        _enableVulkan = enableVulkan;
        _probeMeasure = probeMeasure ?? PortableGpuProbe.Measure;
    }

    public void SetWorkload(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        lock (_mapLock)
        {
            _workload = pattern;
        }
    }

    internal static bool ShouldPreferOpenCl(double vulkanScore, double openClScore) =>
        double.IsFinite(openClScore) && openClScore > 0.0 &&
        (!double.IsFinite(vulkanScore) || vulkanScore <= 0.0 || openClScore >= vulkanScore * OpenClPreferenceMargin);

    private static string SelectionKey(string deviceKey, KeyIdPattern workload) =>
        $"{deviceKey}|{(workload.FixedHexLength <= 4 ? "short" : workload.FixedHexLength <= 8 ? "mid" : "long")}";

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

                if (matchedVulkan is not null && matchedOpenCl is not null &&
                    _preferOpenCl.TryGetValue(SelectionKey(hw.DeviceKey, _workload), out bool preferCl) && preferCl)
                {
                    results.Add(WithOpenClMetadata(hw, matchedOpenCl));
                }
                else if (matchedVulkan is not null)
                {
                    results.Add(WithVulkanMetadata(hw, matchedVulkan));
                }
                else if (matchedOpenCl is not null)
                {
                    results.Add(WithOpenClMetadata(hw, matchedOpenCl));
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
        KeyIdPattern workload;
        bool hasCachedChoice;
        bool cachedPreferOpenCl;
        lock (_mapLock)
        {
            if (_enableVulkan)
            {
                _vulkanMap.TryGetValue(deviceInfo.DeviceKey, out vulkan);
            }

            if (_enableOpenCl)
            {
                _openClMap.TryGetValue(deviceInfo.DeviceKey, out openCl);
            }

            workload = _workload;
            hasCachedChoice = _preferOpenCl.TryGetValue(SelectionKey(deviceInfo.DeviceKey, workload), out cachedPreferOpenCl);
        }

        string selectionKey = SelectionKey(deviceInfo.DeviceKey, workload);

        if (vulkan is not null && openCl is not null)
        {
            if (hasCachedChoice)
            {
                if (cachedPreferOpenCl)
                {
                    IGpuSearchDevice? preferredCl = TryCreateOpenClDevice(deviceInfo, openCl, selectionKey);
                    if (preferredCl is not null) return preferredCl;
                }
                else
                {
                    IGpuSearchDevice? preferredVk = TryCreateVulkanDevice(deviceInfo, vulkan, selectionKey);
                    if (preferredVk is not null) return preferredVk;
                }
            }
            else
            {
                (IGpuSearchDevice? vkDev, double vkScore) = TryProbeVulkan(deviceInfo, vulkan);
                (IGpuSearchDevice? clDev, double clScore) = TryProbeOpenCl(deviceInfo, openCl);
                if (vkDev is not null && clDev is not null)
                {
                    bool preferOpenCl = ShouldPreferOpenCl(vkScore, clScore);
                    lock (_mapLock)
                    {
                        _preferOpenCl[selectionKey] = preferOpenCl;
                    }

                    if (preferOpenCl)
                    {
                        vkDev.Dispose();
                        return clDev;
                    }

                    clDev.Dispose();
                    return vkDev;
                }

                if (vkDev is not null) return vkDev;
                if (clDev is not null) return clDev;

                return CreateDirectComputeDevice(deviceInfo);
            }
        }

        lock (_mapLock)
        {
            vulkan = _enableVulkan && _vulkanMap.TryGetValue(deviceInfo.DeviceKey, out VulkanAdapterDescriptor? vkCurrent) ? vkCurrent : null;
            openCl = _enableOpenCl && _openClMap.TryGetValue(deviceInfo.DeviceKey, out OpenClAdapterDescriptor? clCurrent) ? clCurrent : null;
        }

        if (vulkan is not null)
        {
            IGpuSearchDevice? vkDev = TryCreateVulkanDevice(deviceInfo, vulkan, selectionKey);
            if (vkDev is not null) return vkDev;
        }

        if (openCl is not null)
        {
            IGpuSearchDevice? clDev = TryCreateOpenClDevice(deviceInfo, openCl, selectionKey);
            if (clDev is not null) return clDev;
        }

        return CreateDirectComputeDevice(deviceInfo);
    }

    private IGpuSearchDevice? TryCreateVulkanDevice(GpuDeviceInfo deviceInfo, VulkanAdapterDescriptor vulkan, string selectionKey)
    {
        try
        {
            return new VulkanDevice(vulkan, WithVulkanMetadata(deviceInfo, vulkan), _vulkanCacheDirectory);
        }
        catch
        {
            // Permanently drop failed Vulkan pairing for this session so retries cascade to OpenCL / DirectCompute
            lock (_mapLock)
            {
                _vulkanMap.Remove(deviceInfo.DeviceKey);
                _preferOpenCl.Remove(selectionKey);
            }
            return null;
        }
    }

    private IGpuSearchDevice? TryCreateOpenClDevice(GpuDeviceInfo deviceInfo, OpenClAdapterDescriptor openCl, string selectionKey)
    {
        try
        {
            return new OpenClDevice(openCl.DeviceId, WithOpenClMetadata(deviceInfo, openCl), openCl.SupportsFillBuffer, _openClCacheDirectory, openCl.MaxParameterSize);
        }
        catch
        {
            // Permanently drop failed OpenCL pairing for this session so retries cascade cleanly
            lock (_mapLock)
            {
                _openClMap.Remove(deviceInfo.DeviceKey);
                _preferOpenCl.Remove(selectionKey);
            }
            return null;
        }
    }

    private (IGpuSearchDevice? Device, double Score) TryProbeVulkan(GpuDeviceInfo deviceInfo, VulkanAdapterDescriptor vulkan)
    {
        IGpuSearchDevice? dev = null;
        try
        {
            dev = new VulkanDevice(vulkan, WithVulkanMetadata(deviceInfo, vulkan), _vulkanCacheDirectory);
            double score = _probeMeasure(dev, _workload);
            if (!(score > 0.0) || !double.IsFinite(score))
            {
                throw new InvalidOperationException("GPU probe returned an invalid rate.");
            }
            return (dev, score);
        }
        catch
        {
            dev?.Dispose();
            lock (_mapLock)
            {
                _vulkanMap.Remove(deviceInfo.DeviceKey);
            }
            return (null, 0.0);
        }
    }

    private (IGpuSearchDevice? Device, double Score) TryProbeOpenCl(GpuDeviceInfo deviceInfo, OpenClAdapterDescriptor openCl)
    {
        IGpuSearchDevice? dev = null;
        try
        {
            dev = new OpenClDevice(openCl.DeviceId, WithOpenClMetadata(deviceInfo, openCl), openCl.SupportsFillBuffer, _openClCacheDirectory, openCl.MaxParameterSize);
            double score = _probeMeasure(dev, _workload);
            if (!(score > 0.0) || !double.IsFinite(score))
            {
                throw new InvalidOperationException("GPU probe returned an invalid rate.");
            }
            return (dev, score);
        }
        catch
        {
            dev?.Dispose();
            lock (_mapLock)
            {
                _openClMap.Remove(deviceInfo.DeviceKey);
            }
            return (null, 0.0);
        }
    }

    private IGpuSearchDevice CreateDirectComputeDevice(GpuDeviceInfo deviceInfo) =>
        _directCompute.CreateDevice(deviceInfo with
        {
            BackendName = "DirectCompute",
            BackendDriverVersion = null,
            KernelConfiguration = DirectComputeDevice.DefaultConfiguration,
            KernelVersion = DirectComputeShaderSource.KernelVersion
        });

    private static GpuDeviceInfo WithVulkanMetadata(GpuDeviceInfo info, VulkanAdapterDescriptor vulkan)
    {
        uint wg = Math.Clamp(Math.Min(vulkan.MaxWorkgroupSizeX, vulkan.MaxWorkgroupInvocations),
            1u, (uint)VulkanSpirvSource.DefaultWorkgroupSize);
        return info with
        {
            BackendName = "Vulkan",
            BackendDriverVersion = vulkan.DriverVersion,
            KernelConfiguration = VulkanDevice.ConfigurationFor(wg),
            KernelVersion = VulkanSpirvSource.KernelVersion
        };
    }

    private static GpuDeviceInfo WithOpenClMetadata(GpuDeviceInfo info, OpenClAdapterDescriptor openCl) =>
        info with
        {
            BackendName = "OpenCL",
            BackendDriverVersion = openCl.DriverVersion,
            KernelConfiguration = OpenClDevice.ConfigurationFor(openCl.MaxWorkgroupSize, openCl.MaxParameterSize >= OpenClDevice.ScheduleParamBytes),
            KernelVersion = OpenClKernelSource.KernelVersion
        };

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
