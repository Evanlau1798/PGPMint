using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public class CoordinatorCalibrationTests
{
    [Fact]
    public void SliderMathMatchesExactHandoffIntegerSpecification()
    {
        RateDirective dir37 = RateControl.FromSlider(0.37, 22_351_000_000.0);
        Assert.Equal(RateMode.Capped, dir37.Mode);
        Assert.Equal(8_269_870_000L, dir37.TargetHashesPerSecond);

        RateDirective dirZero = RateControl.FromSlider(0.0, 22_351_000_000.0);
        Assert.Equal(RateMode.Paused, dirZero.Mode);
        Assert.Equal(0L, dirZero.TargetHashesPerSecond);

        RateDirective dirFull = RateControl.FromSlider(1.0, 22_351_000_000.0);
        Assert.Equal(RateMode.FullSpeed, dirFull.Mode);
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
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { DriverVersion = "32.0.15.0000" }, gpu, "k1", requireFull: true));
            Assert.False(CalibrationValidator.IsValidForDevice(validRec with { KernelVersion = "k2" }, gpu, "k1", requireFull: true));

            var stateStore = new AppStateStore(tempDir);
            stateStore.Update(s => s.Calibrations[gpu.DeviceKey] = validRec);

            // Coordinator expecting "k2" must invalidate cached "k1" record
            var backend = new FakeGpuBackend(new[] { gpu }, batchDelayMs: 5, maxCandidatesPerBatch: 4000, kernelVersion: "k2");
            using var coord = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions { ExpectedKernelVersionOverride = "k2" });
            coord.Initialize();

            Assert.True(coord.NeedsQuickProbe());
            Assert.True(coord.NeedsFullCalibration());
            Assert.Equal(0.0, coord.LatestTelemetry.TotalPeakHps);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task TwoStageProbeThenZeroWasteFullCalibrationAndMultiGpuHitAndPatternSwitchWorks()
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
                    QuickProbeDuration = TimeSpan.FromMilliseconds(220),
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(320),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    TelemetryInterval = TimeSpan.FromMilliseconds(40),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            Assert.Equal(2, coordinator.Devices.Count);

            // Stage 1: Quick probe measures both GPUs and returns cleanly to Idle
            await coordinator.RunQuickProbeAsync();
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);

            var stateAfterProbe = stateStore.Load();
            Assert.True(stateAfterProbe.Calibrations[gpu0.DeviceKey].PeakHashesPerSecond > 0);
            Assert.True(stateAfterProbe.Calibrations[gpu1.DeviceKey].PeakHashesPerSecond > 0);
            Assert.False(stateAfterProbe.Calibrations[gpu0.DeviceKey].IsFullCalibration);

            // Stage 2: Start search with 1-hex suffix ("0") and AutoPauseOnHit=false so full calibration completes AND captures real hits
            coordinator.SetAutoPauseOnHit(false);
            coordinator.SetSliderPosition(0.5);
            var pattern = KeyIdPattern.Parse("0");

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));

            var stateAfterCal = stateStore.Load();
            Assert.True(stateAfterCal.Calibrations[gpu0.DeviceKey].IsFullCalibration);
            Assert.True(stateAfterCal.Calibrations[gpu1.DeviceKey].IsFullCalibration);
            Assert.True(vault.LoadMatches().Count > 0);

            // Pause via slider = 0 finishes the active in-flight batch and publishes final telemetry before pausing
            coordinator.SetSliderPosition(0.0);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Paused, TimeSpan.FromSeconds(3));
            await Task.Delay(120); // allow in-flight 5ms mock batch + 40ms telemetry tick to settle
            ulong checkedAtPause = coordinator.LatestTelemetry.TotalChecked;
            await Task.Delay(160);
            Assert.Equal(checkedAtPause, coordinator.LatestTelemetry.TotalChecked);

            coordinator.SetSliderPosition(0.6);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running &&
                                       coordinator.LatestTelemetry.TotalChecked > checkedAtPause, TimeSpan.FromSeconds(3));

            await coordinator.StopAsync();
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            ulong persistedTotal1 = stateStore.Load().TotalCheckedCandidates;
            Assert.True(persistedTotal1 > checkedAtPause);

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
    public async Task ManualRecalibrationCancelFromRunningPreservesPriorPeakAndRestoresRunning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-recal-run-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0 }, batchDelayMs: 15, maxCandidatesPerBatch: 4000);
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            stateStore.Update(s => s.Calibrations[gpu0.DeviceKey] = new CalibrationRecord(
                gpu0.DeviceKey, gpu0.Name, gpu0.DriverVersion, "fake-kernel-v1",
                12_345_678_900.0, 11_000_000_000.0, true, DateTimeOffset.UtcNow));

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromSeconds(5),
                    CalibrationWindow = TimeSpan.FromMilliseconds(100),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            coordinator.SetSliderPosition(0.45);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(3));

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(180));
            await coordinator.RunManualRecalibrationAsync(pattern, cts.Token);

            // External cancellation restores Running at prior slider (0.45) and preserves prior peak
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(3));
            Assert.Equal(0.45, coordinator.SliderPosition, 6);
            Assert.Equal(12_345_678_900.0, stateStore.Load().Calibrations[gpu0.DeviceKey].PeakHashesPerSecond);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StopDuringQuickProbeCancelsAndAwaitsWorkerCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-qstop-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu0 = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new FakeGpuBackend(new[] { gpu0 }, batchDelayMs: 25, maxCandidatesPerBatch: 4000);
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    QuickProbeDuration = TimeSpan.FromSeconds(10),
                    CalibrationWindow = TimeSpan.FromMilliseconds(100),
                    ExpectedKernelVersionOverride = "fake-kernel-v1"
                });

            coordinator.Initialize();
            Task probeTask = coordinator.RunQuickProbeAsync();
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.QuickProbing, TimeSpan.FromSeconds(2));

            await coordinator.StopAsync();
            await probeTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.False(stateStore.Load().Calibrations.ContainsKey(gpu0.DeviceKey));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AutoPauseOnHitDuringPhase1CalibrationCompletesCalibrationOnResumeAndIgnoresSliderMoveWhilePaused()
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
            coordinator.SetSliderPosition(0.8);

            MatchedKeyRecord? notifiedHit = null;
            coordinator.MatchFound += (_, hit) => notifiedHit ??= hit;

            // Starts in Phase 1 calibration; 1-hex suffix hits immediately and triggers AutoPauseOnHit
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Paused, TimeSpan.FromSeconds(5));

            Assert.NotNull(notifiedHit);
            Assert.NotEmpty(vault.LoadMatches());
            // Partial interrupted calibration must NOT be saved as complete
            Assert.True(coordinator.NeedsFullCalibration());

            // Moving slider while explicitly paused must NOT accidentally resume
            coordinator.SetSliderPosition(0.6);
            await Task.Delay(100);
            Assert.Equal(CoordinatorState.Paused, coordinator.CurrentState);

            // Disabling AutoPauseOnHit and explicitly clicking Resume must finish full calibration before entering Running
            coordinator.SetAutoPauseOnHit(false);
            coordinator.Resume();
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
                    QuickProbeDuration = TimeSpan.FromMilliseconds(180),
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(240),
                    CalibrationWindow = TimeSpan.FromMilliseconds(80)
                });

            coordinator.Initialize();
            await coordinator.RunQuickProbeAsync();
            Assert.False(coordinator.NeedsQuickProbe());

            coordinator.SetTargetPattern(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            Assert.NotNull(coordinator.LatestTelemetry.EstimatedMeanWait);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(4));
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(coordinator.LatestTelemetry.AllEnabledFullyCalibrated);
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
            // Enumerated initially as OpenCL, but CreateDevice falls back to DirectCompute
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

            // Device descriptor must have reconciled to DirectCompute and calibration must be valid
            Assert.Equal("DirectCompute", coordinator.LatestTelemetry.Devices[0].BackendName);
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(coordinator.LatestTelemetry.TotalPeakHps > 0);

            // Pause -> Resume must return cleanly to Running (not stuck in Calibrating)
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
