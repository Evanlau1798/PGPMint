using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed class OpenClBackend(string? cacheDirectory = null, int workgroupSize = 128) : IGpuBackend
{
    private readonly int _workgroupSize = GpuDeviceIdentity.ValidateWorkgroup(workgroupSize);
    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint", "shader-cache", "opencl");
    private readonly object _sync = new();
    private readonly Dictionary<string, OpenClAdapterDescriptor> _adapters = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Diagnostics { get; private set; } = [];

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        lock (_sync)
        {
            var diagnostics = new List<string>();
            var infos = new List<GpuDeviceInfo>();
            _adapters.Clear();
            foreach (var adapter in OpenClEnumerator.TryEnumerateGpuAdapters(diagnostics.Add))
            {
                GpuDeviceInfo info = adapter.ToDeviceInfo(_workgroupSize);
                if (!_adapters.TryAdd(info.DeviceKey, adapter)) continue;
                infos.Add(info);
            }
            Diagnostics = diagnostics;
            return infos;
        }
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        lock (_sync)
        {
            if (!_adapters.ContainsKey(deviceInfo.DeviceKey)) EnumerateHardwareGpus();
            if (!_adapters.TryGetValue(deviceInfo.DeviceKey, out var adapter))
                throw new GpuDeviceLostException($"OpenCL GPU '{deviceInfo.Name}' is no longer available.");
            return new OpenClDevice(adapter.DeviceId, adapter.ToDeviceInfo(_workgroupSize), adapter.SupportsFillBuffer,
                _cacheDirectory, adapter.MaxParameterSize, _workgroupSize);
        }
    }
}
