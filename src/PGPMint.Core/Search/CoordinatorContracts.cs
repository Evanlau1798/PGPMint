using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public enum CoordinatorState
{
    Idle,
    Calibrating,
    Running,
    Paused,
    Stopping,
    Faulted
}

public enum SearchFailure
{
    None,
    SaveFailed,
    IncompleteResults
}

public sealed record GpuTelemetrySnapshot(
    string DeviceKey,
    string Name,
    string BackendName,
    bool IsDiscrete,
    bool IsEnabled,
    double ActualHps,
    long TargetHps,
    double BaselineHps,
    bool IsFullCalibrated,
    string? Error)
{
    public double LastBatchWallMilliseconds { get; init; }
    public double PowerWatts { get; init; }
    public double PowerLimitWatts { get; init; }
    public bool HasPowerSensor { get; init; }
    public double GpuUtilPercent { get; init; }
    public bool HasGpuUtilSensor { get; init; }
    public bool IsOverloadYielding { get; init; }
    public bool IsGpuContended { get; init; }
    public bool IsHighIdlePowerFloor { get; init; }
    public bool HasTemperatureSensor { get; init; }
    public double TemperatureCelsius { get; init; }
    public bool IsThermalThrottled { get; init; }
    public bool IsThermalGuardActive { get; init; }
}

public sealed record CoordinatorTelemetry(
    CoordinatorState State,
    double CalibrationRemainingSeconds,
    double TotalActualHps,
    long TotalTargetHps,
    bool IsFullSpeed,
    double TotalBaselineHps,
    bool AllEnabledFullyCalibrated,
    ulong SessionChecked,
    ulong TotalChecked,
    int TotalMatchesCount,
    double CumulativeProbability,
    TimeSpan? EstimatedMeanWait,
    string? ErrorMessage,
    IReadOnlyList<GpuTelemetrySnapshot> Devices)
{
    public SearchWorkMetrics SessionWork { get; init; }
    public bool VaultIsFull { get; init; }
    public SearchFailure Failure { get; init; }
    public PowerTier ActiveTier { get; init; } = PowerTier.Max;
    public double TotalPowerWatts { get; init; }
    public bool HasPowerSensor { get; init; }
    public double AggregateGpuUtilPercent { get; init; }
    public bool HasGpuUtilSensor { get; init; }
    public bool IsOverloadYielding { get; init; }
    public bool IsGpuContended { get; init; }
    public bool HasHighIdlePowerFloor { get; init; }
    public bool HasTemperatureSensor { get; init; }
    public double MaxTemperatureCelsius { get; init; }
    public bool IsThermalThrottled { get; init; }
    public bool IsThermalGuardActive { get; init; }
    public int? MaxGpuTemperatureLimitCelsius { get; init; }
}

public sealed class CoordinatorOptions
{
    public TimeSpan FullCalibrationDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CalibrationWindow { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DynamicMedianInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MinDynamicMedianSamples { get; init; } = 3;
    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan SensorPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public string? ExpectedKernelVersionOverride { get; init; }
}
