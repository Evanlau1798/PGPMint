using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Desktop.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "tmp", "pgpmint-desktop-tests", Guid.NewGuid().ToString("N"));
    private sealed class NoGpu : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => throw new NotSupportedException();
    }

    [Fact]
    public void EmptyPasswordIsRejectedWithoutCreatingVault()
    {
        var session = new DesktopSession(new NoGpu(), _directory);
        Assert.Throws<ArgumentException>(() => session.Unlock([]));
        Assert.Null(session.Coordinator);
        Assert.False(File.Exists(Path.Combine(_directory, "vault.key.json")));
    }

    [Fact]
    public async Task NoGpuStillUnlocksLoadsAndStopsCleanly()
    {
        await using var session = new DesktopSession(new NoGpu(), _directory);
        session.Unlock("test password".AsSpan());
        Assert.Empty(session.Matches());
        Assert.Empty(session.Coordinator!.Devices);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Coordinator.StartSearchAsync(KeyIdPattern.Parse("AB")));
        await session.DisposeAsync();
        Assert.Null(session.Coordinator);
        using var released = new VaultSession(_directory);
    }

    [Fact]
    public async Task WrongPasswordReleasesExclusiveSession()
    {
        using (PasswordSeedProtector.Open(_directory, "right password")) { }
        await using var session = new DesktopSession(new NoGpu(), _directory);
        Assert.ThrowsAny<CryptographicException>(() => session.Unlock("wrong password"));
        Assert.Null(session.Coordinator);
        using var released = new VaultSession(_directory);
    }

    [Fact]
    public async Task SecondDesktopCannotUnlockTheSameDirectory()
    {
        await using var first = new DesktopSession(new NoGpu(), _directory);
        first.Unlock("test password");
        await using var second = new DesktopSession(new NoGpu(), _directory);
        Assert.Throws<IOException>(() => second.Unlock("test password"));
    }

    [Fact]
    public async Task ExportRequiresUidAndPasswordAndNeverOverwrites()
    {
        var pair = Ed25519KeyGenerator.Generate();
        MatchedKeyRecord record;
        using (var protector = PasswordSeedProtector.Open(_directory, "test password"))
        {
            record = new VaultStore(_directory, protector).SaveMatch(pair.PrivateSeed, pair.PublicKey, 1700000000,
                OpenPgpV4Packet.ComputeFingerprint(pair.PublicKey, 1700000000), "AB", "test");
        }
        CryptographicOperations.ZeroMemory(pair.PrivateSeed);
        await using var session = new DesktopSession(new NoGpu(), _directory);
        session.Unlock("test password");
        string secret = Path.Combine(_directory, "secret.asc"), pub = Path.Combine(_directory, "public.asc");
        Assert.Throws<ArgumentException>(() => session.Export(record, "", "", "export password", secret, pub));
        Assert.Throws<ArgumentException>(() => session.Export(record, "User", "", "", secret, pub));
        Assert.False(File.Exists(secret));
        File.WriteAllText(pub, "keep public");
        Assert.Throws<IOException>(() => session.Export(record, "User", "", "export password", secret, pub));
        Assert.False(File.Exists(secret));
        Assert.Equal("keep public", File.ReadAllText(pub));
        File.Delete(pub);
        session.Export(record, "User", "user@example.org", "export password", secret, pub);
        Assert.Contains("PGP PRIVATE KEY BLOCK", File.ReadAllText(secret));
        Assert.Contains("PGP PUBLIC KEY BLOCK", File.ReadAllText(pub));
        Assert.Throws<IOException>(() => session.Export(record, "User", "", "export password", secret, pub));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
