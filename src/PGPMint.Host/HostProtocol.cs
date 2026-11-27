using System.Text.Json;
using System.Text.Json.Serialization;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Host;

public sealed record HostRequest(string? Id, string Method, JsonElement? Params = null);

public sealed record HostResponse(
    string Type,
    string? Id,
    bool Ok,
    object? Result = null,
    string? ErrorCode = null,
    string? Error = null);

public sealed record HostEvent(string Type, string Event, object Payload);

public sealed record HostSettingsDto(
    bool HasCompletedSetup,
    string TargetInput,
    string Language,
    bool AutoPauseOnHit,
    bool MinimizeToTrayOnClose,
    string PowerTier)
{
    public int? MaxGpuTemperatureCelsius { get; init; }
    public uint? MinCreationTimestamp { get; init; }
    public uint? MaxCreationTimestamp { get; init; }
}

public sealed record HostDeviceDto(
    string DeviceKey,
    string Name,
    string BackendName,
    string DriverVersion,
    bool IsDiscrete,
    bool IsEnabled,
    double ActualHps,
    long TargetHps,
    double BaselineHps,
    bool IsFullCalibrated,
    string? Error)
{
    public double PowerWatts { get; init; }
    public bool HasPowerSensor { get; init; }
    public double TemperatureCelsius { get; init; }
    public bool HasTemperatureSensor { get; init; }
    public bool IsThermalThrottled { get; init; }
    public bool IsThermalGuardActive { get; init; }
}

public sealed record HostTelemetryDto(
    string State,
    double CalibrationRemainingSeconds,
    double FullCalibrationDurationSeconds,
    double TotalActualHps,
    long TotalTargetHps,
    bool IsFullSpeed,
    double TotalBaselineHps,
    bool AllEnabledFullyCalibrated,
    ulong SessionChecked,
    ulong TotalChecked,
    int TotalMatchesCount,
    double CumulativeProbability,
    double? EstimatedMeanWaitSeconds,
    string? ErrorMessage,
    bool VaultIsFull,
    string Failure,
    string ActiveTier,
    double TotalPowerWatts,
    bool HasPowerSensor,
    double AggregateGpuUtilPercent,
    bool HasGpuUtilSensor,
    bool IsOverloadYielding,
    bool IsGpuContended,
    long VerifiedHits,
    long SavedKeys,
    IReadOnlyList<HostDeviceDto> Devices)
{
    public bool HasTemperatureSensor { get; init; }
    public double MaxTemperatureCelsius { get; init; }
    public bool IsThermalThrottled { get; init; }
    public bool IsThermalGuardActive { get; init; }
    public int? MaxGpuTemperatureLimitCelsius { get; init; }
}

public sealed record HostMatchDto(
    string Id,
    string Fingerprint,
    string KeyId,
    string FormattedKeyId,
    uint CreationTimestamp,
    string MatchedPattern,
    string DeviceName,
    DateTimeOffset FoundAtUtc,
    string PublicKeyHex);

public sealed record HostStatusSnapshot(
    int ProtocolVersion,
    string AppVersion,
    string DataDirectory,
    bool IsUnlocked,
    bool SupportsAutomaticUnlock,
    bool HasAutomaticMetadata,
    bool HasLegacyVault,
    string VaultMode,
    HostSettingsDto Settings,
    IReadOnlyList<HostDeviceDto> Devices,
    IReadOnlyList<string> GpuDiagnostics,
    HostTelemetryDto? Telemetry,
    IReadOnlyList<HostMatchDto> Matches,
    bool NeedsFullCalibration,
    double FullCalibrationDurationSeconds);

public sealed record UnlockAutomaticResult(bool Unlocked, HostStatusSnapshot Status);

public sealed record DeleteMatchResult(bool Deleted, HostStatusSnapshot Status);

public sealed record ClearMatchesResult(int Removed, HostStatusSnapshot Status);

public sealed record ExportMatchResult(string PrivatePath, string PublicPath);

public sealed record UnlockPasswordParams(string? Password);

public sealed record ApplySettingsParams(
    string? TargetInput = null,
    string? Language = null,
    bool? AutoPauseOnHit = null,
    bool? MinimizeToTrayOnClose = null,
    bool? HasCompletedSetup = null,
    int? MaxGpuTemperatureCelsius = null,
    uint? MinCreationTimestamp = null,
    uint? MaxCreationTimestamp = null,
    bool? ClearTimestampRange = null);

public sealed record SetPowerTierParams(string? Tier);

public sealed record SetDeviceEnabledParams(string? DeviceKey, bool Enabled);

public sealed record TargetParams(string? TargetInput = null);

public sealed record RecordIdParams(string? RecordId);

public sealed record ExportMatchParams(
    string? RecordId,
    string? Uid,
    string? Email,
    string? Passphrase,
    string? PrivatePath,
    string? PublicPath,
    bool IncludeEncryptionSubkey = true,
    long? ExpiresAtUnix = null);

public sealed record InspectSyncBundleParams(string? BundlePath);

public sealed record ExportSyncBundleParams(
    string? OutputPath,
    bool IncludeKeys = true,
    string? Passphrase = null);

public sealed record ImportSyncBundleParams(
    string? BundlePath,
    string? Passphrase = null);

public sealed record SyncInspectResultDto(
    int Version,
    DateTimeOffset CreatedAtUtc,
    string PatternMask,
    string TargetInput,
    ulong TotalCheckedCandidates,
    bool HasEncryptedKeys,
    int KeyCount);

public sealed record SyncExportResultDto(
    string FilePath,
    string PatternMask,
    ulong TotalCheckedCandidates,
    int ExportedKeysCount,
    bool HasEncryptedKeys);

public sealed record SyncImportResultDto(
    string PatternMask,
    ulong AddedCandidates,
    ulong NewTotalCheckedCandidates,
    int ImportedKeysCount,
    int SkippedDuplicateKeysCount,
    bool PatternMismatchSkippedProgress,
    HostStatusSnapshot Status);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(HostEvent))]
[JsonSerializable(typeof(HostStatusSnapshot))]
[JsonSerializable(typeof(UnlockAutomaticResult))]
[JsonSerializable(typeof(DeleteMatchResult))]
[JsonSerializable(typeof(ClearMatchesResult))]
[JsonSerializable(typeof(ExportMatchResult))]
[JsonSerializable(typeof(HostTelemetryDto))]
[JsonSerializable(typeof(HostMatchDto))]
[JsonSerializable(typeof(UnlockPasswordParams))]
[JsonSerializable(typeof(ApplySettingsParams))]
[JsonSerializable(typeof(SetPowerTierParams))]
[JsonSerializable(typeof(SetDeviceEnabledParams))]
[JsonSerializable(typeof(TargetParams))]
[JsonSerializable(typeof(RecordIdParams))]
[JsonSerializable(typeof(ExportMatchParams))]
[JsonSerializable(typeof(InspectSyncBundleParams))]
[JsonSerializable(typeof(ExportSyncBundleParams))]
[JsonSerializable(typeof(ImportSyncBundleParams))]
[JsonSerializable(typeof(SyncInspectResultDto))]
[JsonSerializable(typeof(SyncExportResultDto))]
[JsonSerializable(typeof(SyncImportResultDto))]
public sealed partial class HostJsonContext : JsonSerializerContext;
