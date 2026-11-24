using System.Reflection;
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
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
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
                await Task.WhenAll(backend.WinnerReadbackPending.Task, backend.FollowerReadbackPending.Task)
                    .WaitAsync(TimeSpan.FromSeconds(5));

                object vaultGate = typeof(DpapiVaultStore)
                    .GetField("VaultAccessGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                object submissionGate = typeof(MultiGpuCoordinator)
                    .GetField("_submissionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)!;

                lock (vaultGate)
                {
                    backend.ReleaseWinnerReadback.Set();
                    Assert.True(SpinWait.SpinUntil(() =>
                    {
                        if (!Monitor.TryEnter(submissionGate))
                        {
                            return true;
                        }
                        Monitor.Exit(submissionGate);
                        return false;
                    }, TimeSpan.FromSeconds(2)), "Winner did not reserve the vault write.");

                    backend.ReleaseFollowerReadback.Set();
                    Assert.True(backend.FollowerDispatchedAgain.Wait(TimeSpan.FromSeconds(2)),
                        "Follower did not dispatch while the winner waited for the vault.");
                    Assert.Equal(0, Volatile.Read(ref saved));
                }
            }
            finally
            {
                backend.ReleaseWinnerReadback.Set();
                backend.ReleaseFollowerReadback.Set();
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
        public ManualResetEventSlim ReleaseWinnerReadback { get; } = new();
        public TaskCompletionSource FollowerReadbackPending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WinnerReadbackPending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim FollowerDispatchedAgain { get; } = new();

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _gpus;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new FixtureDevice(info, this);
        public void Dispose()
        {
            ReleaseFollowerReadback.Dispose();
            ReleaseWinnerReadback.Dispose();
            FollowerDispatchedAgain.Dispose();
        }
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
                    backend.FollowerReadbackPending.TrySetResult();
                    backend.ReleaseFollowerReadback.Wait(cancellationToken);
                }
                else
                {
                    backend.FollowerDispatchedAgain.Set();
                }
            }
            else
            {
                backend.WinnerReadbackPending.TrySetResult();
                backend.ReleaseWinnerReadback.Wait(cancellationToken);
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
