namespace PGPMint.Core.Search;

public sealed record GpuKernelConfiguration(int KeysPerWorkItem, int WorkgroupSize, int OuterLoopUnroll, string ScheduleVariant);

public sealed record GpuDeviceInfo(
    string DeviceKey,
    long AdapterLuid,
    string Name,
    uint VendorId,
    uint VendorDeviceId,
    uint SubSysId,
    uint Revision,
    ulong DedicatedVideoMemoryBytes,
    string DriverVersion,
    bool IsDiscrete,
    string BackendName = "DirectCompute",
    string KernelVersion = Gpu.DirectComputeShaderSource.KernelVersion)
{
    public string? BackendDriverVersion { get; init; }
    public GpuKernelConfiguration? KernelConfiguration { get; init; }
    public string? DeviceUuid { get; init; }
    public string? PciAddress { get; init; }
    public ulong? MetalRegistryId { get; init; }
}

public sealed record CalibrationRecord(
    string DeviceKey,
    string DeviceName,
    string DriverVersion,
    string KernelVersion,
    double PeakHashesPerSecond,
    double MedianHashesPerSecond,
    bool IsFullCalibration,
    DateTimeOffset CalibratedAtUtc,
    string? WorkloadIdentity = null,
    EfficiencyProfile? EfficiencyProfile = null);

public sealed record MatchedKeyRecord(
    string Id,
    string Fingerprint,
    string KeyId,
    uint CreationTimestamp,
    string MatchedPattern,
    string DeviceName,
    DateTimeOffset FoundAtUtc,
    string PublicKeyHex,
    string ProtectedPrivateSeedBase64);

public enum SearchResultMode
{
    EnumerateAll,
    FindOne
}

public readonly record struct SearchBatchResult(
    IReadOnlyList<uint>[] Hits,
    long UniqueCandidatesCompleted,
    long? HashEvaluations)
{
    public bool HashRangeFullyProcessed { get; init; } = true;
    public bool ResultsComplete { get; init; } = true;
    public bool WasCancelled { get; init; }
    public double? DispatchWallMilliseconds { get; init; }
    internal long? InitialHashEvaluations { get; init; }
    public long? RescanEvaluations => HashEvaluations - (InitialHashEvaluations ?? UniqueCandidatesCompleted);
    public long ReturnedHits => Hits.Sum(lane => (long)lane.Count);

    internal static IReadOnlyList<uint>[] EmptyHits(int laneCount) =>
        Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), laneCount).ToArray();

    internal static SearchBatchResult CancelledBeforeDispatch(int laneCount) =>
        new(EmptyHits(laneCount), 0, 0)
        {
            HashRangeFullyProcessed = false,
            ResultsComplete = false,
            WasCancelled = true
        };
}

public readonly record struct SearchWorkMetrics(
    long UniqueCandidatesCompleted,
    long? HashEvaluations,
    long ReturnedHits,
    long VerifiedHits,
    long SavedKeys)
{
    internal long? ReportedRescanEvaluations { get; init; }
    public long? RescanEvaluations => ReportedRescanEvaluations ?? (HashEvaluations - UniqueCandidatesCompleted);
}

public interface IGpuSearchDevice : IDisposable
{
    GpuDeviceInfo Info { get; }
    int KeysPerBatch { get; }
    long MaxCandidatesPerBatch { get; }
    string KernelVersion { get; }

    IReadOnlyList<uint>[] ScanGroup(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024);

    // Legacy implementations can return hits without tracking evaluations for recursive rescans.
    SearchBatchResult ScanBatch(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024,
        SearchResultMode resultMode = SearchResultMode.EnumerateAll,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
        }

        if (resultMode != SearchResultMode.EnumerateAll)
        {
            throw new NotSupportedException("The GPU backend does not support bounded search.");
        }

        return new SearchBatchResult(ScanGroup(publicKeys, startTimestamp, timestampCount, pattern, capacity),
            (long)timestampCount * Gpu.GpuSearchBatch.CountDistinctKeys(publicKeys), HashEvaluations: null);
    }
}

public interface IGpuBackend
{
    IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus();
    IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo);
}

public interface IWorkloadGpuBackend
{
    void SetWorkload(KeyIdPattern pattern);
}
