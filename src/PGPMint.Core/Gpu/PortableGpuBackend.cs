using PGPMint.Core.Search;
using System.Text.Json;

namespace PGPMint.Core.Gpu;

public sealed class PortableGpuBackend : IGpuBackend, IWorkloadGpuBackend
{
    private readonly IGpuBackend[] _backends;
    private readonly Func<IGpuSearchDevice, KeyIdPattern, double> _measure;
    private readonly Dictionary<string, (IGpuBackend Backend, GpuDeviceInfo Info)> _winners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private KeyIdPattern _workload = KeyIdPattern.Parse("1145141919810");
    private readonly object _sync = new();
    private readonly Dictionary<string, List<(IGpuBackend Backend, GpuDeviceInfo Info)>> _devices = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = [];
    private bool _enumerated;

    public PortableGpuBackend(string? cacheDirectory = null, int workgroupSize = 128)
        : this(new VulkanBackend(cacheDirectory, workgroupSize), new OpenClBackend(cacheDirectory, workgroupSize)) { }

    internal PortableGpuBackend(IGpuBackend preferred, params IGpuBackend[] fallbacks)
        : this(new[] { preferred }.Concat(fallbacks)) { }

    internal PortableGpuBackend(IEnumerable<IGpuBackend> backends, Func<IGpuSearchDevice, KeyIdPattern, double>? measure = null)
    {
        _backends = backends.ToArray();
        _measure = measure ?? PortableGpuProbe.Measure;
    }

    public void SetWorkload(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        lock (_sync) _workload = pattern;
    }

    public IReadOnlyList<string> Diagnostics { get { lock (_sync) return _diagnostics.ToArray(); } }

    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus()
    {
        lock (_sync)
        {
            _devices.Clear();
            _diagnostics.Clear();
            var selected = new List<GpuDeviceInfo>();
            foreach (IGpuBackend backend in _backends)
            {
                IReadOnlyList<GpuDeviceInfo> available;
                try { available = backend.EnumerateHardwareGpus(); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _diagnostics.Add($"{backend.GetType().Name} enumeration failed: {ex.Message}");
                    continue;
                }
                foreach (GpuDeviceInfo candidate in available)
                {
                    int match = selected.FindIndex(info => _devices[info.DeviceKey].Any(c => GpuDeviceIdentity.Same(c.Info, candidate)));
                    if (match >= 0)
                    {
                        GpuDeviceInfo info = selected[match];
                        for (int i = selected.Count - 1; i > match; i--)
                        {
                            string otherKey = selected[i].DeviceKey;
                            if (!_devices[otherKey].Any(c => GpuDeviceIdentity.Same(c.Info, candidate))) continue;
                            _devices[info.DeviceKey].AddRange(_devices[otherKey]);
                            _devices.Remove(otherKey);
                            selected.RemoveAt(i);
                            _diagnostics.Add("Conflicting driver UUID groups share a reliable hardware identity; collapsed to one GPU worker.");
                        }
                        _devices[info.DeviceKey].Add((backend, candidate));
                        selected[match] = info with
                        {
                            DeviceUuid = info.DeviceUuid ?? candidate.DeviceUuid,
                            PciAddress = info.PciAddress ?? candidate.PciAddress,
                            MetalRegistryId = info.MetalRegistryId ?? candidate.MetalRegistryId
                        };
                        continue;
                    }

                    if (selected.Any(info => !GpuDeviceIdentity.Different(info, candidate)) ||
                        _devices.ContainsKey(candidate.DeviceKey))
                    {
                        _diagnostics.Add($"Suppressed {candidate.BackendName} '{candidate.Name}': hardware identity cannot prove a distinct GPU; no name-based matching is used.");
                        continue;
                    }

                    if (candidate.DeviceUuid is null && candidate.PciAddress is null &&
                        candidate.MetalRegistryId is null && candidate.AdapterLuid == 0)
                        _diagnostics.Add($"{candidate.BackendName} '{candidate.Name}' has no reliable hardware identity; only one unverified candidate can be scheduled.");
                    selected.Add(candidate);
                    _devices.Add(candidate.DeviceKey, [(backend, candidate)]);
                }
                if (backend is VulkanBackend vulkan) _diagnostics.AddRange(vulkan.Diagnostics);
                if (backend is OpenClBackend openCl) _diagnostics.AddRange(openCl.Diagnostics);
            }
            _enumerated = true;
            return selected;
        }
    }

    public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        lock (_sync)
        {
            if (!_enumerated) EnumerateHardwareGpus();
            if (!_devices.TryGetValue(deviceInfo.DeviceKey, out var candidates))
                throw new GpuDeviceLostException($"GPU '{deviceInfo.Name}' is no longer available.");

            string selectionKey = JsonSerializer.Serialize(new GpuSelectionIdentity(deviceInfo.DeviceKey,
                candidates.Select(c => new GpuSelectionCandidate(CandidateKey(c.Info),
                    CalibrationValidator.WorkloadIdentity(c.Info, _workload))).ToArray()),
                GpuSelectionJsonContext.Default.GpuSelectionIdentity);
            Exception? lastError = null;
            if (_winners.TryGetValue(selectionKey, out var cached) && !_rejected.Contains(CandidateKey(cached.Info)))
            {
                try { return Open(cached); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    lastError = ex;
                    Reject(cached.Info, ex);
                }
            }

            var measured = new List<((IGpuBackend Backend, GpuDeviceInfo Info) Candidate, double Score)>();
            foreach (var candidate in candidates)
            {
                if (_rejected.Contains(CandidateKey(candidate.Info))) continue;
                try
                {
                    using var device = candidate.Backend.CreateDevice(candidate.Info);
                    double score = _measure(device, _workload);
                    if (!(score > 0) || !double.IsFinite(score)) throw new InvalidOperationException("GPU probe returned an invalid rate.");
                    measured.Add((candidate, score));
                    _diagnostics.Add($"{candidate.Info.BackendName} '{candidate.Info.Name}' passed the oracle and measured {score:F0} candidates/s for suffix group {_workload.FixedHexLength}.");
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    lastError = ex;
                    Reject(candidate.Info, ex);
                }
            }

            foreach (var result in measured.OrderByDescending(entry => entry.Score))
            {
                try
                {
                    var device = Open(result.Candidate);
                    _winners[selectionKey] = result.Candidate;
                    return device;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    lastError = ex;
                    Reject(result.Candidate.Info, ex);
                }
            }
            if (lastError is GpuDeviceLostException lost) throw lost;
            throw new InvalidOperationException($"No usable GPU backend remains for '{deviceInfo.Name}'.", lastError);

            IGpuSearchDevice Open((IGpuBackend Backend, GpuDeviceInfo Info) candidate) =>
                new CanonicalGpuDevice(candidate.Backend.CreateDevice(candidate.Info), deviceInfo);
        }
    }

    private static string CandidateKey(GpuDeviceInfo info) => JsonSerializer.Serialize(new GpuCandidateIdentity(
        info.DeviceKey, info.BackendName, info.DriverVersion, info.BackendDriverVersion, info.KernelVersion, info.KernelConfiguration),
        GpuSelectionJsonContext.Default.GpuCandidateIdentity);

    private void Reject(GpuDeviceInfo info, Exception error)
    {
        _rejected.Add(CandidateKey(info));
        _diagnostics.Add($"{info.BackendName} '{info.Name}' excluded: {error.Message}");
    }
}

internal sealed class CanonicalGpuDevice(IGpuSearchDevice inner, GpuDeviceInfo canonical) : IGpuSearchDevice
{
    public GpuDeviceInfo Info { get; } = inner.Info with
    {
        DeviceKey = canonical.DeviceKey,
        DeviceUuid = canonical.DeviceUuid ?? inner.Info.DeviceUuid,
        PciAddress = canonical.PciAddress ?? inner.Info.PciAddress,
        MetalRegistryId = canonical.MetalRegistryId ?? inner.Info.MetalRegistryId
    };
    public int KeysPerBatch => inner.KeysPerBatch;
    public long MaxCandidatesPerBatch => inner.MaxCandidatesPerBatch;
    public string KernelVersion => inner.KernelVersion;
    public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> keys, uint start, uint count, KeyIdPattern pattern,
        int capacity = 1024) => inner.ScanGroup(keys, start, count, pattern, capacity);
    public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> keys, uint start, uint count, KeyIdPattern pattern,
        int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
        CancellationToken cancellationToken = default) => inner.ScanBatch(keys, start, count, pattern, capacity, resultMode, cancellationToken);
    public void Dispose() => inner.Dispose();
}
