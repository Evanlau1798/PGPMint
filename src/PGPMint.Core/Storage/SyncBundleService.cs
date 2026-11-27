using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

public sealed record SyncBundleInfo(
    int Version,
    DateTimeOffset CreatedAtUtc,
    string PatternMask,
    string TargetInput,
    ulong TotalCheckedCandidates,
    bool HasEncryptedKeys,
    int KeyCount);

public sealed record SyncExportResult(
    string FilePath,
    string PatternMask,
    ulong TotalCheckedCandidates,
    int ExportedKeysCount,
    bool HasEncryptedKeys);

public sealed record SyncImportResult(
    string PatternMask,
    ulong AddedCandidates,
    ulong NewTotalCheckedCandidates,
    int ImportedKeysCount,
    int SkippedDuplicateKeysCount,
    bool PatternMismatchSkippedProgress);

internal sealed record SyncBundleEnvelope(
    int Version,
    DateTimeOffset CreatedAtUtc,
    string PatternMask,
    string TargetInput,
    Dictionary<string, ulong> ProgressLedger,
    ulong TotalCheckedCandidates,
    bool HasEncryptedKeys,
    int KeyCount,
    string? KdfSaltBase64,
    string? EncryptedKeysPayloadBase64);

internal sealed record SyncBundleKeyEntry(
    string Fingerprint,
    string KeyId,
    uint CreationTimestamp,
    string MatchedPattern,
    string DeviceName,
    DateTimeOffset FoundAtUtc,
    string PublicKeyHex,
    string PrivateSeedBase64);

public static class SyncBundleService
{
    public const int CurrentVersion = 1;
    private const string BundleAadPrefix = "PGPMint.SyncBundle.v1|";

    internal static ulong SumLedger(IReadOnlyDictionary<string, ulong> ledger)
    {
        ulong total = 0;
        foreach (ulong count in ledger.Values)
        {
            total = SaturatingAdd(total, count);
        }

        return total;
    }

    internal static ulong SumLedgerExcluding(IReadOnlyDictionary<string, ulong> ledger, string excludedSessionId)
    {
        ulong total = 0;
        foreach ((string key, ulong count) in ledger)
        {
            if (string.Equals(key, excludedSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            total = SaturatingAdd(total, count);
        }

        return total;
    }

    internal static void EnsureLegacyLedgerSeeded(AppPersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.ProgressLedger ??= new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (state.TotalCheckedCandidates > 0 && state.ProgressLedger.Count == 0)
        {
            state.ProgressLedger["legacy"] = state.TotalCheckedCandidates;
        }
    }

    public static SyncBundleInfo Inspect(string bundlePath)
    {
        SyncBundleEnvelope env = ReadEnvelope(bundlePath);
        ulong total = Math.Max(env.TotalCheckedCandidates, SumLedger(env.ProgressLedger));
        return new SyncBundleInfo(
            env.Version,
            env.CreatedAtUtc,
            env.PatternMask,
            env.TargetInput,
            total,
            env.HasEncryptedKeys,
            env.KeyCount);
    }

    public static SyncExportResult Export(
        AppStateStore stateStore,
        VaultStore? vaultStore,
        string outputPath,
        bool includeKeys,
        ReadOnlySpan<char> passphrase)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("An output path is required.", nameof(outputPath));
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outDir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        AppPersistedState state = stateStore.Load();
        var ledgerCopy = new Dictionary<string, ulong>(state.ProgressLedger, StringComparer.Ordinal);
        if (state.TotalCheckedCandidates > 0 && ledgerCopy.Count == 0)
        {
            ledgerCopy["legacy"] = state.TotalCheckedCandidates;
        }

        ulong totalChecked = Math.Max(state.TotalCheckedCandidates, SumLedger(ledgerCopy));
        string patternMask = state.LastPatternMask ?? string.Empty;
        string targetInput = state.TargetInput ?? string.Empty;

        bool hasEncryptedKeys = false;
        int exportedKeysCount = 0;
        string? saltB64 = null;
        string? encryptedPayloadB64 = null;

        if (includeKeys && vaultStore is not null)
        {
            IReadOnlyList<MatchedKeyRecord> matches = vaultStore.LoadMatches();
            if (matches.Count > 0)
            {
                if (passphrase.IsEmpty)
                {
                    throw new ArgumentException("A nonempty bundle passphrase is required when exporting encrypted keys.");
                }

                var entries = new List<SyncBundleKeyEntry>(matches.Count);
                foreach (MatchedKeyRecord rec in matches)
                {
                    byte[] seed = vaultStore.DecryptPrivateSeed(rec);
                    try
                    {
                        entries.Add(new SyncBundleKeyEntry(
                            rec.Fingerprint,
                            rec.KeyId,
                            rec.CreationTimestamp,
                            rec.MatchedPattern,
                            rec.DeviceName,
                            rec.FoundAtUtc,
                            rec.PublicKeyHex,
                            Convert.ToBase64String(seed)));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(seed);
                    }
                }

                byte[] plainJson = JsonSerializer.SerializeToUtf8Bytes(entries, StorageJsonContext.Default.ListSyncBundleKeyEntry);
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                byte[] key = DeriveBundleKey(passphrase, salt);
                byte[] aad = Encoding.UTF8.GetBytes(BundleAadPrefix + patternMask);
                try
                {
                    byte[] cipher = EncryptAesGcm(key, plainJson, aad);
                    saltB64 = Convert.ToBase64String(salt);
                    encryptedPayloadB64 = Convert.ToBase64String(cipher);
                    hasEncryptedKeys = true;
                    exportedKeysCount = entries.Count;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainJson);
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }

        var envelope = new SyncBundleEnvelope(
            CurrentVersion,
            DateTimeOffset.UtcNow,
            patternMask,
            targetInput,
            ledgerCopy,
            totalChecked,
            hasEncryptedKeys,
            exportedKeysCount,
            saltB64,
            encryptedPayloadB64);

        string json = JsonSerializer.Serialize(envelope, StorageJsonContext.Default.SyncBundleEnvelope);
        AtomicFileStore.WriteTextAtomic(fullOutputPath, json);
        if (hasEncryptedKeys)
        {
            PrivateFileSystem.SecureFile(fullOutputPath);
        }

        return new SyncExportResult(fullOutputPath, patternMask, totalChecked, exportedKeysCount, hasEncryptedKeys);
    }

    public static SyncImportResult Import(
        AppStateStore stateStore,
        VaultStore? vaultStore,
        string bundlePath,
        ReadOnlySpan<char> passphrase) =>
        Import(stateStore, vaultStore, bundlePath, passphrase, out _);

    public static SyncImportResult Import(
        AppStateStore stateStore,
        VaultStore? vaultStore,
        string bundlePath,
        ReadOnlySpan<char> passphrase,
        out List<MatchedKeyRecord> importedRecords)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        SyncBundleEnvelope env = ReadEnvelope(bundlePath);
        importedRecords = new List<MatchedKeyRecord>();

        int importedKeysCount = 0;
        int skippedDuplicateKeys = 0;

        if (env.HasEncryptedKeys && env.KeyCount > 0)
        {
            if (vaultStore is null)
            {
                throw new InvalidOperationException("A vault is required to import encrypted keys.");
            }

            if (passphrase.IsEmpty)
            {
                throw new CryptographicException("A passphrase is required to decrypt keys in this sync bundle.");
            }

            if (string.IsNullOrWhiteSpace(env.KdfSaltBase64) || string.IsNullOrWhiteSpace(env.EncryptedKeysPayloadBase64))
            {
                throw new InvalidDataException("Sync bundle key payload is missing or incomplete.");
            }

            byte[] salt = Convert.FromBase64String(env.KdfSaltBase64);
            byte[] cipher = Convert.FromBase64String(env.EncryptedKeysPayloadBase64);
            byte[] key = DeriveBundleKey(passphrase, salt);
            byte[] aad = Encoding.UTF8.GetBytes(BundleAadPrefix + env.PatternMask);
            byte[]? plainJson = null;

            try
            {
                plainJson = DecryptAesGcm(key, cipher, aad);
                List<SyncBundleKeyEntry>? entries = JsonSerializer.Deserialize(plainJson, StorageJsonContext.Default.ListSyncBundleKeyEntry);
                if (entries is null)
                {
                    throw new InvalidDataException("Sync bundle key list is invalid.");
                }

                foreach (SyncBundleKeyEntry entry in entries)
                {
                    byte[] seed = Convert.FromBase64String(entry.PrivateSeedBase64);
                    byte[] pubKey = Convert.FromHexString(entry.PublicKeyHex);
                    try
                    {
                        if (seed.Length != Ed25519KeyGenerator.KeyByteLength ||
                            pubKey.Length != Ed25519KeyGenerator.KeyByteLength)
                        {
                            throw new InvalidDataException("Invalid Ed25519 key material in sync bundle.");
                        }

                        byte[] derivedPub = Ed25519KeyGenerator.DerivePublicKey(seed);
                        if (!derivedPub.AsSpan().SequenceEqual(pubKey))
                        {
                            throw new InvalidDataException("Public key does not match private seed in sync bundle.");
                        }

                        string fp = OpenPgpV4Packet.ComputeFingerprint(pubKey, entry.CreationTimestamp);
                        if (!string.Equals(fp, entry.Fingerprint, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Fingerprint mismatch in sync bundle.");
                        }

                        MatchedKeyRecord saved = vaultStore.SaveMatch(
                            seed,
                            pubKey,
                            entry.CreationTimestamp,
                            fp,
                            entry.MatchedPattern,
                            entry.DeviceName,
                            out _,
                            out bool wasAdded);

                        if (wasAdded)
                        {
                            importedKeysCount++;
                            importedRecords.Add(saved);
                        }
                        else
                        {
                            skippedDuplicateKeys++;
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(seed);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (plainJson is not null)
                {
                    CryptographicOperations.ZeroMemory(plainJson);
                }
            }
        }

        ulong addedCandidates = 0;
        ulong newTotal = 0;
        bool patternMismatch = false;
        string finalMask = env.PatternMask;

        stateStore.Update(s =>
        {
            EnsureLegacyLedgerSeeded(s);
            ulong beforeTotal = Math.Max(s.TotalCheckedCandidates, SumLedger(s.ProgressLedger));

            bool compatible = string.IsNullOrEmpty(s.LastPatternMask) ||
                              string.IsNullOrEmpty(env.PatternMask) ||
                              string.Equals(s.LastPatternMask, env.PatternMask, StringComparison.OrdinalIgnoreCase);

            if (!compatible)
            {
                patternMismatch = true;
                newTotal = beforeTotal;
                finalMask = s.LastPatternMask;
                return;
            }

            if (string.IsNullOrEmpty(s.LastPatternMask) && !string.IsNullOrEmpty(env.PatternMask))
            {
                s.LastPatternMask = env.PatternMask;
                if (string.IsNullOrWhiteSpace(s.TargetInput) && !string.IsNullOrWhiteSpace(env.TargetInput))
                {
                    s.TargetInput = env.TargetInput;
                }
            }

            Dictionary<string, ulong> incoming = env.ProgressLedger.Count > 0
                ? env.ProgressLedger
                : (env.TotalCheckedCandidates > 0
                    ? new Dictionary<string, ulong>(StringComparer.Ordinal) { ["legacy"] = env.TotalCheckedCandidates }
                    : new Dictionary<string, ulong>(StringComparer.Ordinal));

            foreach ((string sessionId, ulong count) in incoming)
            {
                ulong existing = s.ProgressLedger.GetValueOrDefault(sessionId);
                if (count > existing)
                {
                    s.ProgressLedger[sessionId] = count;
                }
            }

            ulong ledgerSum = SumLedger(s.ProgressLedger);
            newTotal = Math.Max(beforeTotal, ledgerSum);
            s.TotalCheckedCandidates = newTotal;
            addedCandidates = newTotal >= beforeTotal ? newTotal - beforeTotal : 0UL;
            finalMask = s.LastPatternMask ?? string.Empty;
        });

        return new SyncImportResult(
            finalMask,
            addedCandidates,
            newTotal,
            importedKeysCount,
            skippedDuplicateKeys,
            patternMismatch);
    }

    private static SyncBundleEnvelope ReadEnvelope(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            throw new ArgumentException("A sync bundle path is required.", nameof(bundlePath));
        }

        string fullPath = Path.GetFullPath(bundlePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Sync bundle not found: {fullPath}", fullPath);
        }

        string raw = File.ReadAllText(fullPath, Encoding.UTF8);
        SyncBundleEnvelope? env = JsonSerializer.Deserialize(raw, StorageJsonContext.Default.SyncBundleEnvelope);
        if (env is null || env.Version != CurrentVersion)
        {
            throw new InvalidDataException("Unsupported or invalid sync bundle format.");
        }

        return env with
        {
            PatternMask = env.PatternMask ?? string.Empty,
            TargetInput = env.TargetInput ?? string.Empty,
            ProgressLedger = env.ProgressLedger is not null
                ? new Dictionary<string, ulong>(env.ProgressLedger, StringComparer.Ordinal)
                : new Dictionary<string, ulong>(StringComparer.Ordinal)
        };
    }

    private static byte[] DeriveBundleKey(ReadOnlySpan<char> passphrase, byte[] salt)
    {
        if (salt.Length != 16)
        {
            throw new InvalidDataException("Invalid KDF salt length in sync bundle.");
        }

        byte[] passBytes = new byte[Encoding.UTF8.GetByteCount(passphrase)];
        Encoding.UTF8.GetBytes(passphrase, passBytes);
        byte[] key = new byte[32];
        var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithMemoryAsKB(65536)
            .WithIterations(3)
            .WithParallelism(4)
            .Build();
        try
        {
            var gen = new Argon2BytesGenerator();
            gen.Init(parameters);
            gen.GenerateBytes(passBytes, key);
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passBytes);
        }
    }

    private static byte[] EncryptAesGcm(byte[] key, byte[] plaintext, byte[] aad)
    {
        byte[] result = new byte[1 + 12 + 16 + plaintext.Length];
        result[0] = CurrentVersion;
        RandomNumberGenerator.Fill(result.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(result.AsSpan(1, 12), plaintext, result.AsSpan(29), result.AsSpan(13, 16), aad);
        return result;
    }

    private static byte[] DecryptAesGcm(byte[] key, byte[] blob, byte[] aad)
    {
        if (blob.Length < 29 || blob[0] != CurrentVersion)
        {
            throw new CryptographicException("Unsupported encrypted sync bundle payload.");
        }

        byte[] plaintext = new byte[blob.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(blob.AsSpan(1, 12), blob.AsSpan(29), blob.AsSpan(13, 16), plaintext, aad);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static ulong SaturatingAdd(ulong a, ulong b)
    {
        ulong sum = unchecked(a + b);
        return sum < a ? ulong.MaxValue : sum;
    }
}
