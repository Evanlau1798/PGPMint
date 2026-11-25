using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class PasswordVaultPersistenceTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public void ExistingBlankVaultIsBackedUpAndNeverOverwritten(string contents)
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-truncated-" + Guid.NewGuid().ToString("N"));
        try
        {
            var vault = new DpapiVaultStore(directory);
            PrivateFileSystem.CreateDirectory(directory);
            File.WriteAllText(vault.VaultFilePath, contents);
            Assert.Throws<InvalidDataException>(() => vault.LoadMatches());
            Assert.Equal(contents, File.ReadAllText(vault.VaultFilePath));
            string backup = Assert.Single(Directory.GetFiles(directory, "*.corrupt.*.bak"));
            Assert.Equal(contents, File.ReadAllText(backup));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void DpapiRefusesPasswordVaultBeforeAnyReadOrWrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var protector = PasswordSeedProtector.Open(directory, "test-only-password");
            var dpapi = new PGPMint.Core.Storage.DpapiVaultStore(directory);
            Assert.Throws<InvalidDataException>(() => dpapi.LoadMatches());
            Assert.False(File.Exists(Path.Combine(directory, "hits.vault.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RealVaultBindsCiphertextToTheStoredKeyAndProtectsFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-authenticated-" + Guid.NewGuid().ToString("N"));
        var pair = Ed25519KeyGenerator.Generate();
        try
        {
            using var protector = PasswordSeedProtector.Open(directory, "test-only-password");
            var vault = new VaultStore(directory, protector);
            const uint timestamp = 1_700_000_000;
            string fingerprint = OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, timestamp);
            var record = vault.SaveMatch(pair.PrivateSeed, pair.PublicKey, timestamp, fingerprint, "A", "test");
            Assert.Equal(pair.PrivateSeed, vault.DecryptPrivateSeed(vault.LoadMatches().Single()));
            Assert.ThrowsAny<CryptographicException>(() => vault.DecryptPrivateSeed(record with { Id = "swapped-record" }));
            Assert.ThrowsAny<CryptographicException>(() => vault.DecryptPrivateSeed(record with { CreationTimestamp = timestamp + 1 }));
            Assert.DoesNotContain(Convert.ToHexString(pair.PrivateSeed), File.ReadAllText(vault.VaultFilePath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(vault.VaultFilePath));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "vault.key.json")));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pair.PrivateSeed);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void SessionLeaseExcludesAnotherWriterAndReleasesOnDispose()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new VaultSession(directory))
                Assert.Throws<IOException>(() => new VaultSession(directory));
            using var reopened = new VaultSession(directory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
