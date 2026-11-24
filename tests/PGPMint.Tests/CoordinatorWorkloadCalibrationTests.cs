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
            Assert.Equal(0.4, coordinator.SliderPosition, 6);

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

            using var reopened = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory));
            reopened.Initialize();
            reopened.SetTargetPattern(shortPattern);
            Assert.Equal(shortBaseline, reopened.LatestTelemetry.TotalBaselineHps);
            Assert.Equal(0.4, reopened.SliderPosition, 6);

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
