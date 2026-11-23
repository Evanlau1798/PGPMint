using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorReservationTests
{
    [Fact]
    public async Task WaitingForVaultWriteDoesNotPauseTheOtherWorker()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-reserve-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var backend = new TwoWorkerBackend();
            var vault = new DpapiVaultStore(directory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(vault.VaultFilePath, "[]");
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            using var blockReplace = new FileStream(vault.VaultFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            coordinator.Initialize();
            var terminal = new TaskCompletionSource<CoordinatorState>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.State is CoordinatorState.Idle or CoordinatorState.Faulted)
                {
                    terminal.TrySetResult(telemetry.State);
                }
            };
            int saved = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref saved);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            try
            {
                long started = Stopwatch.GetTimestamp();
                while (!Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp").Any()
                    && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
                {
                    await Task.Delay(10);
                }
                Assert.Single(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));

                backend.ReleaseFollowerReadback.Set();
                Task nextBatch = backend.FollowerDispatchedAgain.Task;
                Assert.Same(nextBatch, await Task.WhenAny(nextBatch, Task.Delay(750)));
                Assert.Equal(0, saved);
            }
            finally
            {
                backend.ReleaseFollowerReadback.Set();
                blockReplace.Dispose();
            }

            Assert.Equal(CoordinatorState.Idle, await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, saved);
            Assert.Single(vault.LoadMatches());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class TwoWorkerBackend : IGpuBackend, IDisposable
    {
        private readonly GpuDeviceInfo[] _gpus =
        [
            new("winner", 1, "Winner", 0x10DE, 1, 0, 1, 0, "1", true),
            new("follower", 2, "Follower", 0x10DE, 1, 0, 1, 0, "1", true)
        ];

        public ManualResetEventSlim ReleaseFollowerReadback { get; } = new();
        public TaskCompletionSource FollowerDispatchedAgain { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _gpus;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new FixtureDevice(info, this);
        public void Dispose() => ReleaseFollowerReadback.Dispose();
    }

    private sealed class FixtureDevice(GpuDeviceInfo info, TwoWorkerBackend backend) : IGpuSearchDevice
    {
        private int _batches;
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 1024;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp,
            uint timestampCount, KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (Info.DeviceKey == "follower")
            {
                if (++_batches == 1)
                {
                    backend.ReleaseFollowerReadback.Wait(TimeSpan.FromSeconds(3));
                }
                else
                {
                    backend.FollowerDispatchedAgain.TrySetResult();
                }
            }
            else
            {
                for (uint offset = 0; offset < timestampCount; offset++)
                {
                    uint timestamp = startTimestamp + offset;
                    if (pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[0], timestamp)))
                    {
                        return new SearchBatchResult([new[] { timestamp }], timestampCount, timestampCount);
                    }
                }
            }

            return new SearchBatchResult([[]], timestampCount, timestampCount);
        }

        public void Dispose() { }
    }
}
