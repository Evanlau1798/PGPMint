using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorBatchCancellationTests
{
    [Fact]
    public async Task InitialCalibrationRequestsSmallFindOneBatchAndStopsAtItsBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new BlockingScan();
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), new DpapiVaultStore(directory));
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

    private sealed class BlockingScan : IGpuBackend, IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = new("test-gpu", 1, "Test GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 1L << 30;
        public string KernelVersion => Info.KernelVersion;
        public TaskCompletionSource<(uint Count, SearchResultMode Mode, CancellationToken Token)> FirstBatch { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowReadback { get; } = new();
        public int DispatchCount;

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [Info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => this;

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
            return new SearchBatchResult([[], [], [], []], candidates, candidates);
        }

        void IDisposable.Dispose() { }
        public void Dispose() => AllowReadback.Dispose();
    }
}
