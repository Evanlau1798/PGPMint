using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed class VulkanBackend : IGpuBackend
{
    private readonly DirectComputeBackend _dxgiEnumerator;
    private readonly string _cacheDirectory;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, VulkanAdapterDescriptor> _adapterMap = new(StringComparer.Ordinal);

    public VulkanBackend(string? shaderCacheDirectory = null)
    {
        _dxgiEnumerator = new DirectComputeBackend(shaderCacheDirectory);
        _cacheDirectory = shaderCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PGPMint", "shader-cache", "vulkan");
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        IReadOnlyList<GpuDeviceInfo> dxgiHardware = _dxgiEnumerator.EnumerateHardwareGpus();
        IReadOnlyList<VulkanAdapterDescriptor> vulkanAdapters = VulkanEnumerator.TryEnumerateGpuAdapters();

        var claimed = new HashSet<int>();
        var results = new List<GpuDeviceInfo>(dxgiHardware.Count);

        lock (_mapLock)
        {
            _adapterMap.Clear();

            foreach (GpuDeviceInfo hw in dxgiHardware)
            {
                int matchedIndex = FindMatchingAdapterIndex(hw, vulkanAdapters, claimed);
                if (matchedIndex < 0)
                {
                    continue;
                }

                claimed.Add(matchedIndex);
                VulkanAdapterDescriptor adapter = vulkanAdapters[matchedIndex];
                _adapterMap[hw.DeviceKey] = adapter;

                uint wg = Math.Clamp(Math.Min(adapter.MaxWorkgroupSizeX, adapter.MaxWorkgroupInvocations), 1u, (uint)VulkanSpirvSource.DefaultWorkgroupSize);
                results.Add(hw with
                {
                    BackendName = "Vulkan",
                    BackendDriverVersion = adapter.DriverVersion,
                    KernelConfiguration = VulkanDevice.ConfigurationFor(wg),
                    KernelVersion = VulkanSpirvSource.KernelVersion
                });
            }
        }

        return results;
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);

        VulkanAdapterDescriptor? adapter;
        lock (_mapLock)
        {
            _adapterMap.TryGetValue(deviceInfo.DeviceKey, out adapter);
        }

        if (adapter is null)
        {
            EnumerateHardwareGpus();
            lock (_mapLock)
            {
                _adapterMap.TryGetValue(deviceInfo.DeviceKey, out adapter);
            }
        }

        if (adapter is null)
        {
            throw new InvalidOperationException($"Vulkan GPU '{deviceInfo.Name}' is not available.");
        }

        return new VulkanDevice(adapter, deviceInfo, _cacheDirectory);
    }

    internal static int FindMatchingAdapterIndex(
        GpuDeviceInfo hw,
        IReadOnlyList<VulkanAdapterDescriptor> candidates,
        HashSet<int> alreadyClaimed)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (alreadyClaimed.Contains(i))
            {
                continue;
            }

            if (candidates[i].Luid.HasValue && candidates[i].Luid!.Value == hw.AdapterLuid)
            {
                return i;
            }
        }

        string normHw = NormalizeName(hw.Name);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (alreadyClaimed.Contains(i))
            {
                continue;
            }

            VulkanAdapterDescriptor c = candidates[i];
            if (c.VendorId != 0 && c.VendorId != hw.VendorId)
            {
                continue;
            }

            if (c.DeviceId != 0 && c.DeviceId != hw.VendorDeviceId)
            {
                continue;
            }

            string normVk = NormalizeName(c.Name);
            if (string.IsNullOrEmpty(normVk) || string.IsNullOrEmpty(normHw))
            {
                continue;
            }

            if (string.Equals(normVk, normHw, StringComparison.Ordinal) ||
                normVk.Contains(normHw, StringComparison.Ordinal) ||
                normHw.Contains(normVk, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeName(string name) =>
        name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
}
