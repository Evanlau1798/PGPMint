using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed class MetalBackend : IGpuBackend
{
    private readonly int _workgroupSize;

    public MetalBackend(string? cacheDirectory = null, int workgroupSize = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workgroupSize);
        _workgroupSize = workgroupSize;
        // Metal owns its compiler cache; cacheDirectory preserves backend factory compatibility.
    }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(11)) return [];
        string version = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        return MetalNative.Enumerate().Select(adapter => Describe(adapter.RegistryId, adapter.GetName(),
            adapter.WorkingSetBytes, adapter.UnifiedMemory != 0, adapter.LowPower != 0, version)).ToArray();
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        if (!OperatingSystem.IsMacOSVersionAtLeast(11))
            throw new PlatformNotSupportedException("Metal requires macOS 11 or later.");
        foreach (GpuDeviceInfo available in EnumerateHardwareGpus())
        {
            if (!string.Equals(available.DeviceKey, deviceInfo.DeviceKey, StringComparison.Ordinal)) continue;
            return new MetalDevice(available, MetalSession.Create(available.MetalRegistryId!.Value, _workgroupSize));
        }
        throw new InvalidOperationException($"Metal GPU '{deviceInfo.Name}' is not available.");
    }

    internal static GpuDeviceInfo Describe(ulong registryId, string name, ulong memory,
        bool unifiedMemory, bool lowPower, string driverVersion) =>
        new($"metal:{registryId:x16}", 0, name, 0, 0, 0, 0, unifiedMemory ? 0 : memory,
            driverVersion, !unifiedMemory && !lowPower, "Metal", MetalKernelSource.KernelVersion)
        {
            BackendDriverVersion = driverVersion,
            MetalRegistryId = registryId
        };
}
