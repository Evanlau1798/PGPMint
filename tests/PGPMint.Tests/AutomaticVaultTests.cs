using System.Security.Cryptography;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class AutomaticVaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PGPMint-auto-" + Guid.NewGuid().ToString("N"));
    private readonly MemorySecretStore _store = new();

    [Fact]
    public void NewVaultCreatesOneOsSecretAndReopensWithoutAHumanPassword()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(32), context = [1, 2, 3];
        byte[] encrypted;
        using (var session = new VaultSession(_directory))
        using (var protector = AutomaticVault.TryOpen(session, _store))
        {
            Assert.NotNull(protector);
            encrypted = protector.Protect(seed, context);
        }
        Assert.True(AutomaticVault.IsAutomatic(_directory));
        Assert.Single(_store.Items);
        Assert.Equal(1, _store.Created);
        using var reopenedSession = new VaultSession(_directory);
        using var reopened = AutomaticVault.TryOpen(reopenedSession, _store);
        Assert.NotNull(reopened);
        Assert.Equal(seed, reopened.Unprotect(encrypted, context));
        Assert.Equal(1, _store.Created);
        Assert.All(_store.LastRead!, value => Assert.Equal('\0', value));
        Assert.DoesNotContain(new string(_store.Items.Single().Value), File.ReadAllText(Path.Combine(_directory, "vault.key.json")));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public void ExistingPasswordVaultIsNeverSilentlyConverted()
    {
        using (PasswordSeedProtector.Open(_directory, "existing password")) { }
        byte[] original = File.ReadAllBytes(Path.Combine(_directory, "vault.key.json"));
        using var session = new VaultSession(_directory);
        Assert.Null(AutomaticVault.TryOpen(session, _store));
        Assert.Empty(_store.Items);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_directory, "vault.key.json")));
        Assert.False(AutomaticVault.IsAutomatic(_directory));
    }

    [Fact]
    public void MissingOsSecretNeverCreatesAReplacementForAnExistingVault()
    {
        using (var session = new VaultSession(_directory))
        using (var created = AutomaticVault.TryOpen(session, _store)) { Assert.NotNull(created); }
        byte[] original = File.ReadAllBytes(Path.Combine(_directory, "vault.key.json"));
        _store.Items.Clear();
        using var reopened = new VaultSession(_directory);
        Assert.Throws<InvalidDataException>(() => AutomaticVault.TryOpen(reopened, _store));
        Assert.Equal(1, _store.Created);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_directory, "vault.key.json")));
    }

    [Fact]
    public void MetadataWriteFailureRemovesTheNewUnreferencedOsSecret()
    {
        using var session = new VaultSession(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, AutomaticVault.MetadataFileName));
        AssertIoFailure(() => AutomaticVault.TryOpen(session, _store));
        Assert.Empty(_store.Items);
        Assert.Equal(1, _store.Deleted);
        Assert.False(File.Exists(Path.Combine(_directory, "vault.key.json")));
    }

    [Fact]
    public void HeaderWriteFailureKeepsTheReferencedOsSecretForRecovery()
    {
        using var session = new VaultSession(_directory);
        string header = Path.Combine(_directory, "vault.key.json");
        Directory.CreateDirectory(header);
        AssertIoFailure(() => AutomaticVault.TryOpen(session, _store));
        Assert.True(AutomaticVault.IsAutomatic(_directory));
        Assert.Single(_store.Items);
        Assert.Equal(0, _store.Deleted);
        Directory.Delete(header);
        using var recovered = AutomaticVault.TryOpen(session, _store);
        Assert.NotNull(recovered);
        Assert.True(File.Exists(header));
        Assert.Equal(1, _store.Created);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Kind\":\"test-keychain\",\"Account\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"Version\":1,\"Kind\":\"test-keychain\",\"Account\":\"not-an-account\"}")]
    public void InvalidMetadataNeverCreatesOrOverwritesAKey(string metadata)
    {
        using var session = new VaultSession(_directory);
        string path = Path.Combine(_directory, AutomaticVault.MetadataFileName);
        File.WriteAllText(path, metadata);
        Assert.Throws<InvalidDataException>(() => AutomaticVault.TryOpen(session, _store));
        Assert.Equal(metadata, File.ReadAllText(path));
        Assert.Empty(_store.Items);
        Assert.Equal(0, _store.Created);
        Assert.False(File.Exists(Path.Combine(_directory, "vault.key.json")));
    }

    [Fact]
    public void WrongProviderAndInvalidOsSecretCannotReplaceAnExistingHeader()
    {
        using var session = new VaultSession(_directory);
        using (var created = AutomaticVault.TryOpen(session, _store)) { Assert.NotNull(created); }
        string path = Path.Combine(_directory, AutomaticVault.MetadataFileName);
        string originalMetadata = File.ReadAllText(path);
        byte[] originalHeader = File.ReadAllBytes(Path.Combine(_directory, "vault.key.json"));
        File.WriteAllText(path, originalMetadata.Replace("test-keychain", "other-keychain"));
        Assert.Throws<NotSupportedException>(() => AutomaticVault.TryOpen(session, _store));
        File.WriteAllText(path, originalMetadata);
        Array.Fill(_store.Items.Single().Value, '!');
        Assert.Throws<InvalidDataException>(() => AutomaticVault.TryOpen(session, _store));
        Assert.All(_store.LastRead!, value => Assert.Equal('\0', value));
        Assert.Equal(originalHeader, File.ReadAllBytes(Path.Combine(_directory, "vault.key.json")));
        Assert.Equal(1, _store.Created);
    }

    [Fact]
    public void DisposedSessionCannotOpenOrCreateAnAutomaticVault()
    {
        var session = new VaultSession(_directory);
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => AutomaticVault.TryOpen(session, _store));
        Assert.Equal(0, _store.Created);
        Assert.False(AutomaticVault.IsAutomatic(_directory));
    }

    public void Dispose()
    {
        foreach (char[] secret in _store.Items.Values) Array.Clear(secret);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static void AssertIoFailure(Action action) =>
        Assert.True(Record.Exception(action) is IOException or UnauthorizedAccessException);

    private sealed class MemorySecretStore : IVaultSecretStore
    {
        public string Kind => "test-keychain";
        internal Dictionary<string, char[]> Items { get; } = [];
        internal int Created, Deleted;
        internal char[]? LastRead;
        public char[]? Read(string account) => Items.TryGetValue(account, out var value) ? LastRead = (char[])value.Clone() : null;
        public void Create(string account, ReadOnlySpan<char> secret) { Items.Add(account, secret.ToArray()); Created++; }
        public void Delete(string account) { Array.Clear(Items[account]); Items.Remove(account); Deleted++; }
    }
}
