using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Desktop.Tests;

public sealed class ActiveSessionTests
{
    [Fact]
    public async Task LockWaitsForWorkerCancellationAndDeviceDisposalBeforeReleasingVault()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "tmp",
            "pgpmint-desktop-tests", Guid.NewGuid().ToString("N"));
        var backend = new BlockingGpu();
        try
        {
            await using var session = new DesktopSession(backend, directory);
            session.Unlock("test password");
            var coordinator = session.Coordinator!;
            backend.OnDispose = () => Assert.Throws<IOException>(() => new VaultSession(directory));
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("ABCDEF"));
            await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(backend.Cancelled);
            Assert.True(backend.Disposed);
            Assert.Null(session.Coordinator);
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            using var lease = new VaultSession(directory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class BlockingGpu : IGpuBackend, IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = new("desktop-test", 0, "Fake GPU", 0, 0, 0, 0, 0, "test", true);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 4096;
        public string KernelVersion => Info.KernelVersion;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnDispose { get; set; }
        public bool Cancelled { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [Info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => this;
        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> keys, uint first, uint count, KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();
        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern,
            int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new TimeoutException("Worker did not receive cancellation.");
            Cancelled = cancellationToken.IsCancellationRequested;
            return new([[], [], [], []], 0, 0) { WasCancelled = true, HashRangeFullyProcessed = false, ResultsComplete = false };
        }
        public void Dispose() { OnDispose?.Invoke(); Disposed = true; }
    }
}
