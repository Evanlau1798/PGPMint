namespace PGPMint.Core.Search;

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
    string KernelVersion = "d3d11-sha1-four-v1");

public sealed record CalibrationRecord(
    string DeviceKey,
    string DeviceName,
    string DriverVersion,
    string KernelVersion,
    double PeakHashesPerSecond,
    double MedianHashesPerSecond,
    bool IsFullCalibration,
    DateTimeOffset CalibratedAtUtc);

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

public readonly record struct SearchBatchResult(
    IReadOnlyList<uint>[] Hits,
    long UniqueCandidatesCompleted,
    long? HashEvaluations)
{
    public long? RescanEvaluations => HashEvaluations - UniqueCandidatesCompleted;
    public long ReturnedHits => Hits.Sum(lane => (long)lane.Count);
}

public readonly record struct SearchWorkMetrics(
    long UniqueCandidatesCompleted,
    long? HashEvaluations,
    long ReturnedHits,
    long VerifiedHits,
    long SavedKeys)
{
    public long? RescanEvaluations => HashEvaluations - UniqueCandidatesCompleted;
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
        int capacity = 1024) =>
        new(ScanGroup(publicKeys, startTimestamp, timestampCount, pattern, capacity),
            (long)timestampCount * KeysPerBatch, HashEvaluations: null);
}

public interface IGpuBackend
{
    IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus();
    IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo);
}
