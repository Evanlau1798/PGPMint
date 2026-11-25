#if WINDOWS
using System.Diagnostics;
using System.Reflection;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed partial class CoordinatorPersistenceTests
{
    [Fact]
    public async Task VaultWriteFailureDoesNotReportAFindWithWindowsSharing()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(vault.VaultFilePath, "[]");
            byte[] before = File.ReadAllBytes(vault.VaultFilePath);
            using var blockReplace = new FileStream(vault.VaultFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            CoordinatorTelemetry final = await terminal.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Faulted, final.State);
            Assert.Equal(SearchFailure.SaveFailed, final.Failure);
            Assert.Equal(0, notified);
            Assert.Equal(0L, final.SessionWork.SavedKeys);
            Assert.Equal(before, File.ReadAllBytes(vault.VaultFilePath));
        }
        finally
        {
            CleanDirectory(directory);
        }
    }

    [Fact]
    public async Task StopWaitsForAnAlreadyStartedAtomicSaveWithWindowsSharing()
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(vault.VaultFilePath, "[]");
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            using var blockReplace = new FileStream(vault.VaultFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            coordinator.Initialize();
            int notified = 0;
            coordinator.MatchFound += (_, _) => Interlocked.Increment(ref notified);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            long started = Stopwatch.GetTimestamp();
            while (!Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp").Any()
                   && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(10);
            }
            Assert.Single(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));

            Task stop = coordinator.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, notified);
            blockReplace.Dispose();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(1, notified);
            Assert.Equal(1L, coordinator.LatestTelemetry.SessionWork.SavedKeys);
            Assert.Single(vault.LoadMatches());
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
    public async Task RemovingVaultRecordsWaitsForThePendingSaveDecisionWithWindowsSharing(bool clearAll)
    {
        string directory = NewDirectory();
        try
        {
            var backend = new CpuFixtureBackend();
            var vault = new DpapiVaultStore(directory);
            SeedVault(vault, DpapiVaultStore.MaxStoredMatches - 1);
            string removeId = vault.LoadMatches()[0].Id;
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), vault);
            using var blockReplace = new FileStream(vault.VaultFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            coordinator.Initialize();
            var terminal = ObserveTerminalState(coordinator);

            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0"));
            long started = Stopwatch.GetTimestamp();
            while (!Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp").Any()
                   && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(10);
            }
            Assert.Single(Directory.EnumerateFiles(directory, ".hits.vault.json.*.tmp"));

            FieldInfo? field = typeof(MultiGpuCoordinator).GetField("_lifecycleLock", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            object lifecycle = field.GetValue(coordinator)!;
            Task<bool> deletion;
            using var scheduled = new ManualResetEventSlim();
            lock (lifecycle)
            {
                blockReplace.Dispose();
                Assert.Equal(DpapiVaultStore.MaxStoredMatches, vault.LoadMatches().Count);
                deletion = Task.Run(() =>
                {
                    scheduled.Set();
                    return clearAll ? coordinator.ClearVaultMatches() > 0 : coordinator.DeleteVaultMatch(removeId);
                });
                Assert.True(scheduled.Wait(TimeSpan.FromSeconds(2)));
                Assert.False(SpinWait.SpinUntil(() => vault.LoadMatches().Count < DpapiVaultStore.MaxStoredMatches,
                    TimeSpan.FromMilliseconds(250)));
            }

            Assert.True(await deletion.WaitAsync(TimeSpan.FromSeconds(5)));
            await terminal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(clearAll ? 0 : DpapiVaultStore.MaxStoredMatches - 1, coordinator.LatestTelemetry.TotalMatchesCount);
            Assert.False(coordinator.LatestTelemetry.VaultIsFull);
        }
        finally
        {
            CleanDirectory(directory);
        }
    }
}
#endif
