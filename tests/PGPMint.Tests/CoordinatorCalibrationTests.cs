using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public class CoordinatorCalibrationTests
{
    [Fact]
    public void TwoStageSliderMappingAndRateMathMatchSpecification()
    {
        // Logical -> RateDirective
        RateDirective dir37 = RateControl.FromSlider(0.37, 22_000_000_000.0);
        Assert.Equal(RateMode.Capped, dir37.Mode);
        Assert.Equal(8_140_000_000L, dir37.TargetHashesPerSecond);

        RateDirective dir99 = RateControl.FromSlider(0.99, 22_000_000_000.0);
        Assert.Equal(RateMode.Capped, dir99.Mode);
        Assert.Equal(21_780_000_000L, dir99.TargetHashesPerSecond);

        RateDirective dirZero = RateControl.FromSlider(0.0, 22_000_000_000.0);
        Assert.Equal(RateMode.Paused, dirZero.Mode);
        Assert.Equal(0L, dirZero.TargetHashesPerSecond);

        RateDirective dirFull = RateControl.FromSlider(1.0, 22_000_000_000.0);
        Assert.Equal(RateMode.FullSpeed, dirFull.Mode);

        // Two-stage UI slider (10..990 = 1%..99%, 991..1074 = detent buffer holding 99%, 1075..1080 = FullSpeed)
        Assert.Equal(0.01, RateControl.UiSliderToLogical(0.0), 6);
        Assert.Equal(0.01, RateControl.UiSliderToLogical(10.0), 6);
        Assert.Equal(0.50, RateControl.UiSliderToLogical(500.0), 6);
        Assert.Equal(0.99, RateControl.UiSliderToLogical(990.0), 6);
        Assert.Equal(0.99, RateControl.UiSliderToLogical(1040.0), 6); // inside detent buffer
        Assert.Equal(990.0, RateControl.SnapUiSliderOnRelease(1040.0)); // snaps back to 99%
        Assert.Equal(1.0, RateControl.UiSliderToLogical(1080.0), 6);
        Assert.Equal(1080.0, RateControl.LogicalToUiSlider(1.0));
        Assert.Equal(990.0, RateControl.LogicalToUiSlider(0.99));
        Assert.Equal(10.0, RateControl.LogicalToUiSlider(0.01));
        Assert.Equal(10.0, RateControl.LogicalToUiSlider(0.0));
    }

    [Fact]
    public void CalibrationValidityChecksHardwareDriverAndKernelVersionThroughCoordinator()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-kver-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 1, "RTX 3080", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var validRec = new CalibrationRecord(gpu.DeviceKey, gpu.Name, gpu.DriverVersion, "k1", 20e9, 19e9, true, DateTimeOffset.UtcNow);

            Assert.True(CalibrationValidator.IsValidForDevice(validRec, gpu, "k1", requireFull: true));
            Assert.Equal(19e9, CalibrationValidator.GetBaselineHps(validRec));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { DriverVersion = "32.0.15.0000" }, gpu, "k1", requireFull: true));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { KernelVersion = "k2" }, gpu, "k1", requireFull: true));

            var stateStore = new AppStateStore(tempDir);
            stateStore.Update(s => s.Calibrations[gpu.DeviceKey] = validRec);

            var backend = new FakeGpuBackend(new[] { gpu }, batchDelayMs: 5, maxCandidatesPerBatch: 4000, kernelVersion: "k2");
            using var coord = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions { ExpectedKernelVersionOverride = "k2" });
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
    public async Task AutoPauseOnHitSavesExactlyOneMatchEvenWhenBatchReturnsManyHits()
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
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Idle, TimeSpan.FromSeconds(5));
            await Task.Delay(80);

            Assert.Equal(1, notifiedCount);
            Assert.Single(vault.LoadMatches());
            Assert.True(stateStore.Load().TotalCheckedCandidates > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SliderIsLockedDuringInitialCalibrationAndZeroWasteMultiGpuSearchWorks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-coord-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock RTX A", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var gpu1 = new GpuDeviceInfo("GPU-1", 102, "Mock RX B", 0x1002, 2, 0, 1, 8_000_000_000UL, "2.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0, gpu1 }, batchDelayMs: 5, maxCandidatesPerBatch: 4000);

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
            Assert.Equal(1.0, coordinator.SliderPosition, 6);
            Assert.Equal(0.0, coordinator.LatestTelemetry.TotalBaselineHps);

            // Start with 1-hex suffix and AutoPauseOnHit=false so 30s (320ms) measurement completes and captures hits
            coordinator.SetAutoPauseOnHit(false);
            var pattern = KeyIdPattern.Parse("0");

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Calibrating, TimeSpan.FromSeconds(2));

            // Attempting to adjust slider during initial full-speed measurement must be ignored
            coordinator.SetSliderPosition(0.4);
            Assert.Equal(1.0, coordinator.SliderPosition, 6);

            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));

            var stateAfterCal = stateStore.Load();
            Assert.True(stateAfterCal.Calibrations[gpu0.DeviceKey].IsFullCalibration);
            Assert.True(stateAfterCal.Calibrations[gpu1.DeviceKey].IsFullCalibration);
            Assert.True(stateAfterCal.Calibrations[gpu0.DeviceKey].MedianHashesPerSecond > 0);
            Assert.True(coordinator.LatestTelemetry.TotalBaselineHps > 0);
            Assert.True(vault.LoadMatches().Count > 0);

            // Adjust slider to 60% capped mode while running
            coordinator.SetSliderPosition(0.6);
            Assert.Equal(0.6, coordinator.SliderPosition, 6);
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
            stateStore.Update(s => s.Calibrations[gpu0.DeviceKey] = new CalibrationRecord(
                gpu0.DeviceKey, gpu0.Name, gpu0.DriverVersion, "fake-kernel-v1",
                50_000.0, 50_000.0, true, initialTimestamp));

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
                });

            coordinator.Initialize();
            Assert.Equal(1.0, coordinator.SliderPosition, 6);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            await WaitUntilAsync(() =>
                stateStore.Load().Calibrations[gpu0.DeviceKey].CalibratedAtUtc > initialTimestamp,
                TimeSpan.FromSeconds(4));

            // Dynamic median updated while running in FullSpeed mode
            DateTimeOffset updatedAtFullSpeed = stateStore.Load().Calibrations[gpu0.DeviceKey].CalibratedAtUtc;
            Assert.True(updatedAtFullSpeed > initialTimestamp);

            // Move slider to 95% (Capped mode): dynamic median sampling must immediately stop
            coordinator.SetSliderPosition(0.95);
            await Task.Delay(60); // settle in-flight tick
            DateTimeOffset stampedAfterCap = stateStore.Load().Calibrations[gpu0.DeviceKey].CalibratedAtUtc;
            await Task.Delay(320); // well past DynamicMedianInterval (160ms)
            Assert.Equal(stampedAfterCap, stateStore.Load().Calibrations[gpu0.DeviceKey].CalibratedAtUtc);

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

            // Moving slider while stopped updates preference without starting search
            coordinator.SetSliderPosition(0.6);
            await Task.Delay(60);
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);

            // Disabling AutoPauseOnHit and starting search again must finish full calibration before entering Running
            coordinator.SetAutoPauseOnHit(false);
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(stateStore.Load().Calibrations[gpu0.DeviceKey].IsFullCalibration);

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

    private sealed class PerDeviceFakeBackend : IGpuBackend
    {
        private readonly IReadOnlyList<GpuDeviceInfo> _devices;
        public PerDeviceFakeBackend(IReadOnlyList<GpuDeviceInfo> devices) => _devices = devices;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _devices;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new FakeGpuDevice(info, 5, 4000, info.KernelVersion);
    }

    private sealed class FallbackFakeBackend : IGpuBackend
    {
        private readonly GpuDeviceInfo _enumerated;
        public FallbackFakeBackend(GpuDeviceInfo enumerated) => _enumerated = enumerated;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => new[] { _enumerated };
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info)
        {
            var actual = info with { BackendName = "DirectCompute", KernelVersion = "d3d11-sha1-four-v1" };
            return new FakeGpuDevice(actual, 5, 4000, actual.KernelVersion);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for coordinator state transition.");
    }

    private sealed class FakeGpuBackend : IGpuBackend
    {
        private readonly IReadOnlyList<GpuDeviceInfo> _devices;
        private readonly int _batchDelayMs;
        private readonly long _maxCandidatesPerBatch;
        private readonly string _kernelVersion;

        public FakeGpuBackend(IReadOnlyList<GpuDeviceInfo> devices, int batchDelayMs, long maxCandidatesPerBatch, string kernelVersion = "fake-kernel-v1")
        {
            _devices = devices;
            _batchDelayMs = batchDelayMs;
            _maxCandidatesPerBatch = maxCandidatesPerBatch;
            _kernelVersion = kernelVersion;
        }

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _devices;

        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) =>
            new FakeGpuDevice(deviceInfo, _batchDelayMs, _maxCandidatesPerBatch, _kernelVersion);
    }

    private sealed class FakeGpuDevice : IGpuSearchDevice
    {
        private readonly int _batchDelayMs;
        public GpuDeviceInfo Info { get; }
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch { get; }
        public string KernelVersion { get; }

        public FakeGpuDevice(GpuDeviceInfo info, int batchDelayMs, long maxCandidatesPerBatch, string kernelVersion)
        {
            Info = info;
            _batchDelayMs = batchDelayMs;
            MaxCandidatesPerBatch = maxCandidatesPerBatch;
            KernelVersion = kernelVersion;
        }

        public IReadOnlyList<uint>[] ScanGroup(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024)
        {
            if (_batchDelayMs > 0)
            {
                Thread.Sleep(_batchDelayMs);
            }

            var results = new IReadOnlyList<uint>[4];
            if (pattern.FixedHexLength >= 10)
            {
                for (int i = 0; i < 4; i++) results[i] = Array.Empty<uint>();
                return results;
            }

            uint scanLimit = Math.Min(timestampCount, 64u);
            for (int lane = 0; lane < 4; lane++)
            {
                var hits = new List<uint>();
                for (uint offset = 0; offset < scanLimit; offset++)
                {
                    uint ts = startTimestamp + offset;
                    if (pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[lane], ts)))
                    {
                        hits.Add(ts);
                    }
                }
                results[lane] = hits;
            }
            return results;
        }

        public void Dispose() { }
    }
}
