using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Host;

public sealed class HostSession(
    IGpuBackend backend,
    string directory,
    IVaultSecretStore? automaticSecrets = null,
    CoordinatorOptions? coordinatorOptions = null) : IAsyncDisposable
{
    private static readonly TimeSpan TelemetryThrottle = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AppStateStore _stateStore = new(directory);
    private VaultSession? _lease;
    private PasswordSeedProtector? _protector;
    private VaultStore? _vault;
    private MultiGpuCoordinator? _coordinator;
    private string _vaultMode = "none";
    private long _lastTelemetryEmitTick;
    private CoordinatorState _lastTelemetryState = CoordinatorState.Idle;
    private bool _disposed;

    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public bool SupportsAutomaticUnlock => automaticSecrets is not null;
    public MultiGpuCoordinator? Coordinator => _coordinator;
    public IReadOnlyList<string> GpuDiagnostics => backend is PortableGpuBackend portable ? portable.Diagnostics : [];

    public event Action<HostTelemetryDto>? TelemetryEmitted;
    public event Action<HostMatchDto>? MatchFoundEmitted;

    public HostStatusSnapshot GetStatus() => BuildStatusSnapshot();

    public UnlockAutomaticResult UnlockAutomatic()
    {
        if (automaticSecrets is null)
            return new UnlockAutomaticResult(false, BuildStatusSnapshot());

        _gate.Wait();
        try
        {
            if (_coordinator is not null)
                return new UnlockAutomaticResult(true, BuildStatusSnapshot());

            _lease = AcquireLease();
            try
            {
                PasswordSeedProtector? protector = AutomaticVault.TryOpen(_lease, automaticSecrets);
                if (protector is null)
                {
                    _lease.Dispose();
                    _lease = null;
                    return new UnlockAutomaticResult(false, BuildStatusSnapshot());
                }

                CompleteUnlock(protector, "automatic");
                return new UnlockAutomaticResult(true, BuildStatusSnapshot());
            }
            catch
            {
                AbortUnlock();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public HostStatusSnapshot UnlockPassword(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.IsWhiteSpace())
            throw new ArgumentException("A nonempty password is required.");

        _gate.Wait();
        try
        {
            if (_coordinator is not null)
                throw new InvalidOperationException("The vault is already unlocked.");

            _lease = AcquireLease();
            try
            {
                PasswordSeedProtector protector = PasswordSeedProtector.Open(DirectoryPath, password);
                CompleteUnlock(protector, "password");
                return BuildStatusSnapshot();
            }
            catch
            {
                AbortUnlock();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HostStatusSnapshot> LockAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseUnlockedResourcesAsync().ConfigureAwait(false);
            return BuildStatusSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public HostStatusSnapshot ApplySettings(ApplySettingsParams input)
    {
        MultiGpuCoordinator coordinator = RequireCoordinator();
        KeyIdPattern? parsedTarget = null;

        if (input.TargetInput is not null)
        {
            parsedTarget = KeyIdPattern.Parse(input.TargetInput);
            AppPersistedState current = _stateStore.Load();
            bool targetChanged = !string.Equals(current.TargetInput, parsedTarget.Suffix, StringComparison.OrdinalIgnoreCase);
            if (targetChanged && coordinator.CurrentState is not (CoordinatorState.Idle or CoordinatorState.Faulted))
                throw new InvalidOperationException("Stop search to edit target.");
        }

        if (parsedTarget is not null && coordinator.CurrentState is CoordinatorState.Idle or CoordinatorState.Faulted)
            coordinator.SetTargetPattern(parsedTarget);

        if (input.AutoPauseOnHit is bool autoPause)
            coordinator.SetAutoPauseOnHit(autoPause);

        if (input.MaxGpuTemperatureCelsius is int maxTemp)
            coordinator.SetMaxGpuTemperatureCelsius(maxTemp > 0 ? maxTemp : null);

        _stateStore.Update(s =>
        {
            if (parsedTarget is not null) s.TargetInput = parsedTarget.Suffix;
            if (!string.IsNullOrWhiteSpace(input.Language)) s.Language = input.Language.Trim();
            if (input.AutoPauseOnHit is bool ap) s.AutoPauseOnHit = ap;
            if (input.MinimizeToTrayOnClose is bool tray) s.MinimizeToTrayOnClose = tray;
            if (input.HasCompletedSetup is bool setup) s.HasCompletedSetup = setup;
        });

        return BuildStatusSnapshot();
    }

    public HostStatusSnapshot SetPowerTier(PowerTier tier)
    {
        RequireCoordinator().SetPowerTier(tier);
        return BuildStatusSnapshot();
    }

    public async Task<HostStatusSnapshot> SetDeviceEnabledAsync(string deviceKey, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("A device key is required.");

        await RequireCoordinator().SetDeviceEnabledAsync(deviceKey, enabled).ConfigureAwait(false);
        return BuildStatusSnapshot();
    }

    public async Task<HostStatusSnapshot> StartSearchAsync(string? targetInput)
    {
        MultiGpuCoordinator coordinator = RequireCoordinator();
        string raw = string.IsNullOrWhiteSpace(targetInput) ? _stateStore.Load().TargetInput : targetInput;
        KeyIdPattern pattern = KeyIdPattern.Parse(raw);
        _stateStore.Update(s =>
        {
            s.TargetInput = pattern.Suffix;
            s.HasCompletedSetup = true;
        });
        await coordinator.StartSearchAsync(pattern).ConfigureAwait(false);
        return BuildStatusSnapshot();
    }

    public HostStatusSnapshot Pause()
    {
        RequireCoordinator().Pause();
        return BuildStatusSnapshot();
    }

    public HostStatusSnapshot Resume()
    {
        RequireCoordinator().Resume();
        return BuildStatusSnapshot();
    }

    public async Task<HostStatusSnapshot> StopAsync()
    {
        if (_coordinator is { } coordinator)
            await coordinator.StopAsync().ConfigureAwait(false);
        return BuildStatusSnapshot();
    }

    public async Task<HostStatusSnapshot> RecalibrateAsync(string? targetInput)
    {
        MultiGpuCoordinator coordinator = RequireCoordinator();
        string raw = string.IsNullOrWhiteSpace(targetInput) ? _stateStore.Load().TargetInput : targetInput;
        KeyIdPattern pattern = KeyIdPattern.Parse(raw);
        _stateStore.Update(s =>
        {
            s.TargetInput = pattern.Suffix;
            s.HasCompletedSetup = true;
        });
        if (coordinator.InvalidateCalibrationsForRecalibration())
            await coordinator.StartSearchAsync(pattern).ConfigureAwait(false);
        return BuildStatusSnapshot();
    }

    public ExportMatchResult ExportMatch(ExportMatchParams input)
    {
        if (string.IsNullOrWhiteSpace(input.RecordId))
            throw new ArgumentException("A record ID is required.");
        if ((string.IsNullOrWhiteSpace(input.Uid) && string.IsNullOrWhiteSpace(input.Email)) || string.IsNullOrWhiteSpace(input.Passphrase))
            throw new ArgumentException("A UID name or email and a nonempty export passphrase are required.");
        if (string.IsNullOrWhiteSpace(input.PrivatePath) || string.IsNullOrWhiteSpace(input.PublicPath))
            throw new ArgumentException("Private and public output file paths are required.");

        string privateFull = Path.GetFullPath(input.PrivatePath);
        string publicFull = Path.GetFullPath(input.PublicPath);
        if (string.Equals(privateFull, publicFull, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Private and public export paths must be distinct.");

        _gate.Wait();
        try
        {
            VaultStore vault = _vault ?? throw new InvalidOperationException("The vault is locked.");
            MatchedKeyRecord record = vault.LoadMatches().FirstOrDefault(r => r.Id == input.RecordId)
                ?? throw new InvalidOperationException("The requested key record was not found.");

            bool createdPrivate = false;
            bool createdPublic = false;
            try
            {
                using FileStream secretStream = PrivateFileSystem.CreateNew(privateFull);
                createdPrivate = true;
                using FileStream publicStream = PrivateFileSystem.CreateNew(publicFull);
                createdPublic = true;

                byte[] seed = vault.DecryptPrivateSeed(record);
                try
                {
                    OpenPgpExportResult exported = OpenPgpArmorExporter.ExportKeyPair(new OpenPgpExportRequest(
                        PrivateSeed: seed,
                        PublicKey: Convert.FromHexString(record.PublicKeyHex),
                        CreationTimestamp: record.CreationTimestamp,
                        UserName: input.Uid ?? string.Empty,
                        UserEmail: input.Email,
                        Passphrase: input.Passphrase));

                    using var pubWriter = new StreamWriter(publicStream, new UTF8Encoding(false), leaveOpen: true);
                    pubWriter.Write(exported.PublicKeyArmor);
                    pubWriter.Flush();
                    publicStream.Flush(flushToDisk: true);

                    using var secWriter = new StreamWriter(secretStream, new UTF8Encoding(false), leaveOpen: true);
                    secWriter.Write(exported.PrivateKeyArmor);
                    secWriter.Flush();
                    secretStream.Flush(flushToDisk: true);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                }

                return new ExportMatchResult(privateFull, publicFull);
            }
            catch
            {
                if (createdPrivate) TryDelete(privateFull);
                if (createdPublic) TryDelete(publicFull);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public DeleteMatchResult DeleteMatch(string recordId)
    {
        if (string.IsNullOrWhiteSpace(recordId))
            throw new ArgumentException("A record ID is required.");

        bool deleted = RequireCoordinator().DeleteVaultMatch(recordId);
        return new DeleteMatchResult(deleted, BuildStatusSnapshot());
    }

    public ClearMatchesResult ClearMatches()
    {
        int removed = RequireCoordinator().ClearVaultMatches();
        return new ClearMatchesResult(removed, BuildStatusSnapshot());
    }

    public SyncInspectResultDto InspectSyncBundle(string bundlePath)
    {
        SyncBundleInfo info = SyncBundleService.Inspect(bundlePath);
        return new SyncInspectResultDto(
            info.Version,
            info.CreatedAtUtc,
            info.PatternMask,
            info.TargetInput,
            info.TotalCheckedCandidates,
            info.HasEncryptedKeys,
            info.KeyCount);
    }

    public SyncExportResultDto ExportSyncBundle(ExportSyncBundleParams input)
    {
        ArgumentNullException.ThrowIfNull(input);
        MultiGpuCoordinator coordinator = RequireCoordinator();
        char[] pass = (input.Passphrase ?? string.Empty).ToCharArray();
        try
        {
            SyncExportResult res = coordinator.ExportSyncBundle(input.OutputPath ?? string.Empty, input.IncludeKeys, pass);
            return new SyncExportResultDto(res.FilePath, res.PatternMask, res.TotalCheckedCandidates, res.ExportedKeysCount, res.HasEncryptedKeys);
        }
        finally
        {
            Array.Clear(pass);
        }
    }

    public SyncImportResultDto ImportSyncBundle(ImportSyncBundleParams input)
    {
        ArgumentNullException.ThrowIfNull(input);
        MultiGpuCoordinator coordinator = RequireCoordinator();
        char[] pass = (input.Passphrase ?? string.Empty).ToCharArray();
        try
        {
            SyncImportResult res = coordinator.ImportSyncBundle(input.BundlePath ?? string.Empty, pass);
            return new SyncImportResultDto(
                res.PatternMask,
                res.AddedCandidates,
                res.NewTotalCheckedCandidates,
                res.ImportedKeysCount,
                res.SkippedDuplicateKeysCount,
                res.PatternMismatchSkippedProgress,
                BuildStatusSnapshot());
        }
        finally
        {
            Array.Clear(pass);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await ReleaseUnlockedResourcesAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private MultiGpuCoordinator RequireCoordinator() =>
        _coordinator ?? throw new InvalidOperationException("The vault is locked.");

    private VaultSession AcquireLease()
    {
        if (string.Equals(DirectoryPath, Path.GetFullPath(AppDataPaths.PasswordVaultDirectory), StringComparison.Ordinal))
            AppDataPaths.PreparePasswordVaultDirectory();
        return new VaultSession(DirectoryPath);
    }

    private void CompleteUnlock(PasswordSeedProtector protector, string mode)
    {
        _protector = protector;
        _vault = new VaultStore(DirectoryPath, protector);
        var coordinator = new MultiGpuCoordinator(backend, _stateStore, _vault, coordinatorOptions);
        coordinator.Initialize();

        AppPersistedState persisted = _stateStore.Load();
        if (KeyIdPattern.TryParse(persisted.TargetInput, out KeyIdPattern? savedPattern, out _) && savedPattern is not null)
            coordinator.SetTargetPattern(savedPattern);

        coordinator.TelemetryUpdated += OnTelemetryUpdated;
        coordinator.MatchFound += OnMatchFound;
        _coordinator = coordinator;
        _vaultMode = mode;
        _lastTelemetryEmitTick = 0;
        _lastTelemetryState = coordinator.CurrentState;
    }

    private void AbortUnlock()
    {
        if (_coordinator is { } coordinator)
        {
            coordinator.TelemetryUpdated -= OnTelemetryUpdated;
            coordinator.MatchFound -= OnMatchFound;
            coordinator.Dispose();
            _coordinator = null;
        }

        _vault = null;
        _protector?.Dispose();
        _protector = null;
        _lease?.Dispose();
        _lease = null;
        _vaultMode = "none";
    }

    private async Task ReleaseUnlockedResourcesAsync()
    {
        if (_coordinator is { } coordinator)
        {
            coordinator.TelemetryUpdated -= OnTelemetryUpdated;
            coordinator.MatchFound -= OnMatchFound;
            await coordinator.StopAsync().ConfigureAwait(false);
            coordinator.Dispose();
            _coordinator = null;
        }

        _vault = null;
        _protector?.Dispose();
        _protector = null;
        _lease?.Dispose();
        _lease = null;
        _vaultMode = "none";
    }

    private void OnTelemetryUpdated(object? sender, CoordinatorTelemetry telemetry)
    {
        long now = Stopwatch.GetTimestamp();
        bool stateChanged = telemetry.State != _lastTelemetryState;
        if (!stateChanged && _lastTelemetryEmitTick != 0 && Stopwatch.GetElapsedTime(_lastTelemetryEmitTick) < TelemetryThrottle)
            return;

        _lastTelemetryEmitTick = now;
        _lastTelemetryState = telemetry.State;
        MultiGpuCoordinator? coordinator = _coordinator;
        if (coordinator is null) return;
        TelemetryEmitted?.Invoke(HostSnapshotMapper.MapTelemetry(coordinator, telemetry));
    }

    private void OnMatchFound(object? sender, MatchedKeyRecord record) =>
        MatchFoundEmitted?.Invoke(HostSnapshotMapper.MapMatch(record));

    private HostStatusSnapshot BuildStatusSnapshot() =>
        HostSnapshotMapper.BuildStatus(
            DirectoryPath,
            _stateStore.Load(),
            SupportsAutomaticUnlock,
            _vaultMode,
            _coordinator,
            _vault,
            GpuDiagnostics,
            coordinatorOptions);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
