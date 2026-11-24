using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorBatchCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserStopFinishesTheCurrentCalibrationBatchAndSavesItsHit(bool returnHit)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new BlockingScan(returnHit);
            var vault = new DpapiVaultStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            try
            {
                var (count, mode, token) = await backend.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(SearchResultMode.FindOne, mode);
                Assert.InRange((long)count * backend.KeysPerBatch, 1, 1 << 18);
                Assert.True(token.CanBeCanceled);

                Task stop = coordinator.StopAsync();
                Assert.False(stop.IsCompleted);
                backend.AllowReadback.Set();
                await stop.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(token.IsCancellationRequested);
                Assert.Equal(1, backend.DispatchCount);
                Assert.Equal((long)count * backend.KeysPerBatch, coordinator.LatestTelemetry.SessionWork.UniqueCandidatesCompleted);
                Assert.Equal(returnHit ? 1 : 0, coordinator.LatestTelemetry.SessionWork.SavedKeys);
                Assert.Equal(returnHit ? 1 : 0, vault.LoadMatches().Count);
                Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            }
            finally
            {
                backend.AllowReadback.Set();
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
    public async Task WorkerFailureStopsOtherBatchesBeforeTheyCanSave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new BlockingScan(returnHit: true, failOtherWorker: true);
            var vault = new DpapiVaultStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            var faulted = new TaskCompletionSource<CoordinatorTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.State == CoordinatorState.Faulted)
                {
                    faulted.TrySetResult(telemetry);
                }
            };

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            try
            {
                var (_, _, token) = await backend.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = token.Register(() => cancelled.TrySetResult());
                await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                backend.AllowReadback.Set();
            }

            CoordinatorTelemetry final = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, final.SessionWork.ReturnedHits);
            Assert.Equal(0, final.SessionWork.SavedKeys);
            Assert.Empty(vault.LoadMatches());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class BlockingScan(bool returnHit, bool failOtherWorker = false) : IGpuBackend, IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = new("test-gpu", 1, "Test GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 1L << 30;
        public string KernelVersion => Info.KernelVersion;
        public TaskCompletionSource<(uint Count, SearchResultMode Mode, CancellationToken Token)> FirstBatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowReadback { get; } = new();
        public int DispatchCount;

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() =>
            failOtherWorker ? [Info, Info with { DeviceKey = "failing-gpu", AdapterLuid = 2 }] : [Info];

        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) =>
            deviceInfo.DeviceKey == Info.DeviceKey ? this : new FailingScan(deviceInfo, FirstBatch.Task);

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024) => throw new InvalidOperationException("Unexpected complete enumeration.");

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DispatchCount);
            FirstBatch.TrySetResult((timestampCount, resultMode, cancellationToken));
            if (!AllowReadback.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release GPU readback.");
            }
            long candidates = (long)timestampCount * KeysPerBatch;
            var hits = SearchBatchResult.EmptyHits(KeysPerBatch);
            if (returnHit)
            {
                var (lane, timestamp) =
                    (from index in Enumerable.Range(0, KeysPerBatch)
                     from offset in Enumerable.Range(0, checked((int)timestampCount))
                     let stamp = startTimestamp + (uint)offset
                     where pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[index], stamp))
                     select (index, stamp)).First();
                hits[lane] = [timestamp];
            }
            return new SearchBatchResult(hits, candidates, candidates);
        }

        void IDisposable.Dispose() { }
        public void Dispose() => AllowReadback.Dispose();
    }

    private sealed class FailingScan(GpuDeviceInfo info, Task dispatchStarted) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info => info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 128;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            dispatchStarted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).GetAwaiter().GetResult();
            throw new InvalidOperationException("Simulated worker failure.");
        }

        public void Dispose() { }
    }
}
