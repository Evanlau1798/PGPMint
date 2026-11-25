using System.Reflection;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed partial class CoordinatorPersistenceTests
{
    [Fact]
    public async Task VaultWriteFailureDoesNotReportAFind()
    {
        string directory = NewDirectory();
        try
        {
            var vault = new TestVaultStore(directory);
            PrivateFileSystem.CreateDirectory(directory);
            Directory.CreateDirectory(vault.VaultFilePath);
            string sentinel = Path.Combine(vault.VaultFilePath, "original-data");
            byte[] before = [1, 2, 3, 4];
            File.WriteAllBytes(sentinel, before);
            using var coordinator = new MultiGpuCoordinator(new CpuFixtureBackend(), new AppStateStore(directory), vault);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Faulted, final.State);
            Assert.Equal(SearchFailure.SaveFailed, final.Failure);
            Assert.Equal(1L, final.SessionWork.VerifiedHits);
            Assert.Equal(0, notified);
            Assert.Equal(0L, final.SessionWork.SavedKeys);
            Assert.True(Directory.Exists(vault.VaultFilePath));
            Assert.Equal(sentinel, Assert.Single(Directory.EnumerateFileSystemEntries(vault.VaultFilePath)));
            Assert.Equal(before, File.ReadAllBytes(sentinel));
            Assert.Empty(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task StopWaitsForAnAlreadyStartedAtomicSave()
    {
        string directory = NewDirectory();
        try
        {
            using var protector = new BlockingSeedProtector();
            var vault = new TestVaultStore(directory, protector);
            using var coordinator = new MultiGpuCoordinator(new CpuFixtureBackend(), new AppStateStore(directory), vault);
            coordinator.Initialize();
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);
            protector.Arm();
            Task stop;
            try
            {
                await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
                await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                stop = coordinator.StopAsync();
                Assert.False(stop.IsCompleted);
                Assert.Equal(CoordinatorState.Stopping, coordinator.CurrentState);
                Assert.Equal(0, notified);
                Assert.False(File.Exists(vault.VaultFilePath));
            }
            finally
            {
                protector.Release.Set();
            }
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(1, notified);
            Assert.Equal(1L, coordinator.LatestTelemetry.SessionWork.SavedKeys);
            MatchedKeyRecord saved = Assert.Single(vault.LoadMatches());
            Assert.Equal(32, vault.DecryptPrivateSeed(saved).Length);
            Assert.Empty(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingVaultRecordsWaitsForThePendingSaveDecision(bool clearAll)
    {
        string directory = NewDirectory();
        try
        {
            using var protector = new BlockingSeedProtector();
            var vault = new TestVaultStore(directory, protector);
            SeedVault(vault, VaultStore.MaxStoredMatches - 1);
            string removeId = vault.LoadMatches()[0].Id;
            using var coordinator = new MultiGpuCoordinator(new CpuFixtureBackend(), new AppStateStore(directory), vault);
            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(false);
            var terminal = ObserveTerminalState(coordinator);
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);
            protector.Arm();
            Task<bool> deletion;
            try
            {
                await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
                await protector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, notified);

                FieldInfo? field = typeof(MultiGpuCoordinator).GetField("_lifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                object lifecycle = field.GetValue(coordinator)!;
                using var scheduled = new ManualResetEventSlim();
                lock (lifecycle)
                {
                    // Hold the capacity decision after persistence, independently of the vault's own lock.
                    protector.Release.Set();
                    Assert.Equal(VaultStore.MaxStoredMatches, vault.LoadMatches().Count);
                    deletion = Task.Run(() =>
                    {
                        scheduled.Set();
                        return clearAll ? coordinator.ClearVaultMatches() > 0 : coordinator.DeleteVaultMatch(removeId);
                    });
                    Assert.True(scheduled.Wait(TimeSpan.FromSeconds(2)));
                    Assert.False(SpinWait.SpinUntil(() => vault.LoadMatches().Count < VaultStore.MaxStoredMatches,
                        TimeSpan.FromMilliseconds(250)));
                    Assert.False(deletion.IsCompleted);
                }
            }
            finally
            {
                protector.Release.Set();
            }

            Assert.True(await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
            await terminal.WaitAsync(TimeSpan.FromSeconds(5));
            int expected = clearAll ? 0 : VaultStore.MaxStoredMatches - 1;
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(1, notified);
            Assert.Equal(1L, coordinator.LatestTelemetry.SessionWork.SavedKeys);
            Assert.Equal(expected, vault.LoadMatches().Count);
            Assert.Equal(expected, coordinator.LatestTelemetry.TotalMatchesCount);
            Assert.False(coordinator.LatestTelemetry.VaultIsFull);
            Assert.Null(coordinator.LatestTelemetry.ErrorMessage);
            Assert.DoesNotContain(vault.LoadMatches(), record => record.Id == removeId);
            Assert.Empty(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    private sealed class BlockingSeedProtector : IPrivateSeedProtector, IDisposable
    {
        private readonly IPrivateSeedProtector _inner = TestVaultStore.CreateProtector();
        private bool _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public void Arm() => _armed = true;

        public byte[] Protect(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> context)
        {
            if (_armed)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release the reserved save.");
                }
            }
            return _inner.Protect(seed, context);
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context) =>
            _inner.Unprotect(ciphertext, context);

        public void Dispose()
        {
            Release.Set();
            Release.Dispose();
        }
    }
}
