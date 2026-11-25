using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace PGPMint.Core.Storage;

public sealed class PasswordSeedProtector : IPrivateSeedProtector, IDisposable
{
    private static readonly byte[] HeaderContext = Encoding.UTF8.GetBytes("PGPMint.PasswordVault.v1");
    private readonly byte[] _key;
    private bool _disposed;

    private PasswordSeedProtector(byte[] key) => _key = key;

    public static PasswordSeedProtector Open(string directory, ReadOnlySpan<char> password)
    {
        if (password.IsEmpty) throw new ArgumentException("A nonempty vault password is required.", nameof(password));
        using var fileLock = VaultFileLock.Acquire(directory);
        string path = Path.Combine(directory, "vault.key.json");
        PasswordVaultHeader header;
        bool exists = File.Exists(path);
        if (exists)
        {
            PrivateFileSystem.SecureFile(path);
            header = JsonSerializer.Deserialize(File.ReadAllText(path), PasswordVaultJsonContext.Default.PasswordVaultHeader)
                ?? throw new InvalidDataException("The password vault header is invalid.");
            if (header.Version != 1 || header.Salt is not { Length: 16 } || header.Verifier is null)
                throw new InvalidDataException("Unsupported password vault header.");
        }
        else
        {
            if (File.Exists(Path.Combine(directory, "hits.vault.json")))
                throw new InvalidDataException("Existing keys have no password header. Restore the original header; DPAPI vaults require their Windows user.");
            header = new(1, RandomNumberGenerator.GetBytes(16), []);
        }

        byte[] passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
        Encoding.UTF8.GetBytes(password, passwordBytes);
        byte[] key = new byte[32];
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13).WithSalt(header.Salt)
            .WithMemoryAsKB(65536).WithIterations(3).WithParallelism(4).Build();
        try
        {
            var generator = new Argon2BytesGenerator();
            generator.Init(parameters);
            generator.GenerateBytes(passwordBytes, key);
            var protector = new PasswordSeedProtector(key);
            if (exists)
            {
                byte[] check = protector.Unprotect(header.Verifier, HeaderContext);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(check, HeaderContext))
                        throw new CryptographicException("Invalid vault password or header.");
                }
                finally { CryptographicOperations.ZeroMemory(check); }
            }
            else
            {
                header = header with { Verifier = protector.Protect(HeaderContext, HeaderContext) };
                AtomicFileStore.WriteTextAtomic(path, JsonSerializer.Serialize(header, PasswordVaultJsonContext.Default.PasswordVaultHeader));
            }
            return protector;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] result = new byte[1 + 12 + 16 + seed.Length];
        result[0] = 1;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(result.AsSpan(1, 12), seed, result.AsSpan(29), result.AsSpan(13, 16), context);
        return result;
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ciphertext.Length < 29 || ciphertext[0] != 1)
            throw new CryptographicException("Unsupported encrypted seed format.");
        byte[] plaintext = new byte[ciphertext.Length - 29];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(ciphertext.Slice(1, 12), ciphertext[29..], ciphertext.Slice(13, 16), plaintext, context);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}

internal sealed record PasswordVaultHeader(int Version, byte[] Salt, byte[] Verifier);

[JsonSerializable(typeof(PasswordVaultHeader))]
internal sealed partial class PasswordVaultJsonContext : JsonSerializerContext { }
