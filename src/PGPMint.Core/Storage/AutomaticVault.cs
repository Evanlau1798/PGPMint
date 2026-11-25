using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGPMint.Core.Storage;

public interface IVaultSecretStore
{
    string Kind { get; }
    char[]? Read(string account);
    void Create(string account, ReadOnlySpan<char> secret);
    void Delete(string account);
}

public static class AutomaticVault
{
    public const string MetadataFileName = "vault.unlock.json";
    public static bool IsAutomatic(string directory) => File.Exists(Path.Combine(directory, MetadataFileName));
    public static PasswordSeedProtector? TryOpen(VaultSession session, IVaultSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);
        string directory = session.DirectoryPath;
        string path = Path.Combine(directory, MetadataFileName);
        char[] secret;
        if (File.Exists(path))
        {
            PrivateFileSystem.SecureFile(path);
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Automatic vault metadata is too large.");
            AutomaticVaultMetadata metadata;
            try
            {
                metadata = JsonSerializer.Deserialize(File.ReadAllText(path), AutomaticVaultJsonContext.Default.AutomaticVaultMetadata)
                    ?? throw new InvalidDataException("Automatic vault metadata is empty.");
            }
            catch (JsonException ex) { throw new InvalidDataException("Automatic vault metadata is invalid.", ex); }
            if (metadata.Version != 1 || !Guid.TryParseExact(metadata.Account, "N", out _))
                throw new InvalidDataException("Unsupported automatic vault metadata.");
            if (metadata.Kind != store.Kind) throw new NotSupportedException($"This vault requires {metadata.Kind} on its original user account.");
            secret = store.Read(metadata.Account)
                ?? throw new InvalidDataException("The operating-system vault key is missing. Restore it or import an OpenPGP backup; the vault will not be overwritten.");
        }
        else
        {
            if (File.Exists(Path.Combine(directory, "vault.key.json")) || File.Exists(Path.Combine(directory, "hits.vault.json")))
                return null;
            secret = new char[64];
            byte[] random = RandomNumberGenerator.GetBytes(32);
            try { Convert.TryToHexString(random, secret, out _); }
            finally { CryptographicOperations.ZeroMemory(random); }
            string account = Guid.NewGuid().ToString("N");
            try
            {
                store.Create(account, secret);
                try
                {
                    var metadata = new AutomaticVaultMetadata(1, store.Kind, account);
                    AtomicFileStore.WriteTextAtomic(path, JsonSerializer.Serialize(metadata, AutomaticVaultJsonContext.Default.AutomaticVaultMetadata));
                }
                catch { try { store.Delete(account); } catch { } throw; }
            }
            catch { Array.Clear(secret); throw; }
        }
        try
        {
            if (secret.Length != 64 || secret.Any(value => !char.IsAsciiHexDigit(value)))
                throw new InvalidDataException("The operating-system vault key has an invalid format.");
            // Publish the lookup metadata before the header so an interrupted initialization can be retried.
            return PasswordSeedProtector.Open(directory, secret);
        }
        finally { Array.Clear(secret); }
    }
}

internal sealed record AutomaticVaultMetadata(int Version, string Kind, string Account);

[JsonSerializable(typeof(AutomaticVaultMetadata))]
internal sealed partial class AutomaticVaultJsonContext : JsonSerializerContext { }
