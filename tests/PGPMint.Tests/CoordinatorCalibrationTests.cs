using System.Reflection;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public partial class CoordinatorCalibrationTests
{
    [Fact]
    public void PowerTierMappingAndRateMathMatchSpecification()
    {
        var profile = new EfficiencyProfile(
            FullLoadWatts: 346.0,
            FullLoadGpuUtil: 85.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.08, 52.5, 19.0, 1.76e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.22, 96.9, 35.0, 4.84e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.70, 275.0, 70.0, 15.40e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        RateDirective dirEco = RateControl.FromTier(PowerTier.Eco, 22_000_000_000.0, profile);
        Assert.Equal(RateMode.Capped, dirEco.Mode);
        Assert.Equal(1_760_000_000L, dirEco.TargetHashesPerSecond);

        RateDirective dirBal = RateControl.FromTier(PowerTier.Balanced, 22_000_000_000.0, profile);
        Assert.Equal(RateMode.Capped, dirBal.Mode);
        Assert.Equal(4_840_000_000L, dirBal.TargetHashesPerSecond);

        RateDirective dirEff = RateControl.FromTier(PowerTier.Efficiency, 22_000_000_000.0, profile);
        Assert.Equal(RateMode.Capped, dirEff.Mode);
        Assert.Equal(15_400_000_000L, dirEff.TargetHashesPerSecond);

        RateDirective dirYieldHalf = RateControl.FromTier(PowerTier.Balanced, 22_000_000_000.0, profile, yieldScale: 0.5);
        Assert.Equal(RateMode.Capped, dirYieldHalf.Mode);
        Assert.Equal(2_420_000_000L, dirYieldHalf.TargetHashesPerSecond);

        RateDirective dirYieldZero = RateControl.FromTier(PowerTier.Balanced, 22_000_000_000.0, profile, yieldScale: 0.0);
        Assert.Equal(RateMode.Paused, dirYieldZero.Mode);
        Assert.Equal(0L, dirYieldZero.TargetHashesPerSecond);

        RateDirective dirMax = RateControl.FromTier(PowerTier.Max, 22_000_000_000.0, profile, yieldScale: 0.0);
        Assert.Equal(RateMode.FullSpeed, dirMax.Mode);

        // Legacy slider -> PowerTier migration (Eco -> Balanced -> Efficiency -> Max)
        Assert.Equal(PowerTier.Eco, RateControl.TierFromLegacySlider(0.01));
        Assert.Equal(PowerTier.Eco, RateControl.TierFromLegacySlider(0.30));
        Assert.Equal(PowerTier.Balanced, RateControl.TierFromLegacySlider(0.31));
        Assert.Equal(PowerTier.Balanced, RateControl.TierFromLegacySlider(0.50));
        Assert.Equal(PowerTier.Efficiency, RateControl.TierFromLegacySlider(0.55));
        Assert.Equal(PowerTier.Efficiency, RateControl.TierFromLegacySlider(0.98));
        Assert.Equal(PowerTier.Max, RateControl.TierFromLegacySlider(0.99));
        Assert.Equal(PowerTier.Max, RateControl.TierFromLegacySlider(1.0));
        Assert.Equal(PowerTier.Max, RateControl.TierFromLegacySlider(double.NaN));
    }

    [Fact]
    public void CalibrationValidityChecksHardwareDriverAndKernelVersionThroughCoordinator()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-kver-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 1, "RTX 3080", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var validRec = new CalibrationRecord(gpu.DeviceKey, gpu.Name, gpu.DriverVersion, "k1", 20e9, 19e9, true, DateTimeOffset.UtcNow,
                EfficiencyProfile: EfficiencyProfile.CreateFallback(19e9));

            Assert.True(CalibrationValidator.IsValidForDevice(validRec, gpu, "k1", requireFull: true));
            Assert.Equal(19e9, CalibrationValidator.GetBaselineHps(validRec));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { EfficiencyProfile = null }, gpu, "k1", requireFull: true));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { DriverVersion = "32.0.15.0000" }, gpu, "k1", requireFull: true));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { KernelVersion = "k2" }, gpu, "k1", requireFull: true));

            var stateStore = new AppStateStore(tempDir);
            stateStore.Update(s => s.Calibrations[gpu.DeviceKey] = validRec);

            var backend = new FakeGpuBackend(new[] { gpu }, batchDelayMs: 5, maxCandidatesPerBatch: 4000, kernelVersion: "k2");
            using var coord = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions { ExpectedKernelVersionOverride = "k2" },
                NullGpuTelemetrySensor.Instance);
            coord.Initialize();

            Assert.True(coord.NeedsFullCalibration());
            Assert.Equal(0.0, coord.LatestTelemetry.TotalBaselineHps);
            Assert.Null(coord.LatestTelemetry.EstimatedMeanWait);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AutoPauseOnHitSavesExactlyOneMatchWhenBothDevicesCanHit()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-onehit-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock RTX A", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var gpu1 = new GpuDeviceInfo("GPU-1", 102, "Mock RX B", 0x1002, 2, 0, 1, 8_000_000_000UL, "2.0", true);
            // Large scan per batch on 1-hex suffix ("0") guarantees many hits across all 4 lanes on both GPUs
            var backend = new FakeGpuBackend(new[] { gpu0, gpu1 }, batchDelayMs: 2, maxCandidatesPerBatch: 4096);
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(500),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    TelemetryInterval = TimeSpan.FromMilliseconds(30),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(true);

            int notifiedCount = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notifiedCount);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await WaitUntilAsync(
                () => coordinator.LatestTelemetry is { State: CoordinatorState.Idle, TotalMatchesCount: 1 },
                TimeSpan.FromSeconds(5));

            Assert.Equal(1, notifiedCount);
            Assert.Single(vault.LoadMatches());
            Assert.True(stateStore.Load().TotalCheckedCandidates > 0);
            SearchWorkMetrics metrics = coordinator.LatestTelemetry.SessionWork;
            Assert.Equal(1L, metrics.SavedKeys);
            Assert.InRange(metrics.VerifiedHits, 1L, metrics.ReturnedHits);
            Assert.Equal(stateStore.Load().TotalCheckedCandidates, (ulong)metrics.UniqueCandidatesCompleted);
            Assert.Null(metrics.HashEvaluations);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RepeatedVerifiedCandidateDoesNotIncreaseSavedKeys()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-repeat-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 0, "1.0", true);
            var vault = new DpapiVaultStore(tempDir);
            var backend = new FakeGpuBackend(new[] { gpu }, batchDelayMs: 0, maxCandidatesPerBatch: 4);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(tempDir), vault);
            Ed25519KeyPair key = Ed25519KeyGenerator.Generate();
            const uint timestamp = 1700000000u;
            var pattern = KeyIdPattern.Parse(OpenPgpV4Packet.ComputeKeyId(key.PublicKey, timestamp));
            IReadOnlyList<uint>[] hits = [new[] { timestamp }];
            MethodInfo? submit = typeof(MultiGpuCoordinator).GetMethod("HandleVerifiedHits", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(submit);

            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);
            object[] candidate = [gpu, new[] { key }, hits, pattern, false, CancellationToken.None];
            submit.Invoke(coordinator, candidate);
            submit.Invoke(coordinator, candidate);
            coordinator.SetTargetPattern(pattern);

            Assert.Single(vault.LoadMatches());
            Assert.Equal(1, notified);
            Assert.Equal(2L, coordinator.LatestTelemetry.SessionWork.VerifiedHits);
            Assert.Equal(1L, coordinator.LatestTelemetry.SessionWork.SavedKeys);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PowerTierIsLockedDuringInitialCalibrationAndZeroWasteMultiGpuSearchWorks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-coord-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock RTX A", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var gpu1 = new GpuDeviceInfo("GPU-1", 102, "Mock RX B", 0x1002, 2, 0, 1, 8_000_000_000UL, "2.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0, gpu1 }, batchDelayMs: 5, maxCandidatesPerBatch: 4000, maxHitBatches: 2);

            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(320),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    DynamicMedianInterval = TimeSpan.FromSeconds(30),
                    TelemetryInterval = TimeSpan.FromMilliseconds(40),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            Assert.Equal(2, coordinator.Devices.Count);
            Assert.Equal(PowerTier.Max, coordinator.SelectedTier);
            Assert.Equal(0.0, coordinator.LatestTelemetry.TotalBaselineHps);

            // Start with 1-hex suffix and AutoPauseOnHit=false so 30s (320ms) measurement completes and captures hits
            coordinator.SetAutoPauseOnHit(false);
            var pattern = KeyIdPattern.Parse("0");
            var tierDuringCalibration = new TaskCompletionSource<PowerTier>(TaskCreationOptions.RunContinuationsAsynchronously);
            int observedCalibration = 0;
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.State == CoordinatorState.Calibrating && Interlocked.Exchange(ref observedCalibration, 1) == 0)
                {
                    coordinator.SetPowerTier(PowerTier.Eco);
                    tierDuringCalibration.TrySetResult(coordinator.SelectedTier);
                }
            };

            await coordinator.StartSearchAsync(pattern);
            Assert.Equal(PowerTier.Max, await tierDuringCalibration.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));

            var stateAfterCal = stateStore.Load();
            CalibrationRecord gpu0Cal = stateAfterCal.Calibrations.Values.Single(record => record.DeviceKey == gpu0.DeviceKey);
            CalibrationRecord gpu1Cal = stateAfterCal.Calibrations.Values.Single(record => record.DeviceKey == gpu1.DeviceKey);
            Assert.True(gpu0Cal.IsFullCalibration);
            Assert.True(gpu1Cal.IsFullCalibration);
            Assert.NotNull(gpu0Cal.EfficiencyProfile);
            Assert.NotNull(gpu1Cal.EfficiencyProfile);
            Assert.True(gpu0Cal.MedianHashesPerSecond > 0);
            Assert.True(coordinator.LatestTelemetry.TotalBaselineHps > 0);
            Assert.True(vault.LoadMatches().Count > 0);

            // Switch to Efficiency tier while running
            coordinator.SetPowerTier(PowerTier.Efficiency);
            Assert.Equal(PowerTier.Efficiency, coordinator.SelectedTier);
            Assert.False(coordinator.LatestTelemetry.VaultIsFull);
            ulong checkedBeforeCap = coordinator.LatestTelemetry.TotalChecked;
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running &&
                                       coordinator.LatestTelemetry.TotalChecked > checkedBeforeCap, TimeSpan.FromSeconds(3));

            await coordinator.StopAsync();
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Null(coordinator.LatestTelemetry.EstimatedMeanWait); // Idle shows no speculative ETA
            ulong persistedTotal1 = stateStore.Load().TotalCheckedCandidates;
            Assert.True(persistedTotal1 > checkedBeforeCap);

            // Second start/stop cycle with SAME pattern accumulates monotonically
            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => coordinator.LatestTelemetry.TotalChecked > persistedTotal1, TimeSpan.FromSeconds(3));
            await coordinator.StopAsync();
            ulong persistedTotal2 = stateStore.Load().TotalCheckedCandidates;
            Assert.True(persistedTotal2 > persistedTotal1);

            // Switching to a DIFFERENT pattern resets checked count so probability stays accurate
            var newPattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            await coordinator.StartSearchAsync(newPattern);
            await WaitUntilAsync(() => coordinator.LatestTelemetry.TotalChecked < persistedTotal2, TimeSpan.FromSeconds(3));
            await coordinator.StopAsync();
            Assert.Equal(newPattern.CanonicalMask, stateStore.Load().LastPatternMask);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FullSpeedDynamicMedianUpdatesOnlyAtFullSpeedAndStopsWhenCapped()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-dynmed-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0 }, batchDelayMs: 4, maxCandidatesPerBatch: 4000);
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            // Seed with a known valid full calibration from earlier
            var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu0, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu0.DeviceKey, workload);
            stateStore.Update(state =>
            {
                state.TargetInput = pattern.Suffix;
                state.Calibrations[profileKey] = new CalibrationRecord(gpu0.DeviceKey, gpu0.Name, gpu0.DriverVersion,
                    "fake-kernel-v1", 50_000.0, 50_000.0, true, initialTimestamp, workload,
                    EfficiencyProfile.CreateFallback(50_000.0));
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    DynamicMedianInterval = TimeSpan.FromMilliseconds(160),
                    TelemetryInterval = TimeSpan.FromMilliseconds(40),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                },
                NullGpuTelemetrySensor.Instance);

            coordinator.Initialize();
            Assert.Equal(PowerTier.Max, coordinator.SelectedTier);

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() =>
                stateStore.Load().Calibrations[profileKey].CalibratedAtUtc > initialTimestamp,
                TimeSpan.FromSeconds(4));

            // Dynamic median updated while running in FullSpeed mode and preserved efficiency profile
            CalibrationRecord updatedRec = stateStore.Load().Calibrations[profileKey];
            DateTimeOffset updatedAtFullSpeed = updatedRec.CalibratedAtUtc;
            Assert.True(updatedAtFullSpeed > initialTimestamp);
            Assert.NotNull(updatedRec.EfficiencyProfile);

            // Switch to Efficiency tier (Capped mode): dynamic median sampling must immediately stop
            coordinator.SetPowerTier(PowerTier.Efficiency);
            await Task.Delay(60); // settle in-flight tick
            DateTimeOffset stampedAfterCap = stateStore.Load().Calibrations[profileKey].CalibratedAtUtc;
            await Task.Delay(320); // well past DynamicMedianInterval (160ms)
            Assert.Equal(stampedAfterCap, stateStore.Load().Calibrations[profileKey].CalibratedAtUtc);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AutoStopOnHitDuringPhase1CalibrationStopsCleanlyAndCompletesCalibrationOnNextStart()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-hitcal-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0 }, batchDelayMs: 5, maxCandidatesPerBatch: 1024);
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(240),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(true);

            MatchedKeyRecord? notifiedHit = null;
            coordinator.MatchFound += (_, hit) => notifiedHit ??= hit;

            // Starts in Phase 1 calibration; 1-hex suffix hits immediately and triggers auto-stop into Idle
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Idle, TimeSpan.FromSeconds(5));

            Assert.NotNull(notifiedHit);
            Assert.Single(vault.LoadMatches());
            Assert.True(stateStore.Load().TotalCheckedCandidates > 0);
            // Partial interrupted calibration must NOT be saved as complete
            Assert.True(coordinator.NeedsFullCalibration());

            // Power tier cannot be adjusted before 30s full calibration completes
            coordinator.SetPowerTier(PowerTier.Eco);
            Assert.Equal(PowerTier.Max, coordinator.SelectedTier);
            await Task.Delay(60);
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);

            // Disabling AutoPauseOnHit and starting search again must finish full calibration before entering Running
            coordinator.SetAutoPauseOnHit(false);
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(stateStore.Load().Calibrations.Values.Single(record => record.DeviceKey == gpu0.DeviceKey).IsFullCalibration);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task MixedOpenClAndDirectComputeMultiGpuValidatesEachDeviceOwnKernelVersion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-mixed-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpuCl = new GpuDeviceInfo("GPU-CL", 1, "Mock RTX", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true, "OpenCL", "opencl-sha1-four-v1");
            var gpuDx = new GpuDeviceInfo("GPU-DX", 2, "Mock iGPU", 0x8086, 2, 0, 1, 128_000_000UL, "2.0", false, "DirectCompute", "d3d11-sha1-four-v1");
            var backend = new PerDeviceFakeBackend(new[] { gpuCl, gpuDx });
            var stateStore = new AppStateStore(tempDir);
            stateStore.Update(s => s.EnabledDeviceKeys = new List<string> { gpuCl.DeviceKey, gpuDx.DeviceKey });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(240),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80)
                });

            coordinator.Initialize();
            coordinator.SetTargetPattern(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            Assert.Null(coordinator.LatestTelemetry.EstimatedMeanWait); // No speculative ETA before running

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(4));
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(coordinator.LatestTelemetry.AllEnabledFullyCalibrated);
            Assert.True(coordinator.LatestTelemetry.TotalBaselineHps > 0);
            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task OpenClFallbackToDirectComputeReconcilesDeviceDescriptorAndResumeWorks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-fallback-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var enumeratedInfo = new GpuDeviceInfo("GPU-FB", 1, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true, "OpenCL", "opencl-sha1-four-v1");
            var backend = new FallbackFakeBackend(enumeratedInfo);
            var stateStore = new AppStateStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(240),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80)
                });

            coordinator.Initialize();
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(4));

            Assert.Equal("DirectCompute", coordinator.LatestTelemetry.Devices[0].BackendName);
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(coordinator.LatestTelemetry.TotalBaselineHps > 0);

            coordinator.Pause();
            Assert.Equal(CoordinatorState.Paused, coordinator.CurrentState);
            coordinator.Resume();
            Assert.Equal(CoordinatorState.Running, coordinator.CurrentState);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
