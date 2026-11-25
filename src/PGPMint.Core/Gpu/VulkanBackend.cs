using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed class VulkanBackend : IGpuBackend
{
    private readonly string _cacheDirectory;
    private readonly int _workgroupSize;
    private readonly object _mapLock = new();
    private readonly Dictionary<string, VulkanAdapterDescriptor> _adapterMap = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    public VulkanBackend(string? shaderCacheDirectory = null, int workgroupSize = 128)
    {
        _workgroupSize = GpuDeviceIdentity.ValidateWorkgroup(workgroupSize);
        _cacheDirectory = shaderCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PGPMint", "shader-cache", "vulkan");
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        var diagnostics = new List<string>();
        IReadOnlyList<VulkanAdapterDescriptor> vulkanAdapters = VulkanEnumerator.TryEnumerateGpuAdapters(diagnostics.Add);
        var results = new List<GpuDeviceInfo>(vulkanAdapters.Count);

        lock (_mapLock)
        {
            _adapterMap.Clear();

            foreach (VulkanAdapterDescriptor adapter in vulkanAdapters)
            {
                GpuDeviceInfo hw = adapter.ToDeviceInfo(_workgroupSize);
                if (hw.DeviceKey.Length == 0)
                {
                    diagnostics.Add($"Vulkan '{adapter.Name}' has no reliable hardware identity (UUID, PCI or LUID); cannot safely reopen this GPU.");
                    continue;
                }
                if (_adapterMap.ContainsKey(hw.DeviceKey)) continue;
                _adapterMap[hw.DeviceKey] = adapter;

                results.Add(hw);
            }
            Diagnostics = diagnostics;
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

        return new VulkanDevice(adapter, deviceInfo, _cacheDirectory, _workgroupSize);
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
            if (c.Luid.HasValue && c.Luid.Value != hw.AdapterLuid)
            {
                continue;
            }
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
