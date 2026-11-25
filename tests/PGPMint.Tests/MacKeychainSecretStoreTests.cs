using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class MacKeychainSecretStoreTests
{
    [Fact]
    public void LoginKeychainOutputAcceptsAbsoluteUnicodePathsAndRejectsInvalidResponses()
    {
        Assert.Equal("/Users/Test User/鑰匙圈/login.keychain-db", MacKeychainSecretStore.ParseLoginKeychainPath(
            Encoding.UTF8.GetBytes("    \"/Users/Test User/鑰匙圈/login.keychain-db\"\n")));
        foreach (string invalid in new[] { "", "relative", "\"relative.keychain\"", "\"/\"", "\"/bad\npath\"" })
            Assert.Throws<InvalidDataException>(() => MacKeychainSecretStore.ParseLoginKeychainPath(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<InvalidDataException>(() => MacKeychainSecretStore.ParseLoginKeychainPath([0xFF]));
    }

    [MacOsFact]
    public void DefaultCommandsPinTheLoginKeychainWhenTheDefaultKeychainIsDifferent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-other-keychain-" + Guid.NewGuid().ToString("N"));
        PrivateFileSystem.CreateDirectory(directory);
        string keychain = Path.Combine(directory, "other.keychain-db");
        string originalDefault = Security("default-keychain\n").Trim().Trim('"');
        string login = Security("login-keychain\n").Trim().Trim('"');
        Assert.True(Path.IsPathFullyQualified(originalDefault));
        Assert.True(Path.IsPathFullyQualified(login));
        try
        {
            Security($"create-keychain -p PGPMintIsolatedTest \"{keychain}\"\ndefault-keychain -s \"{keychain}\"\n");
            Assert.Equal(keychain, Security("default-keychain\n").Trim().Trim('"'));
            var store = new MacKeychainSecretStore();
            var method = typeof(MacKeychainSecretStore).GetMethod("Arguments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var arguments = Assert.IsType<string[]>(method.Invoke(store, [new[] { "find-generic-password", "-w" }]));
            Assert.Equal(login, arguments.Last());
            Assert.NotEqual(keychain, arguments.Last());
        }
        finally
        {
            Security($"default-keychain -s \"{originalDefault}\"\ndelete-keychain \"{keychain}\"\n");
            Directory.Delete(directory, true);
        }
    }

    [MacOsFact]
    public void RealIsolatedKeychainRoundtripDoesNotRequireAVaultPassword()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-keychain-" + Guid.NewGuid().ToString("N"));
        PrivateFileSystem.CreateDirectory(directory);
        string keychain = Path.Combine(directory, "test.keychain-db");
        string account = Guid.NewGuid().ToString("N");
        char[] secret = new string('A', 64).ToCharArray();
        try
        {
            Security($"create-keychain -p PGPMintIsolatedTest \"{keychain}\"\nunlock-keychain -p PGPMintIsolatedTest \"{keychain}\"\n");
            var store = new MacKeychainSecretStore(keychain);
            Assert.Null(store.Read(account));
            store.Create(account, secret);
            char[] read = Assert.IsType<char[]>(store.Read(account));
            try { Assert.Equal(secret, read); }
            finally { Array.Clear(read); }
            store.Delete(account);
            Assert.Null(store.Read(account));

            string vaultDirectory = Path.Combine(directory, "vault");
            byte[] seed = RandomNumberGenerator.GetBytes(32);
            byte[] encrypted;
            try
            {
                using (var session = new VaultSession(vaultDirectory))
                using (var protector = AutomaticVault.TryOpen(session, store))
                {
                    Assert.NotNull(protector);
                    encrypted = protector.Protect(seed, [1, 2, 3]);
                }
                byte[] originalHeader = File.ReadAllBytes(Path.Combine(vaultDirectory, "vault.key.json"));
                using var reopened = new VaultSession(vaultDirectory);
                using (var protector = AutomaticVault.TryOpen(reopened, store))
                {
                    Assert.NotNull(protector);
                    byte[] decrypted = protector.Unprotect(encrypted, [1, 2, 3]);
                    try { Assert.Equal(seed, decrypted); }
                    finally { CryptographicOperations.ZeroMemory(decrypted); }
                }
                using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(vaultDirectory, AutomaticVault.MetadataFileName)));
                store.Delete(metadata.RootElement.GetProperty("Account").GetString()!);
                Assert.Throws<InvalidDataException>(() => AutomaticVault.TryOpen(reopened, store));
                Assert.Equal(originalHeader, File.ReadAllBytes(Path.Combine(vaultDirectory, "vault.key.json")));
            }
            finally { CryptographicOperations.ZeroMemory(seed); }
        }
        finally
        {
            Array.Clear(secret);
            Security($"delete-keychain \"{keychain}\"\n");
            Directory.Delete(directory, true);
        }
    }

    private static string Security(string commands)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/security")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        process.StartInfo.ArgumentList.Add("-i");
        Assert.True(process.Start());
        process.StandardInput.Write(commands);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(10000));
        Assert.True(process.ExitCode == 0, error + output);
        return output;
    }
}
