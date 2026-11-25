using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class VaultPermissionTests
{
    [UnixFact]
    public void ExistingSharedDirectoriesAreRejectedWithoutChangingPermissionsOrContents()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            foreach (UnixFileMode mode in new[] { (UnixFileMode)0x1ED, (UnixFileMode)0x3FF })
            {
                File.SetUnixFileMode(directory, mode);
                Assert.Throws<IOException>(() => new VaultSession(directory));
                Assert.Equal(mode, File.GetUnixFileMode(directory));
                Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            }
            string sentinel = Path.Combine(directory, "unrelated.txt");
            File.WriteAllText(sentinel, "shared data");
            Assert.Throws<IOException>(() => PasswordSeedProtector.Open(directory, "test password"));
            Assert.Equal((UnixFileMode)0x3FF, File.GetUnixFileMode(directory));
            Assert.Equal("shared data", File.ReadAllText(sentinel));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [UnixFact]
    public void VaultCannotChangeFilesystemRootPermissions()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        UnixFileMode before = File.GetUnixFileMode("/");
        Assert.Throws<IOException>(() => new VaultSession("/"));
        Assert.Equal(before, File.GetUnixFileMode("/"));
    }

    [UnixFact]
    public void SymlinkHeaderIsRejectedWithoutChangingTheExternalTarget()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-link-" + Guid.NewGuid().ToString("N"));
        string outside = directory + "-outside";
        try
        {
            using (var created = PasswordSeedProtector.Open(directory, "test password")) { }
            string header = Path.Combine(directory, "vault.key.json");
            File.Move(header, outside);
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            byte[] before = File.ReadAllBytes(outside);
            UnixFileMode mode = File.GetUnixFileMode(outside);
            File.CreateSymbolicLink(header, outside);
            Assert.Throws<IOException>(() => PasswordSeedProtector.Open(directory, "test password"));
            Assert.Equal(before, File.ReadAllBytes(outside));
            Assert.Equal(mode, File.GetUnixFileMode(outside));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); if (File.Exists(outside)) File.Delete(outside); }
    }

    [UnixFact]
    public void LooseDirectoryIsRefusedUntilExplicitlySecuredThenPrivateFilesAreHardened()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-permissions-" + Guid.NewGuid().ToString("N"));
        var pair = Ed25519KeyGenerator.Generate();
        try
        {
            using (var protector = PasswordSeedProtector.Open(directory, "test password"))
            {
                var vault = new VaultStore(directory, protector);
                const uint timestamp = 1_700_000_000;
                vault.SaveMatch(pair.PrivateSeed, pair.PublicKey, timestamp,
                    OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, timestamp), "A", "test");
            }
            string header = Path.Combine(directory, "vault.key.json"), file = Path.Combine(directory, "hits.vault.json");
            const UnixFileMode looseDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            const UnixFileMode looseFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(directory, looseDirectory);
            File.SetUnixFileMode(header, looseFile);
            File.SetUnixFileMode(file, looseFile);
            byte[] headerBefore = File.ReadAllBytes(header), vaultBefore = File.ReadAllBytes(file);
            Assert.Throws<IOException>(() => PasswordSeedProtector.Open(directory, "test password"));
            Assert.Equal(looseDirectory, File.GetUnixFileMode(directory));
            Assert.Equal(looseFile, File.GetUnixFileMode(header));
            Assert.Equal(looseFile, File.GetUnixFileMode(file));
            Assert.Equal(headerBefore, File.ReadAllBytes(header));
            Assert.Equal(vaultBefore, File.ReadAllBytes(file));
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var reopened = PasswordSeedProtector.Open(directory, "test password");
            var store = new VaultStore(directory, reopened);
            Assert.Single(store.LoadMatches());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(header));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally { CryptographicOperations.ZeroMemory(pair.PrivateSeed); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix file permissions require a Unix host."; }
}
