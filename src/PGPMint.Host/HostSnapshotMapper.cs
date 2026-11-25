using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Host;

internal static class HostSnapshotMapper
{
    public const int ProtocolVersionNumber = 1;

    public static HostStatusSnapshot BuildStatus(
        string directoryPath,
        AppPersistedState state,
        bool supportsAutomaticUnlock,
        string vaultMode,
        MultiGpuCoordinator? coordinator,
        VaultStore? vault,
        IReadOnlyList<string> gpuDiagnostics,
        CoordinatorOptions? coordinatorOptions)
    {
        bool hasAutomatic = AutomaticVault.IsAutomatic(directoryPath);
        bool hasLegacy = File.Exists(Path.Combine(directoryPath, "vault.key.json")) ||
                         File.Exists(Path.Combine(directoryPath, "hits.vault.json"));

        HostTelemetryDto? telemetryDto = null;
        IReadOnlyList<HostDeviceDto> devices = [];
        IReadOnlyList<HostMatchDto> matches = [];
        bool needsCal = false;
        double fullCalSeconds = coordinatorOptions?.FullCalibrationDuration.TotalSeconds ?? 30.0;

        if (coordinator is not null)
        {
            CoordinatorTelemetry latest = coordinator.LatestTelemetry;
            telemetryDto = MapTelemetry(coordinator, latest);
            devices = telemetryDto.Devices;
            needsCal = coordinator.NeedsFullCalibration();
            fullCalSeconds = coordinator.FullCalibrationDurationSeconds;
            if (vault is not null)
            {
                try { matches = vault.LoadMatches().Select(MapMatch).ToArray(); }
                catch { matches = []; }
            }
        }

        string appVersion = typeof(HostSession).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        PowerTier effectiveTier = coordinator?.SelectedTier ?? state.PowerTier ?? PowerTier.Max;
        var settingsDto = new HostSettingsDto(
            HasCompletedSetup: state.HasCompletedSetup,
            TargetInput: state.TargetInput ?? string.Empty,
            Language: string.IsNullOrWhiteSpace(state.Language) ? "auto" : state.Language,
            AutoPauseOnHit: coordinator?.AutoPauseOnHit ?? state.AutoPauseOnHit,
            MinimizeToTrayOnClose: state.MinimizeToTrayOnClose,
            PowerTier: effectiveTier.ToString());

        return new HostStatusSnapshot(
            ProtocolVersion: ProtocolVersionNumber,
            AppVersion: appVersion,
            DataDirectory: directoryPath,
            IsUnlocked: coordinator is not null,
            SupportsAutomaticUnlock: supportsAutomaticUnlock,
            HasAutomaticMetadata: hasAutomatic,
            HasLegacyVault: hasLegacy,
            VaultMode: vaultMode,
            Settings: settingsDto,
            Devices: devices,
            GpuDiagnostics: gpuDiagnostics,
            Telemetry: telemetryDto,
            Matches: matches,
            NeedsFullCalibration: needsCal,
            FullCalibrationDurationSeconds: fullCalSeconds);
    }

    public static HostTelemetryDto MapTelemetry(MultiGpuCoordinator coordinator, CoordinatorTelemetry t)
    {
        var driverLookup = coordinator.Devices.ToDictionary(d => d.DeviceKey, d => d.DriverVersion, StringComparer.Ordinal);
        HostDeviceDto[] devices = t.Devices.Select(d => new HostDeviceDto(
            DeviceKey: d.DeviceKey,
            Name: d.Name,
            BackendName: d.BackendName,
            DriverVersion: driverLookup.GetValueOrDefault(d.DeviceKey, string.Empty),
            IsDiscrete: d.IsDiscrete,
            IsEnabled: d.IsEnabled,
            ActualHps: d.ActualHps,
            TargetHps: d.TargetHps,
            BaselineHps: d.BaselineHps,
            IsFullCalibrated: d.IsFullCalibrated,
            Error: d.Error)).ToArray();

        double? etaSeconds = t.EstimatedMeanWait is { } wait
            ? (wait == TimeSpan.MaxValue ? 365.25 * 86400.0 * 1_000_000.0 : wait.TotalSeconds)
            : null;

        return new HostTelemetryDto(
            State: t.State.ToString(),
            CalibrationRemainingSeconds: t.CalibrationRemainingSeconds,
            FullCalibrationDurationSeconds: coordinator.FullCalibrationDurationSeconds,
            TotalActualHps: t.TotalActualHps,
            TotalTargetHps: t.TotalTargetHps,
            IsFullSpeed: t.IsFullSpeed,
            TotalBaselineHps: t.TotalBaselineHps,
            AllEnabledFullyCalibrated: t.AllEnabledFullyCalibrated,
            SessionChecked: t.SessionChecked,
            TotalChecked: t.TotalChecked,
            TotalMatchesCount: t.TotalMatchesCount,
            CumulativeProbability: t.CumulativeProbability,
            EstimatedMeanWaitSeconds: etaSeconds,
            ErrorMessage: t.ErrorMessage,
            VaultIsFull: t.VaultIsFull,
            Failure: t.Failure.ToString(),
            ActiveTier: t.ActiveTier.ToString(),
            TotalPowerWatts: t.TotalPowerWatts,
            HasPowerSensor: t.HasPowerSensor,
            AggregateGpuUtilPercent: t.AggregateGpuUtilPercent,
            HasGpuUtilSensor: t.HasGpuUtilSensor,
            IsOverloadYielding: t.IsOverloadYielding,
            IsGpuContended: t.IsGpuContended,
            VerifiedHits: t.SessionWork.VerifiedHits,
            SavedKeys: t.SessionWork.SavedKeys,
            Devices: devices);
    }

    public static HostMatchDto MapMatch(MatchedKeyRecord record) => new(
        Id: record.Id,
        Fingerprint: record.Fingerprint,
        KeyId: record.KeyId,
        FormattedKeyId: FormattingHelpers.FormatKeyIdSpaced(record.KeyId),
        CreationTimestamp: record.CreationTimestamp,
        MatchedPattern: record.MatchedPattern,
        DeviceName: record.DeviceName,
        FoundAtUtc: record.FoundAtUtc,
        PublicKeyHex: record.PublicKeyHex);
}
