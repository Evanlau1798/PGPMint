using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

public sealed class DpapiVaultStore
{
    public const int MaxStoredMatches = 50;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PGPMint.Ed25519.Vault.v1");

    private static readonly object VaultAccessGate = new();
    public string VaultFilePath { get; }

    public DpapiVaultStore(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PGPMint");
        VaultFilePath = Path.Combine(dir, "hits.vault.json");
    }

    public IReadOnlyList<MatchedKeyRecord> LoadMatches()
    {
        lock (VaultAccessGate)
        {
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

            byte[] seedCopy = privateSeed.ToArray();
            string protectedB64;
            try
            {
                byte[] protectedSeed = ProtectedData.Protect(seedCopy, Entropy, DataProtectionScope.CurrentUser);
                protectedB64 = Convert.ToBase64String(protectedSeed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seedCopy);
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
                ProtectedPrivateSeedBase64: protectedB64);

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
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    public bool DeleteMatch(string recordId) => DeleteMatch(recordId, out _);

    internal bool DeleteMatch(string recordId, out int remainingCount)
    {
        lock (VaultAccessGate)
        {
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
            List<MatchedKeyRecord> existing = LoadInternal();
            int count = existing.Count;
            if (count > 0)
            {
                AtomicFileStore.WriteTextAtomic(VaultFilePath, "[]");
            }
            return count;
        }
    }

    private List<MatchedKeyRecord> LoadInternal()
    {
        if (!File.Exists(VaultFilePath))
        {
            return new List<MatchedKeyRecord>();
        }

        string raw = AtomicFileStore.ReadTextWithRetry(VaultFilePath, string.Empty);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<MatchedKeyRecord>();
        }

        try
        {
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
