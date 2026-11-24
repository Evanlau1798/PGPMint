using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorDeviceRecoveryTests
{
    [Fact]
    public async Task RepeatedDeviceLossStopsWithoutAnUnboundedReopenLoop()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-device-loss-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new LosingBackend(failReplacement: true);
            var stateStore = new AppStateStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory));
            coordinator.Initialize();
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.State == CoordinatorState.Faulted)
                {
                    faulted.TrySetResult();
                }
            };

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FEDCBA9876543210"));
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, backend.OpenCount);
            Assert.Equal(2, backend.DisposeCount);
            Assert.Equal(1L << 18, coordinator.LatestTelemetry.SessionWork.UniqueCandidatesCompleted);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceLossReopensAtSafeBatchWithoutCountingFailedWork(bool calibrated)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-device-loss-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new LosingBackend();
            var stateStore = new AppStateStore(directory);
            var pattern = KeyIdPattern.Parse("FEDCBA9876543210");
            string workload = CalibrationValidator.WorkloadIdentity(backend.Info, pattern);
            string profile = CalibrationValidator.ProfileKey(backend.Info.DeviceKey, workload);
            if (calibrated)
            {
                stateStore.Update(state => state.Calibrations[profile] =
                    new CalibrationRecord(backend.Info.DeviceKey, backend.Info.Name, backend.Info.DriverVersion,
                        backend.Info.KernelVersion, 1e9, 1e9, true, DateTimeOffset.UtcNow, workload));
            }

            using var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory));
            coordinator.Initialize();
            coordinator.SetTargetPattern(pattern);
            coordinator.SetSliderPosition(1);
            Assert.Equal(!calibrated, coordinator.NeedsFullCalibration());
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.State == CoordinatorState.Faulted)
                {
                    faulted.TrySetResult();
                }
            };

            await coordinator.StartSearchAsync(pattern);
            Task first = await Task.WhenAny(backend.ReopenedBatch.Task, faulted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(backend.ReopenedBatch.Task, first);
            Assert.Equal(1u << 19, backend.FailedBatchLength);
            Assert.Equal(1u << 18, await backend.ReopenedBatch.Task);
            Assert.Equal(2, backend.OpenCount);
            Assert.Equal(1, backend.DisposeCount);
            Assert.True(coordinator.NeedsFullCalibration());

            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(2, backend.DisposeCount);
            Assert.Equal(1L << 18, coordinator.LatestTelemetry.SessionWork.UniqueCandidatesCompleted);
            Assert.Equal(1L << 18, coordinator.LatestTelemetry.SessionWork.HashEvaluations);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class LosingBackend(bool failReplacement = false) : IGpuBackend
    {
        public bool FailReplacement { get; } = failReplacement;
        public GpuDeviceInfo Info { get; } = new("GPU", 1, "Test GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public TaskCompletionSource<uint> ReopenedBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OpenCount;
        public int DisposeCount;
        public uint FailedBatchLength;

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [Info];

        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) =>
            new LosingDevice(this, Interlocked.Increment(ref OpenCount));

        private sealed class LosingDevice(LosingBackend backend, int openNumber) : IGpuSearchDevice
        {
            private bool _completed;

            public GpuDeviceInfo Info => backend.Info;
            public int KeysPerBatch => 1;
            public long MaxCandidatesPerBatch => 1 << 22;
            public string KernelVersion => Info.KernelVersion;

            public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
                KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();

            public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
                KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
                CancellationToken cancellationToken = default)
            {
                if (openNumber > 1)
                {
                    backend.ReopenedBatch.TrySetResult(timestampCount);
                    if (backend.FailReplacement && openNumber == 2)
                    {
                        throw new GpuDeviceLostException("Replacement GPU lost during readback.");
                    }
                    if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException("The test did not stop the replacement GPU.");
                    }
                    return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
                }

                if (_completed)
                {
                    backend.FailedBatchLength = timestampCount;
                    throw new GpuDeviceLostException("GPU lost during readback.");
                }

                _completed = true;
                return new SearchBatchResult([[]], timestampCount, timestampCount) { DispatchWallMilliseconds = 1 };
            }

            public void Dispose() => Interlocked.Increment(ref backend.DisposeCount);
        }
    }
}
