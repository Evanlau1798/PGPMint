using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public class StorageAtomicRetryTests
{
    [Fact]
    public void DpapiVaultEncryptsPrivateSeedAndRoundTripsCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-vault-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vault = new DpapiVaultStore(tempDir);
            Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();
            uint ts = 1700000000u;
            string fp = OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, ts);

            MatchedKeyRecord saved = vault.SaveMatch(
                pair.PrivateSeed,
                pair.PublicKey,
                ts,
                fp,
                "***1145141919810",
                "Test GPU");

            string rawDiskText = File.ReadAllText(vault.VaultFilePath, Encoding.UTF8);
            string seedHex = Convert.ToHexString(pair.PrivateSeed);
            string seedB64 = Convert.ToBase64String(pair.PrivateSeed);
            Assert.DoesNotContain(seedHex, rawDiskText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(seedB64, rawDiskText, StringComparison.Ordinal);

            IReadOnlyList<MatchedKeyRecord> reloaded = vault.LoadMatches();
            Assert.Single(reloaded);
            Assert.Equal(saved.Fingerprint, reloaded[0].Fingerprint);
            Assert.Equal(saved.KeyId, reloaded[0].KeyId);

            byte[] decryptedSeed = vault.DecryptPrivateSeed(reloaded[0]);
            Assert.Equal(pair.PrivateSeed, decryptedSeed);

            // Saving duplicate fingerprint + timestamp does not create duplicate row
            vault.SaveMatch(pair.PrivateSeed, pair.PublicKey, ts, fp, "***1145141919810", "Test GPU");
            Assert.Single(vault.LoadMatches());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AtomicFileStoreSurvivesTransientWindowsSharingViolation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-retry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "state.json");
            File.WriteAllText(filePath, "{\"TotalCheckedCandidates\":987654321}");

            using (var exclusiveLock = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var releaser = new Thread(() =>
                {
                    Thread.Sleep(120);
                    exclusiveLock.Dispose();
                }) { IsBackground = true };
                releaser.Start();
                string content = AtomicFileStore.ReadTextWithRetry(filePath, "{}");
                releaser.Join();
                Assert.Contains("987654321", content);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AppStateStorePersistsCalibrationsAndCheckedCountsAcrossReload()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-state-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AppStateStore(tempDir);
            AppPersistedState state = store.Load();
            Assert.Equal(1.0, state.SliderPosition, 6);
            state.TotalCheckedCandidates = 42_000_000_000UL;
            state.TargetInput = "1145141919810";
            state.HasCompletedSetup = true;
            state.SliderPosition = 0.37;
            Assert.True(state.ShowMiniHudInTray);
            state.ShowMiniHudInTray = false;
            state.MiniHudX = 1420;
            state.MiniHudY = 980;
            state.Calibrations["GPU-0"] = new CalibrationRecord(
                "GPU-0", "RTX 3080", "32.0.16.1088", "d3d11-sha1-four-v1",
                22_351_000_000.0, 21_100_000_000.0, true, DateTimeOffset.UtcNow);
            store.Save(state);

            AppPersistedState reloaded = new AppStateStore(tempDir).Load();
            Assert.Equal(42_000_000_000UL, reloaded.TotalCheckedCandidates);
            Assert.Equal("1145141919810", reloaded.TargetInput);
            Assert.Equal(0.37, reloaded.SliderPosition, 6);
            Assert.False(reloaded.ShowMiniHudInTray);
            Assert.Equal(1420, reloaded.MiniHudX);
            Assert.Equal(980, reloaded.MiniHudY);
            Assert.True(reloaded.Calibrations.ContainsKey("GPU-0"));
            Assert.Equal(22_351_000_000.0, reloaded.Calibrations["GPU-0"].PeakHashesPerSecond);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DpapiVaultCorruptJsonCreatesBackupAndRefusesToOverwrite()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-corrupt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var vault = new DpapiVaultStore(tempDir);
            string corruptPayload = "[{ \"broken\": ";
            File.WriteAllText(vault.VaultFilePath, corruptPayload);

            Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();
            string fp = OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, 1700000000u);

            Assert.Throws<InvalidDataException>(() =>
                vault.SaveMatch(pair.PrivateSeed, pair.PublicKey, 1700000000u, fp, "***0", "Test GPU"));

            // Original file untouched AND backup created
            Assert.Equal(corruptPayload, File.ReadAllText(vault.VaultFilePath));
            Assert.NotEmpty(Directory.GetFiles(tempDir, "hits.vault.json.corrupt.*.bak"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AtomicFileStoreWriteSurvivesTransientWindowsSharingViolation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-wretry-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            string filePath = Path.Combine(tempDir, "state.json");
            File.WriteAllText(filePath, "{\"v\":1}");

            using (var exclusiveLock = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var releaser = new Thread(() =>
                {
                    Thread.Sleep(80);
                    exclusiveLock.Dispose();
                }) { IsBackground = true };
                releaser.Start();
                AtomicFileStore.WriteTextAtomic(filePath, "{\"v\":2}");
                releaser.Join();
            }

            Assert.Equal("{\"v\":2}", File.ReadAllText(filePath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DpapiVaultPreservesEveryStoredKeyWhenFullAndStillAcceptsDuplicates()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-cap-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vault = new DpapiVaultStore(tempDir);
            Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();
            const uint firstTimestamp = 1700000000u;
            for (uint offset = 0; offset < DpapiVaultStore.MaxStoredMatches; offset++)
            {
                SaveAt(firstTimestamp + offset);
            }

            IReadOnlyList<MatchedKeyRecord> original = vault.LoadMatches();
            Assert.Equal(DpapiVaultStore.MaxStoredMatches, original.Count);
            byte[] before = File.ReadAllBytes(vault.VaultFilePath);
            Assert.Equal(original[^1].Id, SaveAt(firstTimestamp).Id);
            Assert.Equal(before, File.ReadAllBytes(vault.VaultFilePath));

            Exception? rejection = Record.Exception(() => SaveAt(firstTimestamp + DpapiVaultStore.MaxStoredMatches));

            Assert.Equal(original.Select(item => item.Id), vault.LoadMatches().Select(item => item.Id));
            Assert.Equal(before, File.ReadAllBytes(vault.VaultFilePath));
            Assert.Equal(DpapiVaultStore.MaxStoredMatches, Assert.IsType<VaultFullException>(rejection).Capacity);
            Assert.Equal(DpapiVaultStore.MaxStoredMatches, vault.ClearAllMatches());
            Assert.Empty(vault.LoadMatches());

            MatchedKeyRecord SaveAt(uint timestamp)
            {
                string fingerprint = OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, timestamp);
                return vault.SaveMatch(pair.PrivateSeed, pair.PublicKey, timestamp, fingerprint, "***0", "Test GPU");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DpapiVaultAllowsOnlyOneConcurrentSaveForTheLastSlot()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-cap-race-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vault = new DpapiVaultStore(tempDir);
            Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();
            const uint firstTimestamp = 1700000000u;
            uint lastSlot = firstTimestamp + DpapiVaultStore.MaxStoredMatches - 1u;
            for (uint timestamp = firstTimestamp; timestamp < lastSlot; timestamp++)
            {
                SaveAt(vault, timestamp);
            }

            string[] originalIds = vault.LoadMatches().Select(item => item.Id).ToArray();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Exception?>[] writers =
            [
                TrySaveAfterStartAsync(lastSlot),
                TrySaveAfterStartAsync(lastSlot + 1u)
            ];
            start.SetResult();
            Exception?[] outcomes = await Task.WhenAll(writers);

            Assert.Single(outcomes, outcome => outcome is null);
            Assert.IsType<VaultFullException>(Assert.Single(outcomes, outcome => outcome is not null));
            IReadOnlyList<MatchedKeyRecord> stored = vault.LoadMatches();
            Assert.Equal(DpapiVaultStore.MaxStoredMatches, stored.Count);
            Assert.Equal(originalIds, stored.Skip(1).Select(item => item.Id));

            async Task<Exception?> TrySaveAfterStartAsync(uint timestamp)
            {
                await start.Task;
                return Record.Exception(() => SaveAt(new DpapiVaultStore(tempDir), timestamp));
            }

            void SaveAt(DpapiVaultStore target, uint timestamp)
            {
                string fingerprint = OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, timestamp);
                target.SaveMatch(pair.PrivateSeed, pair.PublicKey, timestamp, fingerprint, "***0", "Test GPU");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
