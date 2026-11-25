using System.Collections.Concurrent;
using System.Reflection;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed partial class CoordinatorPersistenceTests
{
    [Fact]
    public async Task ContinuousMintUsesNewPublicKeysForEachSavedRecord()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(false);
            var records = new ConcurrentQueue<MatchedKeyRecord>();
            var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.MatchFound += (_, record) =>
            {
                records.Enqueue(record);
                if (records.Count >= 3)
                {
                    enough.TrySetResult();
                }
            };

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            await enough.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync();

            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.True(records.Count >= 3);
            Assert.Equal(records.Count, records.Select(record => record.PublicKeyHex).Distinct().Count());
            Assert.Equal((long)records.Count, coordinator.LatestTelemetry.SessionWork.SavedKeys);
            Assert.Equal(records.Count, vault.LoadMatches().Count);
            OpenPgpExportGpgTests.VerifyPersistedKeysSign(vault, Path.Combine(directory, "signing-verification"));
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task NotificationExceptionDoesNotFaultOrHideAnAlreadySavedMatch()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);
            int notified = 0;
            coordinator.MatchFound += (_, _) => throw new InvalidOperationException("Test observer failure");
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Idle, final.State);
            Assert.Equal(1, notified);
            Assert.Equal(1L, final.SessionWork.SavedKeys);
            Assert.Single(vault.LoadMatches());
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task IncompleteGpuResultsDoNotCountAsCompletedWork()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend(incomplete: true);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), new DpapiVaultStore(directory));
            coordinator.Initialize();
            Task<CoordinatorTelemetry> terminal = ObserveTerminalState(coordinator);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Faulted, final.State);
            Assert.Equal(SearchFailure.IncompleteResults, final.Failure);
            Assert.Equal(0, final.SessionWork.UniqueCandidatesCompleted);
            Assert.Equal(0, final.SessionWork.SavedKeys);
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task FullVaultRejectsSessionBeforeOpeningAGpu()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            SeedVault(vault, DpapiVaultStore.MaxStoredMatches);
            byte[] before = File.ReadAllBytes(vault.VaultFilePath);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Faulted, final.State);
            Assert.True(final.VaultIsFull);
            Assert.Equal(0, backend.CreatedDevices);
            Assert.Equal(0L, final.SessionWork.SavedKeys);
            Assert.Equal(before, File.ReadAllBytes(vault.VaultFilePath));

            Assert.True(coordinator.DeleteVaultMatch(vault.LoadMatches()[0].Id));
            Assert.False(coordinator.LatestTelemetry.VaultIsFull);
            Assert.Null(coordinator.LatestTelemetry.ErrorMessage);
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task ContinuousMintStopsAfterSavingTheLastAvailableSlot()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            SeedVault(vault, DpapiVaultStore.MaxStoredMatches - 1);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(false);
            var terminal = ObserveTerminalState(coordinator);
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Idle, final.State);
            Assert.True(final.VaultIsFull);
            Assert.Equal(1, notified);
            Assert.Equal(1L, final.SessionWork.SavedKeys);
            Assert.Equal(DpapiVaultStore.MaxStoredMatches, vault.LoadMatches().Count);
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task DelayedFullSnapshotCannotOverwriteTheTelemetryAfterDeletion()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            SeedVault(vault, DpapiVaultStore.MaxStoredMatches);
            string id = vault.LoadMatches()[0].Id;
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry full = await terminal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(full.VaultIsFull);
            await coordinator.StopAsync();
            FieldInfo? field = typeof(MultiGpuCoordinator).GetField("_lifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            object lifecycle = field.GetValue(coordinator)!;

            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            Task older = Task.Run(() => coordinator.PublishTelemetry(() =>
            {
                captured.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The test did not release the captured telemetry.");
                }

                return full;
            }));

            Task<bool> deletion;
            try
            {
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                bool acquired = Monitor.TryEnter(lifecycle);
                if (acquired)
                {
                    Monitor.Exit(lifecycle);
                }

                Assert.False(acquired, "An in-flight snapshot must keep the lifecycle lock until it is published.");
                deletion = Task.Run(() => coordinator.DeleteVaultMatch(id));
            }
            finally
            {
                release.Set();
            }

            Assert.True(await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
            await older.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(coordinator.LatestTelemetry.VaultIsFull);
            Assert.Equal(DpapiVaultStore.MaxStoredMatches - 1, coordinator.LatestTelemetry.TotalMatchesCount);
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    private static void SeedVault(VaultStore vault, int count)
    {
        Ed25519KeyPair key = Ed25519KeyGenerator.Generate();
        for (uint i = 0; i < count; i++)
        {
            uint stamp = 1700000000u + i;
            string fingerprint = OpenPgpV4Packet.ComputeFingerprint(key.PublicKey, stamp);
            vault.SaveMatch(key.PrivateSeed, key.PublicKey, stamp, fingerprint, "***0", "Test GPU");
        }
    }

    private static string NewDirectory() => Path.Combine(Path.GetTempPath(), "pm-persist-" + Guid.NewGuid().ToString("N"));

    private static void CleanDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<CoordinatorTelemetry> ObserveTerminalState(MultiGpuCoordinator coordinator)
    {
        var result = new TaskCompletionSource<CoordinatorTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.TelemetryUpdated += (_, telemetry) =>
        {
            if (telemetry.State is CoordinatorState.Idle or CoordinatorState.Faulted)
            {
                result.TrySetResult(telemetry);
            }
        };
        return result.Task;
    }

    private sealed class CpuFixtureBackend(bool incomplete = false) : IGpuBackend
    {
        private readonly GpuDeviceInfo _info = new("cpu-fixture", 1, "Test GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [_info];
        public int CreatedDevices { get; private set; }

        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo)
        {
            CreatedDevices++;
            return new CpuFixtureDevice(deviceInfo, incomplete);
        }
    }

    private sealed class CpuFixtureDevice(GpuDeviceInfo info, bool incomplete) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 128;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024)
        {
            var hits = new List<uint>();
            for (uint i = 0; i < timestampCount; i++)
            {
                uint stamp = startTimestamp + i;
                if (pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[0], stamp)))
                {
                    hits.Add(stamp);
                }
            }
            return [hits];
        }

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
            }

            IReadOnlyList<uint>[] hits = ScanGroup(publicKeys, startTimestamp, timestampCount, pattern, capacity);
            if (resultMode == SearchResultMode.FindOne)
            {
                hits[0] = hits[0].Take(1).ToArray();
            }
            return new SearchBatchResult(hits, timestampCount, timestampCount)
            {
                ResultsComplete = !incomplete,
                HashRangeFullyProcessed = !incomplete
            };
        }

        public void Dispose() { }
    }
}
