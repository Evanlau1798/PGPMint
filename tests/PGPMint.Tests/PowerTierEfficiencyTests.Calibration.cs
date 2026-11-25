using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

public sealed partial class PowerTierEfficiencyTests
{
    [Fact]
    public async Task OverloadYieldPausesBalancedTierAndAutoRecoversWhenOtherProcessStops()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-yield-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 42, "Mock RTX 3080", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 95.0, 350.0, true, 32.0, 27.0, 5.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);

            var efficiencyProfile = new EfficiencyProfile(
                FullLoadWatts: 346.5,
                FullLoadGpuUtil: 85.0,
                HasPowerSensor: true,
                Eco: new TierOperatingPoint(0.08, 52.5, 19.0, 80_000.0, "natural-plateau"),
                Balanced: new TierOperatingPoint(0.22, 96.9, 35.0, 220_000.0, "natural-peak"),
                Efficiency: new TierOperatingPoint(0.48, 240.0, 48.0, 480_000.0, "upper-mid-peak"),
                Samples: Array.Empty<EfficiencyCurveSample>());

            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Balanced;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    1_000_000.0, 1_000_000.0, true, DateTimeOffset.UtcNow, workload, efficiencyProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    TelemetryInterval = TimeSpan.FromMilliseconds(40),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            Assert.Equal(PowerTier.Balanced, coordinator.SelectedTier);

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0 &&
                !coordinator.LatestTelemetry.IsOverloadYielding,
                TimeSpan.FromSeconds(4));

            sensor.Current = new GpuSensorSample(true, 280.0, 350.0, true, 92.0, 15.0, 77.0);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.IsOverloadYielding,
                TimeSpan.FromSeconds(4));

            sensor.Current = new GpuSensorSample(true, 55.0, 350.0, true, 6.0, 0.0, 6.0);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                !coordinator.LatestTelemetry.IsOverloadYielding &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0,
                TimeSpan.FromSeconds(6));

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DynamicMedianUsesMedianWattsAndDoesNotFlipSensorlessProfile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-dynwatt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 320.0, 350.0, true, 95.0, 90.0, 5.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);
            var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            // Seed sensorless profile (HasPowerSensor=false, tier EstimatedWatts=0)
            var sensorlessProfile = EfficiencyProfile.CreateFallback(50_000.0, fullLoadWatts: 0.0, fullLoadGpuUtil: 90.0);
            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Max;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    50_000.0, 50_000.0, true, initialTimestamp, workload, sensorlessProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    DynamicMedianInterval = TimeSpan.FromMilliseconds(160),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => stateStore.Load().Calibrations[profileKey].CalibratedAtUtc > initialTimestamp, TimeSpan.FromSeconds(4));
            await coordinator.StopAsync();

            EfficiencyProfile? updatedProfile = stateStore.Load().Calibrations[profileKey].EfficiencyProfile;
            Assert.NotNull(updatedProfile);
            Assert.False(updatedProfile!.HasPowerSensor);
            Assert.Equal(0.0, updatedProfile.FullLoadWatts, 6);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DynamicMedianSkipsBaselineUpdateWhileOtherProcessContendsForGpu()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-contend-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new SimpleFakeBackend(gpu);
            // Start uncontested so samples begin accumulating, then launch a game mid-interval
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 348.5, 350.0, true, 96.0, 92.0, 4.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);
            var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            var cleanProfile = new EfficiencyProfile(
                FullLoadWatts: 348.5,
                FullLoadGpuUtil: 95.0,
                HasPowerSensor: true,
                Eco: new TierOperatingPoint(0.05, 46.0, 22.0, 1_000_000.0, "natural-plateau"),
                Balanced: new TierOperatingPoint(0.15, 60.0, 29.0, 3_000_000.0, "natural-peak"),
                Efficiency: new TierOperatingPoint(0.48, 248.0, 47.0, 10_000_000.0, "upper-mid-peak"),
                Samples: Array.Empty<EfficiencyCurveSample>());

            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Max;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    23_500_000_000.0, 23_000_000_000.0, true, initialTimestamp, workload, cleanProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    DynamicMedianInterval = TimeSpan.FromMilliseconds(260),
                    TelemetryInterval = TimeSpan.FromMilliseconds(35),
                    SensorPollInterval = TimeSpan.FromMilliseconds(35),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            await coordinator.StartSearchAsync(pattern);

            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0 &&
                !coordinator.LatestTelemetry.IsGpuContended,
                TimeSpan.FromSeconds(4));

            // Inject mid-interval game contention (32% other GPU, 335 W): accumulated samples must be discarded
            sensor.Current = new GpuSensorSample(true, 335.0, 350.0, true, 96.0, 44.0, 32.0);
            await WaitUntilAsync(() => coordinator.LatestTelemetry.IsGpuContended, TimeSpan.FromSeconds(4));

            // Stay contended well past DynamicMedianInterval (260ms): baseline and FullLoadWatts MUST stay untouched
            await Task.Delay(500);
            CalibrationRecord duringGame = stateStore.Load().Calibrations[profileKey];
            Assert.Equal(initialTimestamp, duringGame.CalibratedAtUtc);
            Assert.Equal(23_000_000_000.0, duringGame.MedianHashesPerSecond, 6);
            Assert.Equal(348.5, duringGame.EfficiencyProfile!.FullLoadWatts, 6);

            // Once competing game exits (OtherProcessGpuUtil = 4%, 349 W), contention clears and DynamicMedian resumes
            // using strictly clean post-game wattage samples (no leaked 335 W).
            sensor.Current = new GpuSensorSample(true, 349.0, 350.0, true, 98.0, 94.0, 4.0);
            await WaitUntilAsync(() =>
                !coordinator.LatestTelemetry.IsGpuContended &&
                stateStore.Load().Calibrations[profileKey].CalibratedAtUtc > initialTimestamp,
                TimeSpan.FromSeconds(4));

            CalibrationRecord afterGame = stateStore.Load().Calibrations[profileKey];
            Assert.Equal(349.0, afterGame.EfficiencyProfile!.FullLoadWatts, 6);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LegacyStateMigrationAndPartialEfficiencyProfileAreHandledSafely()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 1, "Legacy GPU", 0x10DE, 1, 0, 1, 8_000_000_000UL, "1.0", true);
            var pattern = KeyIdPattern.Parse("1145141919810");
            string currentWorkload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string currentKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, currentWorkload);
            string legacyV2Workload = currentWorkload.Replace("v3/", "v2/", StringComparison.Ordinal);
            string legacyV2Key = CalibrationValidator.ProfileKey(gpu.DeviceKey, legacyV2Workload);
            Directory.CreateDirectory(tempDir);

            // State with both a complete legacy v2 profile and a partial current v3 profile missing Balanced:
            // must not throw, must preserve TotalChecked & PowerTier, and must require fresh v3 calibration.
            string partialJson = $$"""
            {
              "HasCompletedSetup": true,
              "TargetInput": "1145141919810",
              "LastPatternMask": "{{pattern.CanonicalMask}}",
              "TotalCheckedCandidates": 1832181651148985,
              "SliderPosition": 0.25,
              "EnabledDeviceKeys": ["GPU-0"],
              "Calibrations": {
                "{{legacyV2Key}}": {
                  "DeviceKey": "GPU-0",
                  "DeviceName": "Legacy GPU",
                  "DriverVersion": "1.0",
                  "KernelVersion": "d3d11-sha1-four-v4",
                  "PeakHashesPerSecond": 22000000000,
                  "MedianHashesPerSecond": 21000000000,
                  "IsFullCalibration": true,
                  "CalibratedAtUtc": "2026-11-25T10:00:00Z",
                  "WorkloadIdentity": "{{legacyV2Workload}}",
                  "EfficiencyProfile": {
                    "FullLoadWatts": 340,
                    "FullLoadGpuUtil": 85,
                    "HasPowerSensor": true,
                    "Eco": { "RateFraction": 0.08, "EstimatedWatts": 52, "EstimatedGpuUtil": 19, "MeasuredHps": 1700000000, "SelectionMode": "natural-plateau" },
                    "Balanced": { "RateFraction": 0.22, "EstimatedWatts": 98, "EstimatedGpuUtil": 30, "MeasuredHps": 4700000000, "SelectionMode": "natural-peak" },
                    "Efficiency": { "RateFraction": 0.48, "EstimatedWatts": 245, "EstimatedGpuUtil": 48, "MeasuredHps": 10400000000, "SelectionMode": "upper-mid-peak" }
                  }
                },
                "{{currentKey}}": {
                  "DeviceKey": "GPU-0",
                  "DeviceName": "Legacy GPU",
                  "DriverVersion": "1.0",
                  "KernelVersion": "d3d11-sha1-four-v4",
                  "PeakHashesPerSecond": 22000000000,
                  "MedianHashesPerSecond": 21000000000,
                  "IsFullCalibration": true,
                  "CalibratedAtUtc": "2026-11-25T10:00:00Z",
                  "WorkloadIdentity": "{{currentWorkload}}",
                  "EfficiencyProfile": {
                    "FullLoadWatts": 340,
                    "FullLoadGpuUtil": 85,
                    "HasPowerSensor": true,
                    "Eco": { "RateFraction": 0.08, "EstimatedWatts": 52, "EstimatedGpuUtil": 19, "MeasuredHps": 1700000000, "SelectionMode": "natural-plateau" }
                  }
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "state.json"), partialJson);

            var stateStore = new AppStateStore(tempDir);
            var backend = new SimpleFakeBackend(gpu);
            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                telemetrySensor: NullGpuTelemetrySensor.Instance);

            coordinator.Initialize();
            coordinator.SetTargetPattern(pattern);

            Assert.Equal(PowerTier.Eco, coordinator.SelectedTier);
            Assert.Equal(1832181651148985UL, coordinator.LatestTelemetry.TotalChecked);
            Assert.True(coordinator.NeedsFullCalibration());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalizationStringBundlesHaveMatchingKeysAndAllFourTierStrings()
    {
        string enPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Strings", "en-US.json");
        string zhPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Strings", "zh-Hant.json");
        Assert.True(File.Exists(enPath), $"Missing {enPath}");
        Assert.True(File.Exists(zhPath), $"Missing {zhPath}");

        using JsonDocument enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using JsonDocument zhDoc = JsonDocument.Parse(File.ReadAllText(zhPath));

        HashSet<string> enKeys = enDoc.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> zhKeys = zhDoc.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(enKeys, zhKeys);
        foreach (string required in new[] { "TierEco", "TierBalanced", "TierEfficiency", "TierMax", "TierEcoTooltip", "TierBalancedTooltip", "TierEfficiencyTooltip", "TierMaxTooltip", "Recalibrate", "Recalibrating", "PowerTooltipFormat", "GpuLoadTooltipFormat", "YieldingBadge", "YieldingSuffix" })
        {
            Assert.Contains(required, enKeys);
            Assert.False(string.IsNullOrWhiteSpace(enDoc.RootElement.GetProperty("strings").GetProperty(required).GetString()));
            Assert.False(string.IsNullOrWhiteSpace(zhDoc.RootElement.GetProperty("strings").GetProperty(required).GetString()));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for expected coordinator condition.");
    }

    private sealed class ControllableFakeSensor(GpuSensorSample initial) : IGpuTelemetrySensor
    {
        private readonly object _gate = new();
        private GpuSensorSample _current = initial;

        public GpuSensorSample Current
        {
            get { lock (_gate) return _current; }
            set { lock (_gate) _current = value; }
        }

        public bool HasPowerSensor(GpuDeviceInfo device) => Current.HasPowerWatts;
        public GpuSensorSample Sample(GpuDeviceInfo device) => Current;
        public void Dispose() { }
    }

    private sealed class SimpleFakeBackend(GpuDeviceInfo info) : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => new SimpleFakeDevice(deviceInfo);
    }

    private sealed class SimpleFakeDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 16_384;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024) =>
            ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

        public SearchBatchResult ScanBatch(
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

            Thread.Sleep(3);
            return new SearchBatchResult([[], [], [], []], (long)timestampCount * KeysPerBatch, (long)timestampCount * KeysPerBatch);
        }

        public void Dispose() { }
    }
}
