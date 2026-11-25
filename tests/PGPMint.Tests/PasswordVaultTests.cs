using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class PasswordVaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PGPMint-password-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PasswordProtectionReopensWithoutPlaintextAndBindsRecordIdentity()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32);
        byte[] context = Encoding.UTF8.GetBytes("record-id:fingerprint:timestamp:public-key");
        byte[] ciphertext;
        using (var protector = PasswordSeedProtector.Open(_directory, "correct horse battery staple"))
        {
            ciphertext = protector.Protect(seed, context);
            Assert.False(ciphertext.AsSpan().IndexOf(seed) >= 0);
        }
        using var reopened = PasswordSeedProtector.Open(_directory, "correct horse battery staple");
        Assert.Equal(seed, reopened.Unprotect(ciphertext, context));
        Assert.ThrowsAny<CryptographicException>(() => reopened.Unprotect(ciphertext, new byte[] { 1 }));
        ciphertext[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => reopened.Unprotect(ciphertext, context));
        Assert.ThrowsAny<CryptographicException>(() => PasswordSeedProtector.Open(_directory, "incorrect password"));
        Assert.DoesNotContain("correct horse battery staple", File.ReadAllText(Path.Combine(_directory, "vault.key.json")));
    }

    [Fact]
    public void EmptyPasswordAndLostHeaderNeverInitializeOrOverwriteExistingKeys()
    {
        Assert.Throws<ArgumentException>(() => PasswordSeedProtector.Open(_directory, ""));
        PrivateFileSystem.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "hits.vault.json");
        File.WriteAllText(path, "[{\"existing-key\":true}]");
        Assert.Throws<InvalidDataException>(() => PasswordSeedProtector.Open(_directory, "new password"));
        Assert.Equal("[{\"existing-key\":true}]", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
