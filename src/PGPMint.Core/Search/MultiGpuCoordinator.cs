using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator : IDisposable
{
    private readonly IGpuBackend _backend;
    private readonly AppStateStore _stateStore;
    private readonly VaultStore _vaultStore;
    private readonly CoordinatorOptions _options;
    private readonly IGpuTelemetrySensor _telemetrySensor;
    private readonly bool _ownsTelemetrySensor;

    private readonly object _lifecycleLock = new();
    private readonly object _submissionGate = new();
    private readonly Dictionary<string, bool> _enabledDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CalibrationRecord> _calibrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRuntimeState> _workerStates = new(StringComparer.Ordinal);

    private IReadOnlyList<GpuDeviceInfo> _devices = Array.Empty<GpuDeviceInfo>();
    private KeyIdPattern? _activePattern;
    private string _persistedPatternMask = string.Empty;
    private int _selectedTier = (int)PowerTier.Max;
    private volatile bool _autoPauseOnHit = true;
    private int _maxGpuTemperatureCelsius;
    private volatile bool _pausedExplicitly;
    private int _autoPauseTriggered;
    private bool _submissionFailed;
    private int _rateEpoch;

    private ulong _persistedTotalChecked;
    private long _sessionChecked;
    private SearchWorkMetrics _sessionWork = new(0, 0, 0, 0, 0);
    private int _totalMatchesCount;

    private volatile CoordinatorState _state = CoordinatorState.Idle;
    private double _calibrationRemainingSeconds;
    private string? _errorMessage;
    private SearchFailure _searchFailure;
    private volatile bool _vaultIsFull;

    private CancellationTokenSource? _activeOpCts;
    private Task? _activeOpTask;
    private bool _disposed;

    public IReadOnlyList<GpuDeviceInfo> Devices => _devices;
    public CoordinatorState CurrentState => _state;
    public PowerTier SelectedTier => (PowerTier)Volatile.Read(ref _selectedTier);
    public double FullCalibrationDurationSeconds => Math.Max(1.0, _options.FullCalibrationDuration.TotalSeconds);
    public bool AutoPauseOnHit => _autoPauseOnHit;
    public int? MaxGpuTemperatureCelsius
    {
        get
        {
            int value = Volatile.Read(ref _maxGpuTemperatureCelsius);
            return value > 0 ? value : null;
        }
    }
    private volatile CoordinatorTelemetry _latestTelemetry;
    public CoordinatorTelemetry LatestTelemetry => _latestTelemetry;

    public event EventHandler<CoordinatorTelemetry>? TelemetryUpdated;
    public event EventHandler<MatchedKeyRecord>? MatchFound;

    public MultiGpuCoordinator(
        IGpuBackend backend,
        AppStateStore stateStore,
        VaultStore vaultStore,
        CoordinatorOptions? options = null,
        IGpuTelemetrySensor? telemetrySensor = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _vaultStore = vaultStore ?? throw new ArgumentNullException(nameof(vaultStore));
        _options = options ?? new CoordinatorOptions();
        if (telemetrySensor is not null)
        {
            _telemetrySensor = telemetrySensor;
            _ownsTelemetrySensor = false;
        }
        else
        {
            _telemetrySensor = CreateDefaultTelemetrySensor();
            _ownsTelemetrySensor = true;
        }

        _latestTelemetry = BuildTelemetrySnapshot();
    }

    internal static IGpuTelemetrySensor CreateDefaultTelemetrySensor()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsGpuTelemetrySensor();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxGpuTelemetrySensor();
        }

        return NullGpuTelemetrySensor.Instance;
    }

    public void Initialize()
    {
        AppPersistedState persisted = _stateStore.Load();
        IReadOnlyList<GpuDeviceInfo> enumerated = _backend.EnumerateHardwareGpus();

        lock (_lifecycleLock)
        {
            _devices = enumerated;
            _persistedPatternMask = persisted.LastPatternMask ?? string.Empty;
            _persistedTotalChecked = persisted.TotalCheckedCandidates;

            PowerTier resolvedTier = persisted.PowerTier is { } savedTier && Enum.IsDefined(savedTier)
                ? savedTier
                : RateControl.TierFromLegacySlider(persisted.SliderPosition);
            Volatile.Write(ref _selectedTier, (int)resolvedTier);
            _autoPauseOnHit = persisted.AutoPauseOnHit;
            Volatile.Write(ref _maxGpuTemperatureCelsius, ThermalGuardController.NormalizeLimit(persisted.MaxGpuTemperatureCelsius) ?? 0);

            _calibrations.Clear();
            foreach (var kv in persisted.Calibrations)
            {
                _calibrations[kv.Key] = kv.Value;
            }

            _enabledDevices.Clear();
            bool hasSavedSelection = persisted.EnabledDeviceKeys.Count > 0;
            var savedSet = new HashSet<string>(persisted.EnabledDeviceKeys, StringComparer.Ordinal);
            bool anyDiscrete = _devices.Any(d => d.IsDiscrete);

            foreach (GpuDeviceInfo dev in _devices)
            {
                bool enabled = hasSavedSelection
                    ? savedSet.Contains(dev.DeviceKey)
                    : (!anyDiscrete || dev.IsDiscrete);
                _enabledDevices[dev.DeviceKey] = enabled;
                _workerStates[dev.DeviceKey] = new WorkerRuntimeState();
            }

            if (_devices.Count > 0 && !_enabledDevices.Values.Any(v => v))
            {
                _enabledDevices[_devices[0].DeviceKey] = true;
            }
        }

        try
        {
            _totalMatchesCount = _vaultStore.LoadMatches().Count;
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
            _totalMatchesCount = 0;
        }

        PublishTelemetry();
    }

    public bool IsDeviceEnabled(string deviceKey)
    {
        lock (_lifecycleLock)
        {
            return _enabledDevices.TryGetValue(deviceKey, out bool enabled) && enabled;
        }
    }

    public IReadOnlyList<GpuDeviceInfo> GetEnabledDevices()
    {
        lock (_lifecycleLock)
        {
            return _devices.Where(d => _enabledDevices.TryGetValue(d.DeviceKey, out bool e) && e).ToList();
        }
    }

    public bool NeedsFullCalibration()
    {
        lock (_lifecycleLock)
        {
            return NeedsFullCalibrationLocked();
        }
    }

    public void SetAutoPauseOnHit(bool enabled)
    {
        _autoPauseOnHit = enabled;
        _stateStore.Update(s => s.AutoPauseOnHit = enabled);
    }

    public void SetMaxGpuTemperatureCelsius(int? limitCelsius)
    {
        int? normalized = ThermalGuardController.NormalizeLimit(limitCelsius);
        Volatile.Write(ref _maxGpuTemperatureCelsius, normalized ?? 0);
        _stateStore.Update(s => s.MaxGpuTemperatureCelsius = normalized);
        PublishTelemetry();
    }

    public void SetTargetPattern(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        lock (_lifecycleLock)
        {
            if (_state is not (CoordinatorState.Idle or CoordinatorState.Faulted))
            {
                return;
            }

            _activePattern = pattern;
            SyncPatternProgressScope(pattern);
        }

        PublishTelemetry();
    }

    public async Task SetDeviceEnabledAsync(string deviceKey, bool enabled)
    {
        bool shouldRestart;
        KeyIdPattern? pattern;

        lock (_lifecycleLock)
        {
            if (!_enabledDevices.ContainsKey(deviceKey))
            {
                return;
            }

            if (_state is CoordinatorState.Calibrating or CoordinatorState.Stopping)
            {
                return;
            }

            if (!enabled && _enabledDevices.Count(kv => kv.Value) <= 1)
            {
                return;
            }

            _enabledDevices[deviceKey] = enabled;
            List<string> enabledKeys = _enabledDevices.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            _stateStore.Update(s => s.EnabledDeviceKeys = enabledKeys);

            shouldRestart = _state is CoordinatorState.Running or CoordinatorState.Paused &&
                            Volatile.Read(ref _autoPauseTriggered) == 0 &&
                            (_activeOpCts is null || !_activeOpCts.IsCancellationRequested);
            pattern = _activePattern;
        }

        if (shouldRestart && pattern is not null)
        {
            bool wasExplicitlyPaused = _pausedExplicitly;
            await StopAsync().ConfigureAwait(false);
            if (wasExplicitlyPaused)
            {
                _pausedExplicitly = true;
            }
            await StartSearchAsync(pattern).ConfigureAwait(false);
        }
        else
        {
            PublishTelemetry();
        }
    }

    public void SetPowerTier(PowerTier tier)
    {
        if (!Enum.IsDefined(tier))
        {
            return;
        }

        lock (_lifecycleLock)
        {
            // Power tier cannot be adjusted before or during the initial 30s curve measurement.
            if (_state is CoordinatorState.Calibrating or CoordinatorState.Stopping || NeedsFullCalibrationLocked())
            {
                return;
            }

            Volatile.Write(ref _selectedTier, (int)tier);
            Interlocked.Increment(ref _rateEpoch);
        }

        double legacySlider = RateControl.TierToLegacySlider(tier);
        _stateStore.Update(s =>
        {
            s.PowerTier = tier;
            s.SliderPosition = legacySlider;
        });
        PublishTelemetry();
    }

    public bool InvalidateCalibrationsForRecalibration()
    {
        HashSet<string> enabledKeys;
        List<string> removedKeys;
        lock (_lifecycleLock)
        {
            if (_state is not (CoordinatorState.Idle or CoordinatorState.Faulted))
            {
                return false;
            }

            enabledKeys = _enabledDevices
                .Where(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (enabledKeys.Count == 0)
            {
                return false;
            }

            removedKeys = _calibrations
                .Where(kv => enabledKeys.Contains(kv.Value.DeviceKey) || enabledKeys.Any(k => kv.Key.StartsWith(k + "|", StringComparison.Ordinal) || string.Equals(kv.Key, k, StringComparison.Ordinal)))
                .Select(kv => kv.Key)
                .ToList();

            foreach (string key in removedKeys)
            {
                _calibrations.Remove(key);
            }

            foreach (string devKey in enabledKeys)
            {
                if (_workerStates.TryGetValue(devKey, out WorkerRuntimeState? rt))
                {
                    ResetWorkerRuntime(rt);
                }
            }
        }

        if (removedKeys.Count > 0)
        {
            _stateStore.Update(s =>
            {
                foreach (string key in removedKeys)
                {
                    s.Calibrations.Remove(key);
                }
            });
        }

        PublishTelemetry();
        return true;
    }

    public void Pause()
    {
        lock (_lifecycleLock)
        {
            if (_state != CoordinatorState.Running)
            {
                return;
            }

            _pausedExplicitly = true;
            _state = CoordinatorState.Paused;
            Interlocked.Increment(ref _rateEpoch);
        }

        PublishTelemetry();
    }

    public void Resume()
    {
        lock (_lifecycleLock)
        {
            if (_state != CoordinatorState.Paused)
            {
                return;
            }

            Interlocked.Exchange(ref _autoPauseTriggered, 0);
            _pausedExplicitly = false;

            bool needsCal = NeedsFullCalibrationLocked();
            if (needsCal)
            {
                Volatile.Write(ref _calibrationRemainingSeconds, _options.FullCalibrationDuration.TotalSeconds);
                _state = CoordinatorState.Calibrating;
            }
            else
            {
                _state = CoordinatorState.Running;
            }

            Interlocked.Increment(ref _rateEpoch);
        }

        PublishTelemetry();
    }

    private bool NeedsFullCalibrationLocked()
    {
        foreach (GpuDeviceInfo d in _devices)
        {
            if (!_enabledDevices.TryGetValue(d.DeviceKey, out bool en) || !en)
            {
                continue;
            }

            if (GetValidatedCalibrationLocked(d) is null)
            {
                return true;
            }
        }

        return false;
    }

    public Task StartSearchAsync(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        IReadOnlyList<GpuDeviceInfo> enabled = GetEnabledDevices();
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException("No hardware GPU is available.");
        }

        lock (_lifecycleLock)
        {
            if ((_activeOpTask is not null && !_activeOpTask.IsCompleted) ||
                _state is not (CoordinatorState.Idle or CoordinatorState.Faulted))
            {
                throw new InvalidOperationException("An operation is already active.");
            }

            _activePattern = pattern;
            SyncPatternProgressScope(pattern);

            _errorMessage = null;
            (_backend as IWorkloadGpuBackend)?.SetWorkload(pattern);
            _searchFailure = SearchFailure.None;
            _vaultIsFull = false;
            _submissionFailed = false;
            Interlocked.Exchange(ref _sessionChecked, 0);
            _sessionWork = new SearchWorkMetrics(0, 0, 0, 0, 0);
            Interlocked.Exchange(ref _autoPauseTriggered, 0);

            var cts = new CancellationTokenSource();
            _activeOpCts = cts;

            bool needsFullCal = enabled.Any(d => GetValidatedCalibrationLocked(d) is null);
            bool startPaused = _pausedExplicitly;

            _state = startPaused
                ? CoordinatorState.Paused
                : (needsFullCal ? CoordinatorState.Calibrating : CoordinatorState.Running);

            Volatile.Write(ref _calibrationRemainingSeconds, (!startPaused && needsFullCal) ? _options.FullCalibrationDuration.TotalSeconds : 0.0);

            _activeOpTask = Task.Run(() => RunMultiGpuSessionAsync(enabled, pattern, cts), CancellationToken.None);
        }

        PublishTelemetry();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown errors during dispose
        }
        finally
        {
            if (_ownsTelemetrySensor)
            {
                try { _telemetrySensor.Dispose(); } catch { }
            }
        }
    }
}
