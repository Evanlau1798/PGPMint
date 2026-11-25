using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorWorkloadCalibrationTests
{
    [Fact]
    public async Task ProfilesAreScopedToWorkloadAndLegacyProfilesExpire()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-workload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new CalibrationBackend();
            var stateStore = new AppStateStore(directory);
            stateStore.Update(state =>
            {
                state.TargetInput = "0";
                state.SliderPosition = 0.4;
                GpuDeviceInfo info = backend.Info;
                state.Calibrations[info.DeviceKey] = new CalibrationRecord(info.DeviceKey, info.Name, info.DriverVersion,
                    info.KernelVersion, 1e9, 1e9, true, DateTimeOffset.UtcNow);
            });
            using var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory),
                new CoordinatorOptions { FullCalibrationDuration = TimeSpan.FromMilliseconds(75), CalibrationWindow = TimeSpan.FromMilliseconds(25) });
            coordinator.Initialize();

            var shortPattern = KeyIdPattern.Parse("0");
            var sameDensity = KeyIdPattern.Parse("F");
            var longPattern = KeyIdPattern.Parse("0000000000000000");
            coordinator.SetTargetPattern(shortPattern);
            Assert.True(coordinator.NeedsFullCalibration());
            Assert.Equal(0, coordinator.LatestTelemetry.TotalBaselineHps);
            Assert.Equal(PowerTier.Balanced, coordinator.SelectedTier);

            await CalibrateAsync(coordinator, shortPattern);
            coordinator.SetTargetPattern(sameDensity);
            Assert.False(coordinator.NeedsFullCalibration());
            double shortBaseline = coordinator.LatestTelemetry.TotalBaselineHps;
            Assert.True(shortBaseline > 0);

            coordinator.SetTargetPattern(longPattern);
            Assert.True(coordinator.NeedsFullCalibration());
            Assert.Equal(0, coordinator.LatestTelemetry.TotalBaselineHps);
            await CalibrateAsync(coordinator, longPattern);

            coordinator.SetTargetPattern(shortPattern);
            Assert.False(coordinator.NeedsFullCalibration());
            Assert.Equal(shortBaseline, coordinator.LatestTelemetry.TotalBaselineHps);

            using var reopened = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory), telemetrySensor: NullGpuTelemetrySensor.Instance);
            reopened.Initialize();
            reopened.SetTargetPattern(shortPattern);
            Assert.Equal(shortBaseline, reopened.LatestTelemetry.TotalBaselineHps);
            Assert.Equal(PowerTier.Balanced, reopened.SelectedTier);

            var newShape = new CalibrationBackend(backend.Info with
            {
                KernelConfiguration = backend.Info.KernelConfiguration! with { WorkgroupSize = 64 }
            });
            using var changedShape = new MultiGpuCoordinator(newShape, stateStore, new DpapiVaultStore(directory));
            changedShape.Initialize();
            changedShape.SetTargetPattern(shortPattern);
            Assert.True(changedShape.NeedsFullCalibration());

            var newDriver = new CalibrationBackend(backend.Info with { BackendDriverVersion = "new-ICD" });
            using var changed = new MultiGpuCoordinator(newDriver, stateStore, new DpapiVaultStore(directory));
            changed.Initialize();
            changed.SetTargetPattern(shortPattern);
            Assert.True(changed.NeedsFullCalibration());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LengthsWithinSameExecutionGroupReuseCalibrationWhileDistinctGroupsIsolate()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-workload-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new CalibrationBackend();
            var stateStore = new AppStateStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory),
                new CoordinatorOptions { FullCalibrationDuration = TimeSpan.FromMilliseconds(75), CalibrationWindow = TimeSpan.FromMilliseconds(25) });
            coordinator.Initialize();

            // Group 1..4: isolated per length
            var len1 = KeyIdPattern.Parse("A");
            var len2 = KeyIdPattern.Parse("AB");
            await CalibrateAsync(coordinator, len1);
            Assert.False(coordinator.NeedsFullCalibration());
            coordinator.SetTargetPattern(len2);
            Assert.True(coordinator.NeedsFullCalibration());

            // Group 5..8: share low32 execution profile
            var len5 = KeyIdPattern.Parse("12345");
            var len8 = KeyIdPattern.Parse("DEADBEEF");
            await CalibrateAsync(coordinator, len5);
            Assert.False(coordinator.NeedsFullCalibration());
            coordinator.SetTargetPattern(len8);
            Assert.False(coordinator.NeedsFullCalibration());

            // Transition 8 -> 9 crosses into high64 group
            var len9 = KeyIdPattern.Parse("123456789");
            coordinator.SetTargetPattern(len9);
            Assert.True(coordinator.NeedsFullCalibration());

            // Group 9..16: share high64 execution profile across 9, 12, 14, 16 hex
            await CalibrateAsync(coordinator, len9);
            Assert.False(coordinator.NeedsFullCalibration());
            foreach (string suffix in new[] { "676767676767", "0123456789ABCD", "0123456789ABCDEF" })
            {
                coordinator.SetTargetPattern(KeyIdPattern.Parse(suffix));
                Assert.False(coordinator.NeedsFullCalibration());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconciledBackendDriverVersionEnablesTierSwitchingAndPersistsCalibration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-reconcile-driver-" + Guid.NewGuid().ToString("N"));
        try
        {
            var enumeratedInfo = new GpuDeviceInfo("metal:000000010000052b", 0, "Apple M3", 0, 0, 0, 0, 0,
                "Darwin 24.6.0", false, "Metal", "metal-sha1-v1")
            {
                BackendDriverVersion = "Darwin 24.6.0"
            };
            var runtimeInfo = enumeratedInfo with
            {
                BackendName = "Vulkan",
                DriverVersion = "1.4.2",
                BackendDriverVersion = "1.4.2",
                KernelVersion = "vulkan-sha1-four-v4",
                KernelConfiguration = new GpuKernelConfiguration(4, 128, 1, "ubo-spirv-v4")
            };

            var backend = new SwitchingBackend(enumeratedInfo, runtimeInfo);
            var stateStore = new AppStateStore(directory);
            var options = new CoordinatorOptions
            {
                FullCalibrationDuration = TimeSpan.FromMilliseconds(75),
                CalibrationWindow = TimeSpan.FromMilliseconds(25)
            };
            var pattern = KeyIdPattern.Parse("000000000000");

            using (var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory), options))
            {
                coordinator.Initialize();
                var runningTcs = new TaskCompletionSource<CoordinatorTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
                coordinator.TelemetryUpdated += (_, t) =>
                {
                    if (t.State == CoordinatorState.Running)
                    {
                        runningTcs.TrySetResult(t);
                    }
                };

                await coordinator.StartSearchAsync(pattern);
                CoordinatorTelemetry runningSnap = await runningTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

                // Calibration completed on reconciled runtime backend/driver must immediately unlock baseline & tier switching while running.
                Assert.True(runningSnap.AllEnabledFullyCalibrated);
                Assert.True(runningSnap.TotalBaselineHps > 0);
                Assert.False(coordinator.NeedsFullCalibration());

                coordinator.SetPowerTier(PowerTier.Balanced);
                Assert.Equal(PowerTier.Balanced, coordinator.SelectedTier);
                Assert.Equal(PowerTier.Balanced, coordinator.LatestTelemetry.ActiveTier);

                await coordinator.StopAsync();
            }

            // Reopening with the same state store must recognize the reconciled calibration immediately on StartSearchAsync without re-calibrating.
            using (var reopened = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory),
                new CoordinatorOptions { FullCalibrationDuration = TimeSpan.FromSeconds(30), CalibrationWindow = TimeSpan.FromMilliseconds(25) }))
            {
                reopened.Initialize();
                reopened.SetTargetPattern(pattern);
                var runningTcs = new TaskCompletionSource<CoordinatorTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
                reopened.TelemetryUpdated += (_, t) =>
                {
                    if (t.State == CoordinatorState.Running)
                    {
                        runningTcs.TrySetResult(t);
                    }
                };

                await reopened.StartSearchAsync(pattern);
                CoordinatorTelemetry runningSnap = await runningTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(runningSnap.AllEnabledFullyCalibrated);
                Assert.True(runningSnap.TotalBaselineHps > 0);
                Assert.Equal(PowerTier.Balanced, runningSnap.ActiveTier);
                await reopened.StopAsync();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task CalibrateAsync(MultiGpuCoordinator coordinator, KeyIdPattern pattern)
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TelemetryUpdated += OnTelemetry;
        try
        {
            await coordinator.StartSearchAsync(pattern);
            await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync();
        }
        finally
        {
            coordinator.TelemetryUpdated -= OnTelemetry;
        }

        void OnTelemetry(object? sender, CoordinatorTelemetry telemetry)
        {
            if (telemetry.State == CoordinatorState.Running)
            {
                running.TrySetResult();
            }
        }
    }

    private sealed class SwitchingBackend(GpuDeviceInfo enumeratedInfo, GpuDeviceInfo runtimeInfo) : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [enumeratedInfo];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new CalibrationDevice(runtimeInfo);
    }

    private sealed class CalibrationBackend(GpuDeviceInfo? info = null) : IGpuBackend
    {
        public GpuDeviceInfo Info { get; } = info ?? new GpuDeviceInfo("GPU", 1, "Test GPU", 0x10DE, 1, 0, 1, 0, "1", true,
            "OpenCL", "cl-test") { BackendDriverVersion = "1", KernelConfiguration = new GpuKernelConfiguration(1, 128, 2, "fixed-xor") };
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [Info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new CalibrationDevice(info);
    }

    private sealed class CalibrationDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 8192;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
            }

            Thread.Sleep(4);
            return new SearchBatchResult([[]], timestampCount, timestampCount);
        }

        public void Dispose() { }
    }
}
