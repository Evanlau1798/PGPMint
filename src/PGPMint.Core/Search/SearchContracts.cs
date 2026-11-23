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
}

public interface IGpuBackend
{
    IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus();
    IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo);
}
