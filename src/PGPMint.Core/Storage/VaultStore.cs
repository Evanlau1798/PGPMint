using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

public class VaultStore
{
    public const int MaxStoredMatches = 50;

    private readonly IPrivateSeedProtector _protector;

    private static readonly object VaultAccessGate = new();
    public string VaultFilePath { get; }

    public VaultStore(string? dataDirectory, IPrivateSeedProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        string dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PGPMint");
        VaultFilePath = Path.Combine(dir, "hits.vault.json");
    }

    public IReadOnlyList<MatchedKeyRecord> LoadMatches()
    {
        lock (VaultAccessGate)
        {
            using var fileLock = VaultFileLock.Acquire(Path.GetDirectoryName(VaultFilePath)!);
            return LoadInternal();
        }
    }

    public MatchedKeyRecord SaveMatch(
        ReadOnlySpan<byte> privateSeed,
        ReadOnlySpan<byte> publicKey,
        uint creationTimestamp,
        string fingerprint,
        string matchedPattern,
        string deviceName) =>
        SaveMatch(privateSeed, publicKey, creationTimestamp, fingerprint, matchedPattern, deviceName, out _);

    public MatchedKeyRecord SaveMatch(
        ReadOnlySpan<byte> privateSeed,
        ReadOnlySpan<byte> publicKey,
        uint creationTimestamp,
        string fingerprint,
        string matchedPattern,
        string deviceName,
        out int totalStoredCount) =>
        SaveMatch(privateSeed, publicKey, creationTimestamp, fingerprint, matchedPattern, deviceName, out totalStoredCount, out _);

    internal MatchedKeyRecord SaveMatch(
        ReadOnlySpan<byte> privateSeed,
        ReadOnlySpan<byte> publicKey,
        uint creationTimestamp,
        string fingerprint,
        string matchedPattern,
        string deviceName,
        out int totalStoredCount,
        out bool wasAdded)
    {
        if (privateSeed.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("Private seed must be 32 bytes.", nameof(privateSeed));
        }

        if (publicKey.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("Public key must be 32 bytes.", nameof(publicKey));
        }

        string recomputedFp = OpenPgpV4Packet.ComputeFingerprint(publicKey, creationTimestamp);
        if (!string.Equals(recomputedFp, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fingerprint verification failed before vault persistence.");
        }

        lock (VaultAccessGate)
        {
            using var fileLock = VaultFileLock.Acquire(Path.GetDirectoryName(VaultFilePath)!);
            List<MatchedKeyRecord> existing = LoadInternal();
            MatchedKeyRecord? duplicate = existing.FirstOrDefault(r =>
                string.Equals(r.Fingerprint, recomputedFp, StringComparison.OrdinalIgnoreCase) &&
                r.CreationTimestamp == creationTimestamp);

            if (duplicate is not null)
            {
                totalStoredCount = existing.Count;
                wasAdded = false;
                return duplicate;
            }

            if (existing.Count >= MaxStoredMatches)
            {
                throw new VaultFullException(MaxStoredMatches);
            }

            var record = new MatchedKeyRecord(
                Id: Guid.NewGuid().ToString("N"),
                Fingerprint: recomputedFp,
                KeyId: recomputedFp[^16..],
                CreationTimestamp: creationTimestamp,
                MatchedPattern: matchedPattern,
                DeviceName: deviceName,
                FoundAtUtc: DateTimeOffset.UtcNow,
                PublicKeyHex: Convert.ToHexString(publicKey),
                ProtectedPrivateSeedBase64: "");
            byte[] seedCopy = privateSeed.ToArray();
            string protectedB64;
            try
            {
                byte[] protectedSeed = _protector.Protect(seedCopy, SeedContext(record));
                protectedB64 = Convert.ToBase64String(protectedSeed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seedCopy);
            }

            record = record with { ProtectedPrivateSeedBase64 = protectedB64 };

            existing.Insert(0, record);
            string json = JsonSerializer.Serialize(existing, StorageJsonContext.Default.ListMatchedKeyRecord);
            AtomicFileStore.WriteTextAtomic(VaultFilePath, json);
            totalStoredCount = existing.Count;
            wasAdded = true;
            return record;
        }
    }

    public byte[] DecryptPrivateSeed(MatchedKeyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        byte[] protectedBytes = Convert.FromBase64String(record.ProtectedPrivateSeedBase64);
        return _protector.Unprotect(protectedBytes, SeedContext(record));
    }

    public bool DeleteMatch(string recordId) => DeleteMatch(recordId, out _);

    internal bool DeleteMatch(string recordId, out int remainingCount)
    {
        lock (VaultAccessGate)
        {
            using var fileLock = VaultFileLock.Acquire(Path.GetDirectoryName(VaultFilePath)!);
            List<MatchedKeyRecord> existing = LoadInternal();
            int removed = existing.RemoveAll(r => string.Equals(r.Id, recordId, StringComparison.Ordinal));
            remainingCount = existing.Count;
            if (removed > 0)
            {
                string json = JsonSerializer.Serialize(existing, StorageJsonContext.Default.ListMatchedKeyRecord);
                AtomicFileStore.WriteTextAtomic(VaultFilePath, json);
                return true;
            }

            return false;
        }
    }

    public int ClearAllMatches()
    {
        lock (VaultAccessGate)
        {
            using var fileLock = VaultFileLock.Acquire(Path.GetDirectoryName(VaultFilePath)!);
            List<MatchedKeyRecord> existing = LoadInternal();
            int count = existing.Count;
            if (count > 0)
            {
                AtomicFileStore.WriteTextAtomic(VaultFilePath, "[]");
            }
            return count;
        }
    }

    private static byte[] SeedContext(MatchedKeyRecord record) => Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"{record.Id}/{record.Fingerprint}/{record.KeyId}/{record.CreationTimestamp}/{record.PublicKeyHex}"));

    private List<MatchedKeyRecord> LoadInternal()
    {
        if (_protector is DpapiSeedProtector && File.Exists(Path.Combine(Path.GetDirectoryName(VaultFilePath)!, "vault.key.json")))
            throw new InvalidDataException("This is a password vault. Open it with the CLI or portable desktop; it cannot be used as a DPAPI vault.");
        if (!File.Exists(VaultFilePath))
        {
            return new List<MatchedKeyRecord>();
        }

        PrivateFileSystem.SecureFile(VaultFilePath);
        string raw = AtomicFileStore.ReadTextWithRetry(VaultFilePath, string.Empty);

        try
        {
            if (string.IsNullOrWhiteSpace(raw)) throw new JsonException("An existing vault is empty or truncated.");
            List<MatchedKeyRecord>? parsed = JsonSerializer.Deserialize(raw, StorageJsonContext.Default.ListMatchedKeyRecord);
            if (parsed is null)
            {
                throw new JsonException("Vault JSON deserialized to null.");
            }
            return parsed;
        }
        catch (JsonException ex)
        {
            // Never silently return empty list and risk overwriting stored private keys.
            string backupPath = $"{VaultFilePath}.corrupt.{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            try
            {
                if (!File.Exists(backupPath))
                {
                    File.Copy(VaultFilePath, backupPath, overwrite: false);
                }
            }
            catch
            {
                // Preserve original error even if backup copy fails
            }

            throw new InvalidDataException(
                $"Vault file is corrupted and was backed up to '{backupPath}'. Refusing to overwrite stored keys.",
                ex);
        }
    }
}
