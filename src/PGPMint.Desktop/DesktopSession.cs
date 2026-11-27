using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed class DesktopSession(IGpuBackend backend, string directory, IVaultSecretStore? automaticSecrets = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VaultSession? _lease;
    private PasswordSeedProtector? _protector;
    private VaultStore? _vault;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public AppStateStore StateStore { get; } = new(directory);
    public MultiGpuCoordinator? Coordinator { get; private set; }
    public IReadOnlyList<string> GpuDiagnostics => backend is PortableGpuBackend portable ? portable.Diagnostics : [];

    public bool SupportsAutomaticUnlock => automaticSecrets is not null;

    public bool TryUnlockAutomatically()
    {
        if (automaticSecrets is null) return false;
        _gate.Wait();
        try
        {
            if (Coordinator is not null) return true;
            _lease = AcquireLease();
            try
            {
                var protector = AutomaticVault.TryOpen(_lease, automaticSecrets);
                if (protector is null) { _lease.Dispose(); _lease = null; return false; }
                CompleteUnlock(protector);
                return true;
            }
            catch { AbortUnlock(); throw; }
        }
        finally { _gate.Release(); }
    }

    public void Unlock(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.IsWhiteSpace()) throw new ArgumentException("A nonempty password is required.");
        _gate.Wait();
        try
        {
            if (Coordinator is not null) throw new InvalidOperationException("The vault is already unlocked.");
            _lease = AcquireLease();
            try
            {
                CompleteUnlock(PasswordSeedProtector.Open(DirectoryPath, password));
            }
            catch { AbortUnlock(); throw; }
        }
        finally { _gate.Release(); }
    }

    private VaultSession AcquireLease()
    {
        if (DirectoryPath == Path.GetFullPath(AppDataPaths.PasswordVaultDirectory)) AppDataPaths.PreparePasswordVaultDirectory();
        return new VaultSession(DirectoryPath);
    }

    private void CompleteUnlock(PasswordSeedProtector protector)
    {
        _protector = protector;
        _vault = new VaultStore(DirectoryPath, protector);
        Coordinator = new MultiGpuCoordinator(backend, StateStore, _vault);
        Coordinator.Initialize();
    }

    private void AbortUnlock()
    {
        Coordinator?.Dispose();
        Coordinator = null;
        _vault = null;
        _protector?.Dispose();
        _protector = null;
        _lease?.Dispose();
        _lease = null;
    }

    public IReadOnlyList<MatchedKeyRecord> Matches() =>
        (_vault ?? throw new InvalidOperationException("The vault is locked.")).LoadMatches();

    public void Export(
        MatchedKeyRecord record,
        string uid,
        string email,
        string password,
        string privatePath,
        string publicPath,
        bool includeEncryptionSubkey = true,
        DateTimeOffset? expiresAtUtc = null)
    {
        if ((string.IsNullOrWhiteSpace(uid) && string.IsNullOrWhiteSpace(email)) || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A UID and nonempty export passphrase are required.");
        _gate.Wait();
        try
        {
            var vault = _vault ?? throw new InvalidOperationException("The vault is locked.");
            record = vault.LoadMatches().Single(r => r.Id == record.Id);
            bool createdPrivate = false, createdPublic = false;
            try
            {
                using var secret = PrivateFileSystem.CreateNew(privatePath);
                createdPrivate = true;
                using var pub = PrivateFileSystem.CreateNew(publicPath);
                createdPublic = true;
                byte[] seed = vault.DecryptPrivateSeed(record);
                try
                {
                    var exported = OpenPgpArmorExporter.ExportKeyPair(new(
                        seed,
                        Convert.FromHexString(record.PublicKeyHex),
                        record.CreationTimestamp,
                        uid,
                        email,
                        password,
                        IncludeEncryptionSubkey: includeEncryptionSubkey,
                        ExpiresAtUtc: expiresAtUtc));
                    using var pubWriter = new StreamWriter(pub, new UTF8Encoding(false), leaveOpen: true);
                    pubWriter.Write(exported.PublicKeyArmor);
                    pubWriter.Flush();
                    pub.Flush(flushToDisk: true);
                    using var writer = new StreamWriter(secret, new UTF8Encoding(false), leaveOpen: true);
                    writer.Write(exported.PrivateKeyArmor);
                    writer.Flush();
                    secret.Flush(flushToDisk: true);
                }
                finally { CryptographicOperations.ZeroMemory(seed); }
            }
            catch
            {
                if (createdPrivate) File.Delete(privatePath);
                if (createdPublic) File.Delete(publicPath);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Coordinator is not null)
            {
                await Coordinator.StopAsync();
                Coordinator.Dispose();
                Coordinator = null;
            }
            _vault = null;
            _protector?.Dispose();
            _protector = null;
            _lease?.Dispose();
            _lease = null;
        }
        finally { _gate.Release(); }
    }
}
