using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public enum CoordinatorState
{
    Idle,
    Calibrating,
    Running,
    Paused,
    Stopping,
    Faulted
}

public sealed record GpuTelemetrySnapshot(
    string DeviceKey,
    string Name,
    string BackendName,
    bool IsDiscrete,
    bool IsEnabled,
    double ActualHps,
    long TargetHps,
    double BaselineHps,
    bool IsFullCalibrated,
    string? Error);

public sealed record CoordinatorTelemetry(
    CoordinatorState State,
    double CalibrationRemainingSeconds,
    double TotalActualHps,
    long TotalTargetHps,
    bool IsFullSpeed,
    double TotalBaselineHps,
    bool AllEnabledFullyCalibrated,
    ulong SessionChecked,
    ulong TotalChecked,
    int TotalMatchesCount,
    double CumulativeProbability,
    TimeSpan? EstimatedMeanWait,
    string? ErrorMessage,
    IReadOnlyList<GpuTelemetrySnapshot> Devices)
{
    public SearchWorkMetrics SessionWork { get; init; }
    public bool VaultIsFull { get; init; }
}

public sealed class CoordinatorOptions
{
    public TimeSpan FullCalibrationDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CalibrationWindow { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DynamicMedianInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MinDynamicMedianSamples { get; init; } = 3;
    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public string? ExpectedKernelVersionOverride { get; init; }
}

public sealed partial class MultiGpuCoordinator : IDisposable
{
    private readonly IGpuBackend _backend;
    private readonly AppStateStore _stateStore;
    private readonly DpapiVaultStore _vaultStore;
    private readonly CoordinatorOptions _options;

    private readonly object _lifecycleLock = new();
    private readonly object _submissionGate = new();
    private readonly Dictionary<string, bool> _enabledDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CalibrationRecord> _calibrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRuntimeState> _workerStates = new(StringComparer.Ordinal);

    private IReadOnlyList<GpuDeviceInfo> _devices = Array.Empty<GpuDeviceInfo>();
    private KeyIdPattern? _activePattern;
    private string _persistedPatternMask = string.Empty;
    private double _sliderPosition = 1.0;
    private double _lastPositiveSliderPosition = 1.0;
    private volatile bool _autoPauseOnHit = true;
    private volatile bool _pausedExplicitly;
    private volatile bool _pausedByZeroSlider;
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
    private volatile bool _vaultIsFull;

    private CancellationTokenSource? _activeOpCts;
    private Task? _activeOpTask;
    private bool _disposed;

    public IReadOnlyList<GpuDeviceInfo> Devices => _devices;
    public CoordinatorState CurrentState => _state;
    public double SliderPosition => Volatile.Read(ref _sliderPosition);
    public double FullCalibrationDurationSeconds => Math.Max(1.0, _options.FullCalibrationDuration.TotalSeconds);
    public bool AutoPauseOnHit => _autoPauseOnHit;
    private volatile CoordinatorTelemetry _latestTelemetry;
    public CoordinatorTelemetry LatestTelemetry => _latestTelemetry;

    public event EventHandler<CoordinatorTelemetry>? TelemetryUpdated;
    public event EventHandler<MatchedKeyRecord>? MatchFound;

    public MultiGpuCoordinator(
        IGpuBackend backend,
        AppStateStore stateStore,
        DpapiVaultStore vaultStore,
        CoordinatorOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _vaultStore = vaultStore ?? throw new ArgumentNullException(nameof(vaultStore));
        _options = options ?? new CoordinatorOptions();

        _latestTelemetry = BuildTelemetrySnapshot();
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
            _sliderPosition = persisted.SliderPosition <= 0.0 ? 1.0 : Math.Clamp(persisted.SliderPosition, 0.01, 1.0);
            _lastPositiveSliderPosition = _sliderPosition;
            _autoPauseOnHit = persisted.AutoPauseOnHit;

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

            if (NeedsFullCalibrationLocked())
            {
                _sliderPosition = 1.0;
                _lastPositiveSliderPosition = 1.0;
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
        IReadOnlyList<GpuDeviceInfo> enabled = GetEnabledDevices();
        if (enabled.Count == 0)
        {
            return false;
        }

        lock (_lifecycleLock)
        {
            return enabled.Any(d =>
                !_calibrations.TryGetValue(d.DeviceKey, out CalibrationRecord? rec) ||
                !CalibrationValidator.IsValidForDevice(rec, d, ExpectedKernelFor(d), requireFull: true));
        }
    }

    public void SetAutoPauseOnHit(bool enabled)
    {
        _autoPauseOnHit = enabled;
        _stateStore.Update(s => s.AutoPauseOnHit = enabled);
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

    public void SetSliderPosition(double position)
    {
        double clamped = Math.Clamp(position, 0.01, 1.0);
        lock (_lifecycleLock)
        {
            // Slider cannot be adjusted before or during the initial 30s baseline measurement.
            if (_state is CoordinatorState.Calibrating or CoordinatorState.Stopping || NeedsFullCalibrationLocked())
            {
                return;
            }

            Volatile.Write(ref _sliderPosition, clamped);
            _lastPositiveSliderPosition = clamped;
            _pausedByZeroSlider = false;
            Interlocked.Increment(ref _rateEpoch);
        }

        _stateStore.Update(s => s.SliderPosition = clamped);
        PublishTelemetry();
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
        double? persistedSlider = null;
        lock (_lifecycleLock)
        {
            if (_state != CoordinatorState.Paused)
            {
                return;
            }

            if (Volatile.Read(ref _sliderPosition) <= 0.0)
            {
                double restored = _lastPositiveSliderPosition > 0.0 ? _lastPositiveSliderPosition : 1.0;
                Volatile.Write(ref _sliderPosition, restored);
                persistedSlider = restored;
            }

            Interlocked.Exchange(ref _autoPauseTriggered, 0);
            _pausedExplicitly = false;
            _pausedByZeroSlider = false;

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

        if (persistedSlider.HasValue)
        {
            _stateStore.Update(s => s.SliderPosition = persistedSlider.Value);
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

            if (!_calibrations.TryGetValue(d.DeviceKey, out CalibrationRecord? rec) ||
                !CalibrationValidator.IsValidForDevice(rec, d, ExpectedKernelFor(d), requireFull: true))
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
            _vaultIsFull = false;
            _submissionFailed = false;
            Interlocked.Exchange(ref _sessionChecked, 0);
            _sessionWork = new SearchWorkMetrics(0, 0, 0, 0, 0);
            Interlocked.Exchange(ref _autoPauseTriggered, 0);

            var cts = new CancellationTokenSource();
            _activeOpCts = cts;

            bool needsFullCal = enabled.Any(d =>
                !_calibrations.TryGetValue(d.DeviceKey, out CalibrationRecord? rec) ||
                !CalibrationValidator.IsValidForDevice(rec, d, ExpectedKernelFor(d), requireFull: true));

            bool startPaused = _pausedExplicitly || (!needsFullCal && Volatile.Read(ref _sliderPosition) <= 0.0);
            if (!needsFullCal && Volatile.Read(ref _sliderPosition) <= 0.0)
            {
                _pausedByZeroSlider = true;
            }
            else if (needsFullCal && Volatile.Read(ref _sliderPosition) <= 0.0)
            {
                // Initial 30s baseline measurement runs at full speed (100%).
                Volatile.Write(ref _sliderPosition, 1.0);
                _lastPositiveSliderPosition = 1.0;
                _pausedByZeroSlider = false;
                _stateStore.Update(s => s.SliderPosition = 1.0);
            }

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
    }
}
