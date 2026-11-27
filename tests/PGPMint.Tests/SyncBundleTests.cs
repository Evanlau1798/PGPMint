using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class SyncBundleTests
{
    [Fact]
    public void ProgressOnlyRoundTripIsIdempotentAndAppliesOnlyHigherSessionDeltas()
    {
        using var root = new TempDir();
        string dirA = Path.Combine(root.Path, "a");
        string dirB = Path.Combine(root.Path, "b");
        string bundle1 = Path.Combine(root.Path, "p1.pgpmint-sync");
        string bundle2 = Path.Combine(root.Path, "p2.pgpmint-sync");

        var storeA = new AppStateStore(dirA);
        storeA.Update(s =>
        {
            s.LastPatternMask = "********DEADBEEF";
            s.TargetInput = "DEADBEEF";
            s.ProgressLedger["sess-1"] = 10_000;
            s.ProgressLedger["sess-2"] = 5_000;
            s.TotalCheckedCandidates = 15_000;
        });

        SyncExportResult exp1 = SyncBundleService.Export(storeA, vaultStore: null, bundle1, includeKeys: false, ReadOnlySpan<char>.Empty);
        Assert.False(exp1.HasEncryptedKeys);
        Assert.Equal(0, exp1.ExportedKeysCount);
        Assert.Equal(15_000UL, exp1.TotalCheckedCandidates);

        var storeB = new AppStateStore(dirB);
        SyncImportResult imp1 = SyncBundleService.Import(storeB, vaultStore: null, bundle1, ReadOnlySpan<char>.Empty);
        Assert.False(imp1.PatternMismatchSkippedProgress);
        Assert.Equal(15_000UL, imp1.AddedCandidates);
        Assert.Equal(15_000UL, imp1.NewTotalCheckedCandidates);

        // Re-importing the exact same bundle must add zero candidates.
        SyncImportResult impAgain = SyncBundleService.Import(storeB, vaultStore: null, bundle1, ReadOnlySpan<char>.Empty);
        Assert.Equal(0UL, impAgain.AddedCandidates);
        Assert.Equal(15_000UL, impAgain.NewTotalCheckedCandidates);

        // Advancing one session on A merges only the positive delta.
        storeA.Update(s =>
        {
            s.ProgressLedger["sess-2"] = 9_000;
            s.TotalCheckedCandidates = 19_000;
        });
        SyncBundleService.Export(storeA, vaultStore: null, bundle2, includeKeys: false, ReadOnlySpan<char>.Empty);

        SyncImportResult imp2 = SyncBundleService.Import(storeB, vaultStore: null, bundle2, ReadOnlySpan<char>.Empty);
        Assert.Equal(4_000UL, imp2.AddedCandidates);
        Assert.Equal(19_000UL, imp2.NewTotalCheckedCandidates);
    }

    [Fact]
    public void LegacyTotalCheckedCandidatesUpgradesIntoLedgerCleanly()
    {
        using var root = new TempDir();
        string dirA = Path.Combine(root.Path, "legacy");
        string dirB = Path.Combine(root.Path, "target");
        string bundle = Path.Combine(root.Path, "legacy.pgpmint-sync");

        var storeA = new AppStateStore(dirA);
        storeA.Update(s =>
        {
            s.LastPatternMask = "****CAFE";
            s.TargetInput = "CAFE";
            s.TotalCheckedCandidates = 42_000;
            s.ProgressLedger.Clear();
        });

        SyncBundleService.Export(storeA, vaultStore: null, bundle, includeKeys: false, ReadOnlySpan<char>.Empty);

        var storeB = new AppStateStore(dirB);
        SyncImportResult first = SyncBundleService.Import(storeB, vaultStore: null, bundle, ReadOnlySpan<char>.Empty);
        SyncImportResult second = SyncBundleService.Import(storeB, vaultStore: null, bundle, ReadOnlySpan<char>.Empty);

        Assert.Equal(42_000UL, first.AddedCandidates);
        Assert.Equal(0UL, second.AddedCandidates);
        AppPersistedState persisted = storeB.Load();
        Assert.Equal(42_000UL, persisted.ProgressLedger["legacy"]);
    }

    [Fact]
    public void EncryptedKeysCrossVaultRoundTripValidatesPassphraseTamperingAndDuplicates()
    {
        using var root = new TempDir();
        string dirA = Path.Combine(root.Path, "vault-a");
        string dirB = Path.Combine(root.Path, "vault-b");
        string bundle = Path.Combine(root.Path, "keys.pgpmint-sync");
        PrivateFileSystem.CreateDirectory(dirA);
        PrivateFileSystem.CreateDirectory(dirB);

        using var protectorA = PasswordSeedProtector.Open(dirA, "vault-a-pass".AsSpan());
        using var protectorB = PasswordSeedProtector.Open(dirB, "vault-b-pass".AsSpan());
        var vaultA = new VaultStore(dirA, protectorA);
        var vaultB = new VaultStore(dirB, protectorB);
        var stateA = new AppStateStore(dirA);
        var stateB = new AppStateStore(dirB);

        stateA.Update(s =>
        {
            s.LastPatternMask = "********CAFEBABE";
            s.TargetInput = "CAFEBABE";
            s.ProgressLedger["gpu-a"] = 7_500;
            s.TotalCheckedCandidates = 7_500;
        });

        Ed25519KeyPair kp = Ed25519KeyGenerator.Generate();
        uint ts = 1_700_000_123u;
        string fp = OpenPgpV4Packet.ComputeFingerprint(kp.PublicKey, ts);
        try
        {
            vaultA.SaveMatch(kp.PrivateSeed, kp.PublicKey, ts, fp, "********CAFEBABE", "RTX 5090", out _, out _);

            SyncExportResult exported = SyncBundleService.Export(stateA, vaultA, bundle, includeKeys: true, "bundle-secret".AsSpan());
            Assert.True(exported.HasEncryptedKeys);
            Assert.Equal(1, exported.ExportedKeysCount);

            SyncBundleInfo info = SyncBundleService.Inspect(bundle);
            Assert.True(info.HasEncryptedKeys);
            Assert.Equal(1, info.KeyCount);

            // Wrong passphrase must fail decryption without polluting target vault.
            Assert.ThrowsAny<CryptographicException>(() =>
                SyncBundleService.Import(stateB, vaultB, bundle, "wrong-secret".AsSpan()));
            Assert.Empty(vaultB.LoadMatches());

            // Tampering with AAD-bound PatternMask must fail GCM authentication.
            string tamperedPath = Path.Combine(root.Path, "tampered.pgpmint-sync");
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(bundle, Encoding.UTF8)))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
                node["PatternMask"] = "********DEADBEEF";
                File.WriteAllText(tamperedPath, node.ToJsonString(), Encoding.UTF8);
            }
            Assert.ThrowsAny<CryptographicException>(() =>
                SyncBundleService.Import(stateB, vaultB, tamperedPath, "bundle-secret".AsSpan()));

            // Valid import succeeds and recovers identical private seed.
            SyncImportResult first = SyncBundleService.Import(stateB, vaultB, bundle, "bundle-secret".AsSpan());
            Assert.Equal(1, first.ImportedKeysCount);
            Assert.Equal(0, first.SkippedDuplicateKeysCount);
            Assert.Equal(7_500UL, first.AddedCandidates);

            IReadOnlyList<MatchedKeyRecord> importedMatches = vaultB.LoadMatches();
            MatchedKeyRecord rec = Assert.Single(importedMatches);
            byte[] recoveredSeed = vaultB.DecryptPrivateSeed(rec);
            try
            {
                Assert.True(recoveredSeed.AsSpan().SequenceEqual(kp.PrivateSeed));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveredSeed);
            }

            // Re-importing skips duplicate key and adds zero progress.
            SyncImportResult duplicate = SyncBundleService.Import(stateB, vaultB, bundle, "bundle-secret".AsSpan());
            Assert.Equal(0, duplicate.ImportedKeysCount);
            Assert.Equal(1, duplicate.SkippedDuplicateKeysCount);
            Assert.Equal(0UL, duplicate.AddedCandidates);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kp.PrivateSeed);
        }
    }

    [Fact]
    public void PatternMismatchSkipsProgressMerge()
    {
        using var root = new TempDir();
        string dirA = Path.Combine(root.Path, "a");
        string dirB = Path.Combine(root.Path, "b");
        string bundle = Path.Combine(root.Path, "mismatch.pgpmint-sync");

        var storeA = new AppStateStore(dirA);
        storeA.Update(s =>
        {
            s.LastPatternMask = "****CAFE";
            s.ProgressLedger["a"] = 25_000;
            s.TotalCheckedCandidates = 25_000;
        });

        var storeB = new AppStateStore(dirB);
        storeB.Update(s =>
        {
            s.LastPatternMask = "****BEEF";
            s.ProgressLedger["b"] = 1_000;
            s.TotalCheckedCandidates = 1_000;
        });

        SyncBundleService.Export(storeA, vaultStore: null, bundle, includeKeys: false, ReadOnlySpan<char>.Empty);
        SyncImportResult result = SyncBundleService.Import(storeB, vaultStore: null, bundle, ReadOnlySpan<char>.Empty);

        Assert.True(result.PatternMismatchSkippedProgress);
        Assert.Equal(0UL, result.AddedCandidates);
        Assert.Equal(1_000UL, storeB.Load().TotalCheckedCandidates);
    }

    [Fact]
    public async Task LiveHotImportIntoRunningCoordinatorPreservesDeltaAcrossBatchesAndStop()
    {
        using var root = new TempDir();
        string dirPeer = Path.Combine(root.Path, "peer");
        string dirLive = Path.Combine(root.Path, "live");
        string bundle = Path.Combine(root.Path, "hot.pgpmint-sync");

        var peerStore = new AppStateStore(dirPeer);
        peerStore.Update(s =>
        {
            s.LastPatternMask = "********DEADBEEF";
            s.TargetInput = "DEADBEEF";
            s.ProgressLedger["cloud-pod"] = 200_000;
            s.TotalCheckedCandidates = 200_000;
        });
        SyncBundleService.Export(peerStore, vaultStore: null, bundle, includeKeys: false, ReadOnlySpan<char>.Empty);

        var liveBackend = new ContinuousNoHitBackend();
        var liveStore = new AppStateStore(dirLive);
        var liveVault = new DpapiVaultStore(dirLive);
        var options = new CoordinatorOptions
        {
            FullCalibrationDuration = TimeSpan.FromMilliseconds(50),
            TelemetryInterval = TimeSpan.FromMilliseconds(30)
        };

        using var coordinator = new MultiGpuCoordinator(liveBackend, liveStore, liveVault, options);
        coordinator.Initialize();

        await coordinator.StartSearchAsync(KeyIdPattern.Parse("DEADBEEF"));
        await WaitUntilCheckedAtLeastAsync(coordinator, 256, TimeSpan.FromSeconds(5));

        SyncImportResult hotResult = coordinator.ImportSyncBundle(bundle, ReadOnlySpan<char>.Empty);
        Assert.False(hotResult.PatternMismatchSkippedProgress);
        Assert.Equal(200_000UL, hotResult.AddedCandidates);

        ulong checkedAfterImport = hotResult.NewTotalCheckedCandidates;
        Assert.True(checkedAfterImport >= 200_000UL);

        // Allow more worker batches to complete while running, then stop cleanly.
        await WaitUntilCheckedAtLeastAsync(coordinator, checkedAfterImport + 256UL, TimeSpan.FromSeconds(5));
        await coordinator.StopAsync();

        AppPersistedState finalState = liveStore.Load();
        Assert.True(finalState.TotalCheckedCandidates >= checkedAfterImport + 256UL,
            $"Expected persisted total >= {checkedAfterImport + 256UL}, got {finalState.TotalCheckedCandidates}.");
        Assert.Equal(200_000UL, finalState.ProgressLedger["cloud-pod"]);
    }

    private static async Task WaitUntilCheckedAtLeastAsync(MultiGpuCoordinator coordinator, ulong threshold, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (coordinator.LatestTelemetry.TotalChecked >= threshold)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for TotalChecked >= {threshold} (actual={coordinator.LatestTelemetry.TotalChecked}).");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PGPMint-SyncTest-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    private sealed class ContinuousNoHitBackend : IGpuBackend
    {
        private readonly GpuDeviceInfo _info = new("sync-mock", 1, "Sync Mock GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [_info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => new ContinuousNoHitDevice(deviceInfo);
    }

    private sealed class ContinuousNoHitDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 128;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024) =>
            [Array.Empty<uint>()];

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
            }

            Thread.Sleep(5);
            return new SearchBatchResult([Array.Empty<uint>()], timestampCount, timestampCount)
            {
                ResultsComplete = true,
                HashRangeFullyProcessed = true
            };
        }

        public void Dispose() { }
    }
}
