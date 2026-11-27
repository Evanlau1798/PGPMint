using System.Text.Json;
using System.Text.Json.Serialization;
using PGPMint.Core.Search;

namespace PGPMint.Cli;

internal sealed record CliStatusDeviceEvent(
    string DeviceKey,
    string Name,
    string Backend,
    bool Enabled,
    double ActualHps,
    double PowerWatts,
    double TemperatureCelsius,
    bool IsThermalThrottled,
    bool IsThermalGuardActive);

internal sealed record CliStatusEvent(
    string Event,
    string TimestampUtc,
    string State,
    string ActiveTier,
    double ActualHps,
    double PowerWatts,
    double? Mhpw,
    double? MaxTemperatureCelsius,
    bool IsThermalThrottled,
    bool IsThermalGuardActive,
    ulong SessionChecked,
    ulong TotalChecked,
    int TotalMatchesCount,
    double CumulativeProbability,
    double? EtaSeconds,
    IReadOnlyList<CliStatusDeviceEvent> Devices);

internal sealed record CliMatchEvent(
    string Event,
    string TimestampUtc,
    string KeyId,
    string Fingerprint,
    string Mask,
    string Device,
    string FoundAtUtc);

internal sealed record CliSummaryEvent(
    string Event,
    string TimestampUtc,
    string State,
    ulong SessionChecked,
    ulong TotalChecked,
    int SavedKeys,
    string? Error);

internal sealed record CliCurveRow(
    int TargetPercent,
    double ActualGhps,
    double PowerMedianWatts,
    double Mhpw,
    double GpuUtilMedianPercent,
    double? TempMaxCelsius,
    bool IsThermalThrottled,
    long StepCandidates);

internal sealed record CliCurveReport(
    string TimestampUtc,
    string Pattern,
    int SettleSeconds,
    int MeasureSeconds,
    IReadOnlyList<string> Devices,
    IReadOnlyList<CliCurveRow> Points,
    EfficiencyProfile Profile);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliStatusEvent))]
[JsonSerializable(typeof(CliMatchEvent))]
[JsonSerializable(typeof(CliSummaryEvent))]
[JsonSerializable(typeof(CliCurveReport))]
internal sealed partial class CliJsonContext : JsonSerializerContext;
