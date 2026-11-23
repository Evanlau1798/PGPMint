using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public enum CoordinatorState
{
    Idle,
    QuickProbing,
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
    double PeakHps,
    bool IsFullCalibrated,
    bool IsProvisionalCalibrated,
    string? Error);

public sealed record CoordinatorTelemetry(
    CoordinatorState State,
    double CalibrationRemainingSeconds,
    double TotalActualHps,
    long TotalTargetHps,
    bool IsFullSpeed,
    double TotalPeakHps,
    bool AllEnabledFullyCalibrated,
    ulong SessionChecked,
    ulong TotalChecked,
    int TotalMatchesCount,
    double CumulativeProbability,
    TimeSpan? EstimatedMeanWait,
    string? ErrorMessage,
    IReadOnlyList<GpuTelemetrySnapshot> Devices);

public sealed class CoordinatorOptions
{
    public TimeSpan QuickProbeDuration { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FullCalibrationDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CalibrationWindow { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public string? ExpectedKernelVersionOverride { get; init; }
}

public sealed class MultiGpuCoordinator : IDisposable
{
    private static readonly KeyIdPattern DummyCalibrationPattern =
        KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");

    private readonly IGpuBackend _backend;
    private readonly AppStateStore _stateStore;
    private readonly DpapiVaultStore _vaultStore;
    private readonly CoordinatorOptions _options;

    private readonly object _lifecycleLock = new();
    private readonly Dictionary<string, bool> _enabledDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CalibrationRecord> _calibrations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkerRuntimeState> _workerStates = new(StringComparer.Ordinal);

    private IReadOnlyList<GpuDeviceInfo> _devices = Array.Empty<GpuDeviceInfo>();
    private KeyIdPattern? _activePattern;
    private string _persistedPatternMask = string.Empty;
    private double _sliderPosition = 0.7;
    private double _lastPositiveSliderPosition = 0.7;
    private bool _autoPauseOnHit = true;
    private bool _pausedExplicitly;
    private bool _pausedByZeroSlider;
    private int _rateEpoch;

    private ulong _persistedTotalChecked;
    private long _sessionChecked;
    private int _totalMatchesCount;

    private volatile CoordinatorState _state = CoordinatorState.Idle;
    private double _calibrationRemainingSeconds;
    private string? _errorMessage;

    // Unified single active operation slot (QuickProbe, ManualRecalibration, or Search)
    private CancellationTokenSource? _activeOpCts;
    private Task? _activeOpTask;
    private bool _stopRequestedOnActiveOp;
    private bool _disposed;

    public IReadOnlyList<GpuDeviceInfo> Devices => _devices;
    public CoordinatorState CurrentState => _state;
    public double SliderPosition => Volatile.Read(ref _sliderPosition);
    public bool AutoPauseOnHit => _autoPauseOnHit;
    public CoordinatorTelemetry LatestTelemetry { get; private set; }

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

        LatestTelemetry = BuildTelemetrySnapshot();
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
            _sliderPosition = Math.Clamp(persisted.SliderPosition, 0.0, 1.0);
            _lastPositiveSliderPosition = _sliderPosition > 0.0 ? _sliderPosition : 0.7;
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

    public bool NeedsQuickProbe()
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
                !CalibrationValidator.IsValidForDevice(rec, d, ExpectedKernelFor(d), requireFull: false));
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
        CoordinatorState current;
        KeyIdPattern? pattern;

        lock (_lifecycleLock)
        {
            if (!_enabledDevices.ContainsKey(deviceKey))
            {
                return;
            }

            // Refuse device changes while calibrating, probing, or stopping
            if (_state is CoordinatorState.QuickProbing or CoordinatorState.Calibrating or CoordinatorState.Stopping)
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

            current = _state;
            pattern = _activePattern;
        }

        if (current is CoordinatorState.Running or CoordinatorState.Paused && pattern is not null)
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
        double clamped = Math.Clamp(position, 0.0, 1.0);
        Volatile.Write(ref _sliderPosition, clamped);

        if (clamped > 0.0)
        {
            _lastPositiveSliderPosition = clamped;
            if (_pausedByZeroSlider)
            {
                _pausedByZeroSlider = false;
                if (!_pausedExplicitly && _state == CoordinatorState.Paused)
                {
                    _state = CoordinatorState.Running;
                }
            }
        }
        else
        {
            _pausedByZeroSlider = true;
            if (_state == CoordinatorState.Running)
            {
                _state = CoordinatorState.Paused;
            }
        }

        Interlocked.Increment(ref _rateEpoch);
        _stateStore.Update(s => s.SliderPosition = clamped);
        PublishTelemetry();
    }

    public void Pause()
    {
        if (_state != CoordinatorState.Running)
        {
            return;
        }

        _pausedExplicitly = true;
        _state = CoordinatorState.Paused;
        Interlocked.Increment(ref _rateEpoch);
        PublishTelemetry();
    }

    public void Resume()
    {
        if (_state != CoordinatorState.Paused)
        {
            return;
        }

        if (Volatile.Read(ref _sliderPosition) <= 0.0)
        {
            double restored = _lastPositiveSliderPosition > 0.0 ? _lastPositiveSliderPosition : 0.7;
            Volatile.Write(ref _sliderPosition, restored);
            _stateStore.Update(s => s.SliderPosition = restored);
        }

        _pausedExplicitly = false;
        _pausedByZeroSlider = false;
        _state = NeedsFullCalibration() ? CoordinatorState.Calibrating : CoordinatorState.Running;
        Interlocked.Increment(ref _rateEpoch);
        PublishTelemetry();
    }

    public async Task RunQuickProbeAsync(CancellationToken externalToken = default)
    {
        IReadOnlyList<GpuDeviceInfo> enabled = GetEnabledDevices();
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException("No hardware GPU is available.");
        }

        Task opTask;
        CancellationTokenSource linkedCts;

        lock (_lifecycleLock)
        {
            if (_activeOpTask is not null && !_activeOpTask.IsCompleted)
            {
                return;
            }

            _errorMessage = null;
            _stopRequestedOnActiveOp = false;
            _state = CoordinatorState.QuickProbing;
            _calibrationRemainingSeconds = _options.QuickProbeDuration.TotalSeconds;

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _activeOpCts = linkedCts;

            opTask = Task.Run(async () =>
            {
                try
                {
                    await RunStandaloneCalibrationCoreAsync(
                        enabled,
                        DummyCalibrationPattern,
                        _options.QuickProbeDuration,
                        isFullCalibration: false,
                        countTowardProgress: false,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Clean cancellation
                }
                catch (Exception ex)
                {
                    _errorMessage = ex.Message;
                    _state = CoordinatorState.Faulted;
                    throw;
                }
                finally
                {
                    _calibrationRemainingSeconds = 0;
                    ResetTransientRates();
                    if (_state != CoordinatorState.Faulted)
                    {
                        _state = CoordinatorState.Idle;
                    }
                    ClearActiveOperation(linkedCts);
                    PublishTelemetry();
                }
            }, CancellationToken.None);

            _activeOpTask = opTask;
        }

        PublishTelemetry();
        await opTask.ConfigureAwait(false);
    }

    public async Task RunManualRecalibrationAsync(KeyIdPattern? currentPattern = null, CancellationToken externalToken = default)
    {
        IReadOnlyList<GpuDeviceInfo> enabled = GetEnabledDevices();
        if (enabled.Count == 0)
        {
            throw new InvalidOperationException("No hardware GPU is available.");
        }

        CoordinatorState priorState = _state;
        double priorSlider = Volatile.Read(ref _sliderPosition);
        bool priorExplicitPause = _pausedExplicitly;
        bool priorZeroPause = _pausedByZeroSlider;
        KeyIdPattern? priorPattern = _activePattern ?? currentPattern;

        if (priorState is CoordinatorState.Running or CoordinatorState.Paused)
        {
            await StopAsync().ConfigureAwait(false);
        }
        else if (priorState is CoordinatorState.QuickProbing or CoordinatorState.Calibrating or CoordinatorState.Stopping)
        {
            return;
        }

        Task opTask;
        CancellationTokenSource linkedCts;
        bool wasExternallyCanceled = false;
        bool wasStopPressed = false;
        bool faulted = false;

        lock (_lifecycleLock)
        {
            if (_activeOpTask is not null && !_activeOpTask.IsCompleted)
            {
                return;
            }

            _errorMessage = null;
            _stopRequestedOnActiveOp = false;
            _state = CoordinatorState.Calibrating;
            _calibrationRemainingSeconds = _options.FullCalibrationDuration.TotalSeconds;

            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            _activeOpCts = linkedCts;

            KeyIdPattern pattern = priorPattern ?? DummyCalibrationPattern;
            bool realSearch = priorPattern is not null;
            if (realSearch)
            {
                SyncPatternProgressScope(pattern);
            }

            opTask = Task.Run(async () =>
            {
                try
                {
                    await RunStandaloneCalibrationCoreAsync(
                        enabled,
                        pattern,
                        _options.FullCalibrationDuration,
                        isFullCalibration: true,
                        countTowardProgress: realSearch,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasStopPressed = _stopRequestedOnActiveOp;
                    wasExternallyCanceled = !wasStopPressed;
                }
                catch (Exception ex)
                {
                    faulted = true;
                    _errorMessage = ex.Message;
                    _state = CoordinatorState.Faulted;
                }
                finally
                {
                    _calibrationRemainingSeconds = 0;
                    ResetTransientRates();
                    ClearActiveOperation(linkedCts);
                }
            }, CancellationToken.None);

            _activeOpTask = opTask;
        }

        PublishTelemetry();
        await opTask.ConfigureAwait(false);

        if (faulted)
        {
            PublishTelemetry();
            return;
        }

        // Explicit Stop during calibration stops cleanly in Idle while preserving prior calibration.
        // External cancellation (e.g. Cancel calibration) preserves prior calibration AND restores prior state.
        if (wasStopPressed)
        {
            _state = CoordinatorState.Idle;
            PublishTelemetry();
            return;
        }

        if (wasExternallyCanceled || true)
        {
            if (priorState == CoordinatorState.Running && priorPattern is not null)
            {
                Volatile.Write(ref _sliderPosition, priorSlider);
                _pausedExplicitly = false;
                _pausedByZeroSlider = priorSlider <= 0.0;
                await StartSearchAsync(priorPattern).ConfigureAwait(false);
            }
            else if (priorState == CoordinatorState.Paused && priorPattern is not null)
            {
                Volatile.Write(ref _sliderPosition, priorSlider);
                _pausedExplicitly = priorExplicitPause;
                _pausedByZeroSlider = priorZeroPause || priorSlider <= 0.0;
                await StartSearchAsync(priorPattern).ConfigureAwait(false);
            }
            else
            {
                _state = CoordinatorState.Idle;
                PublishTelemetry();
            }
        }
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
            if (_activeOpTask is not null && !_activeOpTask.IsCompleted)
            {
                throw new InvalidOperationException("An operation is already active.");
            }

            _activePattern = pattern;
            SyncPatternProgressScope(pattern);

            _errorMessage = null;
            _stopRequestedOnActiveOp = false;
            Interlocked.Exchange(ref _sessionChecked, 0);

            var cts = new CancellationTokenSource();
            _activeOpCts = cts;

            bool needsFullCal = enabled.Any(d =>
                !_calibrations.TryGetValue(d.DeviceKey, out CalibrationRecord? rec) ||
                !CalibrationValidator.IsValidForDevice(rec, d, ExpectedKernelFor(d), requireFull: true));

            bool startPaused = _pausedExplicitly || Volatile.Read(ref _sliderPosition) <= 0.0;
            if (Volatile.Read(ref _sliderPosition) <= 0.0)
            {
                _pausedByZeroSlider = true;
            }

            _state = startPaused
                ? CoordinatorState.Paused
                : (needsFullCal ? CoordinatorState.Calibrating : CoordinatorState.Running);

            _calibrationRemainingSeconds = (!startPaused && needsFullCal) ? _options.FullCalibrationDuration.TotalSeconds : 0.0;

            _activeOpTask = Task.Run(() => RunMultiGpuSessionAsync(enabled, pattern, cts), CancellationToken.None);
        }

        PublishTelemetry();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? runningTask;
        CancellationTokenSource? cts;

        lock (_lifecycleLock)
        {
            runningTask = _activeOpTask;
            cts = _activeOpCts;
            if (runningTask is null)
            {
                _pausedExplicitly = false;
                _pausedByZeroSlider = false;
                _state = CoordinatorState.Idle;
                _calibrationRemainingSeconds = 0;
                PublishTelemetry();
                return;
            }

            _stopRequestedOnActiveOp = true;
            _state = CoordinatorState.Stopping;
        }

        PublishTelemetry();

        try
        {
            cts?.Cancel();
            await runningTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch
        {
            // Errors are already captured in _errorMessage / state
        }
        finally
        {
            FlushSessionCheckedIntoPersisted();
            _pausedExplicitly = false;
            _pausedByZeroSlider = false;
            _calibrationRemainingSeconds = 0;
            ResetTransientRates();
            if (_state != CoordinatorState.Faulted)
            {
                _state = CoordinatorState.Idle;
            }
            PublishTelemetry();
        }
    }

    private void ClearActiveOperation(CancellationTokenSource expectedCts)
    {
        lock (_lifecycleLock)
        {
            if (ReferenceEquals(_activeOpCts, expectedCts))
            {
                _activeOpCts = null;
                _activeOpTask = null;
            }
        }
        expectedCts.Dispose();
    }

    private void SyncPatternProgressScope(KeyIdPattern pattern)
    {
        if (!string.Equals(_persistedPatternMask, pattern.CanonicalMask, StringComparison.Ordinal))
        {
            _persistedPatternMask = pattern.CanonicalMask;
            _persistedTotalChecked = 0;
            Interlocked.Exchange(ref _sessionChecked, 0);
            _stateStore.Update(s =>
            {
                s.LastPatternMask = pattern.CanonicalMask;
                s.TotalCheckedCandidates = 0;
            });
        }
    }

    private async Task RunStandaloneCalibrationCoreAsync(
        IReadOnlyList<GpuDeviceInfo> enabled,
        KeyIdPattern pattern,
        TimeSpan duration,
        bool isFullCalibration,
        bool countTowardProgress,
        CancellationToken ct)
    {
        var tasks = new List<Task<CalibrationRecord?>>(enabled.Count);
        long startTs = Stopwatch.GetTimestamp();

        using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task telemetryLoop = Task.Run(async () =>
        {
            while (!telemetryCts.IsCancellationRequested)
            {
                double elapsed = Stopwatch.GetElapsedTime(startTs).TotalSeconds;
                _calibrationRemainingSeconds = Math.Max(0.0, duration.TotalSeconds - elapsed);
                PublishTelemetry();
                try
                {
                    await Task.Delay(_options.TelemetryInterval, telemetryCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        try
        {
            using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            foreach (GpuDeviceInfo dev in enabled)
            {
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        return CalibrateSingleDevice(dev, pattern, duration, isFullCalibration, countTowardProgress, workerCts.Token);
                    }
                    catch when (!workerCts.IsCancellationRequested)
                    {
                        workerCts.Cancel();
                        throw;
                    }
                }, CancellationToken.None));
            }

            CalibrationRecord?[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            foreach (CalibrationRecord? record in results)
            {
                if (record is not null)
                {
                    SaveCalibrationRecord(record);
                }
            }
        }
        finally
        {
            telemetryCts.Cancel();
            try { await telemetryLoop.ConfigureAwait(false); } catch { }
            if (countTowardProgress)
            {
                FlushSessionCheckedIntoPersisted();
            }
        }
    }

    private CalibrationRecord? CalibrateSingleDevice(
        GpuDeviceInfo devInfo,
        KeyIdPattern pattern,
        TimeSpan duration,
        bool isFullCalibration,
        bool countTowardProgress,
        CancellationToken ct)
    {
        WorkerRuntimeState rt = GetOrCreateRuntimeState(devInfo.DeviceKey);
        rt.Error = null;
        rt.ActualHps = 0;

        using IGpuSearchDevice device = _backend.CreateDevice(devInfo);
        ReconcileActualDeviceInfo(device);
        GpuDeviceInfo actualInfo = device.Info with { KernelVersion = device.KernelVersion };
        Ed25519KeyPair[] keys = GenerateKeyGroup(device.KeysPerBatch);
        byte[][] pubKeys = keys.Select(k => k.PublicKey).ToArray();
        uint cursor = 1u;
        uint end = CurrentTimestampUpperBound();

        try
        {
            ct.ThrowIfCancellationRequested();

            // Warmup batch before measurement clock starts
            uint warmupStamps = RateControl.ComputeBatchTimestamps(
                device.MaxCandidatesPerBatch,
                device.KeysPerBatch,
                new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                end - cursor);

            var warmupHits = device.ScanGroup(pubKeys, cursor, warmupStamps, pattern);
            if (countTowardProgress)
            {
                HandleVerifiedHits(devInfo, keys, warmupHits, pattern, allowAutoPause: false);
                Interlocked.Add(ref _sessionChecked, (long)warmupStamps * device.KeysPerBatch);
            }
            cursor += warmupStamps;

            var samples = new List<double>();
            long calStart = Stopwatch.GetTimestamp();
            long windowStart = calStart;
            long windowChecked = 0;
            long totalMeasuredChecked = 0;

            while (!ct.IsCancellationRequested)
            {
                double totalElapsed = Stopwatch.GetElapsedTime(calStart).TotalSeconds;
                if (totalElapsed >= duration.TotalSeconds)
                {
                    break;
                }

                if (cursor >= end)
                {
                    ZeroKeyGroup(keys);
                    keys = GenerateKeyGroup(device.KeysPerBatch);
                    pubKeys = keys.Select(k => k.PublicKey).ToArray();
                    cursor = 1u;
                    end = CurrentTimestampUpperBound();
                }

                uint stamps = RateControl.ComputeBatchTimestamps(
                    device.MaxCandidatesPerBatch,
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                var hits = device.ScanGroup(pubKeys, cursor, stamps, pattern);
                long batchCandidates = (long)stamps * device.KeysPerBatch;

                if (countTowardProgress)
                {
                    HandleVerifiedHits(devInfo, keys, hits, pattern, allowAutoPause: false);
                    Interlocked.Add(ref _sessionChecked, batchCandidates);
                }

                cursor += stamps;
                windowChecked += batchCandidates;
                totalMeasuredChecked += batchCandidates;

                double windowElapsed = Stopwatch.GetElapsedTime(windowStart).TotalSeconds;
                if (windowElapsed >= _options.CalibrationWindow.TotalSeconds)
                {
                    double hps = windowChecked / windowElapsed;
                    samples.Add(hps);
                    rt.ActualHps = hps;
                    windowStart = Stopwatch.GetTimestamp();
                    windowChecked = 0;
                }
            }

            ct.ThrowIfCancellationRequested();

            if (samples.Count == 0 && totalMeasuredChecked > 0)
            {
                double elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(calStart).TotalSeconds);
                samples.Add(totalMeasuredChecked / elapsed);
            }

            if (samples.Count == 0)
            {
                return null;
            }

            samples.Sort();
            double peak = samples[^1];
            double median = samples.Count % 2 == 1
                ? samples[samples.Count / 2]
                : 0.5 * (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]);

            return new CalibrationRecord(
                DeviceKey: actualInfo.DeviceKey,
                DeviceName: actualInfo.Name,
                DriverVersion: actualInfo.DriverVersion,
                KernelVersion: actualInfo.KernelVersion,
                PeakHashesPerSecond: peak,
                MedianHashesPerSecond: median,
                IsFullCalibration: isFullCalibration,
                CalibratedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            rt.Error = ex.Message;
            throw;
        }
        finally
        {
            rt.ActualHps = 0;
            ZeroKeyGroup(keys);
        }
    }

    private async Task RunMultiGpuSessionAsync(
        IReadOnlyList<GpuDeviceInfo> enabled,
        KeyIdPattern pattern,
        CancellationTokenSource sessionCts)
    {
        CancellationToken ct = sessionCts.Token;
        var devices = new List<IGpuSearchDevice>(enabled.Count);
        using var failureLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task? telemetryLoop = null;
        long calStartTimestamp = Stopwatch.GetTimestamp();

        try
        {
            foreach (GpuDeviceInfo info in enabled)
            {
                IGpuSearchDevice created = _backend.CreateDevice(info);
                ReconcileActualDeviceInfo(created);
                devices.Add(created);
            }

            telemetryLoop = Task.Run(async () =>
            {
                long lastPersist = Stopwatch.GetTimestamp();
                while (!telemetryCts.IsCancellationRequested)
                {
                    if (_state == CoordinatorState.Calibrating)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref calStartTimestamp)).TotalSeconds;
                        _calibrationRemainingSeconds = Math.Max(0.0, _options.FullCalibrationDuration.TotalSeconds - elapsed);
                    }
                    else
                    {
                        _calibrationRemainingSeconds = 0.0;
                    }

                    PublishTelemetry();

                    if (Stopwatch.GetElapsedTime(lastPersist).TotalSeconds >= 5.0)
                    {
                        PersistTotalChecked();
                        lastPersist = Stopwatch.GetTimestamp();
                    }

                    try
                    {
                        await Task.Delay(_options.TelemetryInterval, telemetryCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);

            // Phase 1: Zero-waste 30s full calibration on open devices.
            // If interrupted by pause (e.g. AutoPauseOnHit or slider=0), wait cleanly while paused and retry calibration upon Resume.
            while (!failureLinkedCts.IsCancellationRequested && NeedsFullCalibration())
            {
                if (IsCurrentlyPaused())
                {
                    _state = CoordinatorState.Paused;
                    _calibrationRemainingSeconds = 0.0;
                    ResetTransientRates();
                    PublishTelemetry();
                    await Task.Delay(40, failureLinkedCts.Token).ConfigureAwait(false);
                    continue;
                }

                _state = CoordinatorState.Calibrating;
                Volatile.Write(ref calStartTimestamp, Stopwatch.GetTimestamp());
                _calibrationRemainingSeconds = _options.FullCalibrationDuration.TotalSeconds;
                PublishTelemetry();

                var calTasks = devices.Select(dev => Task.Run(() =>
                {
                    try
                    {
                        return RunZeroWasteCalibrationOnOpenDevice(dev, pattern, failureLinkedCts.Token);
                    }
                    catch when (!failureLinkedCts.IsCancellationRequested)
                    {
                        failureLinkedCts.Cancel();
                        throw;
                    }
                }, CancellationToken.None)).ToArray();

                CalibrationRecord?[] records = await Task.WhenAll(calTasks).ConfigureAwait(false);

                if (failureLinkedCts.IsCancellationRequested)
                {
                    break;
                }

                bool completedAll = !IsCurrentlyPaused() && records.All(r => r is not null);
                if (completedAll)
                {
                    foreach (CalibrationRecord? rec in records)
                    {
                        if (rec is not null)
                        {
                            SaveCalibrationRecord(rec);
                        }
                    }

                    PersistTotalChecked();
                    _calibrationRemainingSeconds = 0.0;
                    break;
                }
            }

            if (!failureLinkedCts.IsCancellationRequested)
            {
                _state = IsCurrentlyPaused() ? CoordinatorState.Paused : CoordinatorState.Running;
                PublishTelemetry();

                // Phase 2: Continuous multi-GPU throttled search on the already-open devices
                var workerTasks = devices.Select(dev => Task.Run(() =>
                {
                    try
                    {
                        RunSearchWorkerLoop(dev, pattern, failureLinkedCts.Token);
                    }
                    catch when (!failureLinkedCts.IsCancellationRequested)
                    {
                        failureLinkedCts.Cancel();
                        throw;
                    }
                }, CancellationToken.None)).ToArray();

                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean user stop
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
            _state = CoordinatorState.Faulted;
            PublishTelemetry();
        }
        finally
        {
            telemetryCts.Cancel();
            if (telemetryLoop is not null)
            {
                try { await telemetryLoop.ConfigureAwait(false); } catch { }
            }

            foreach (IGpuSearchDevice dev in devices)
            {
                try { dev.Dispose(); } catch { }
            }

            FlushSessionCheckedIntoPersisted();
            ClearActiveOperation(sessionCts);
        }
    }

    private CalibrationRecord? RunZeroWasteCalibrationOnOpenDevice(
        IGpuSearchDevice device,
        KeyIdPattern pattern,
        CancellationToken ct)
    {
        WorkerRuntimeState rt = GetOrCreateRuntimeState(device.Info.DeviceKey);
        rt.Error = null;
        Ed25519KeyPair[] keys = GenerateKeyGroup(device.KeysPerBatch);
        byte[][] pubKeys = keys.Select(k => k.PublicKey).ToArray();
        uint cursor = 1u;
        uint end = CurrentTimestampUpperBound();

        try
        {
            // Warmup batch (counted toward search progress & hits so nothing is wasted)
            if (!ct.IsCancellationRequested && !IsCurrentlyPaused())
            {
                uint warmupStamps = RateControl.ComputeBatchTimestamps(
                    device.MaxCandidatesPerBatch,
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                var warmupHits = device.ScanGroup(pubKeys, cursor, warmupStamps, pattern);
                Interlocked.Add(ref _sessionChecked, (long)warmupStamps * device.KeysPerBatch);
                HandleVerifiedHits(device.Info, keys, warmupHits, pattern, allowAutoPause: true);
                cursor += warmupStamps;
            }

            var samples = new List<double>();
            long calStart = Stopwatch.GetTimestamp();
            long windowStart = calStart;
            long windowChecked = 0;
            long totalMeasured = 0;

            while (!ct.IsCancellationRequested && !IsCurrentlyPaused())
            {
                if (Stopwatch.GetElapsedTime(calStart).TotalSeconds >= _options.FullCalibrationDuration.TotalSeconds)
                {
                    break;
                }

                if (cursor >= end)
                {
                    ZeroKeyGroup(keys);
                    keys = GenerateKeyGroup(device.KeysPerBatch);
                    pubKeys = keys.Select(k => k.PublicKey).ToArray();
                    cursor = 1u;
                    end = CurrentTimestampUpperBound();
                }

                uint stamps = RateControl.ComputeBatchTimestamps(
                    device.MaxCandidatesPerBatch,
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                var hits = device.ScanGroup(pubKeys, cursor, stamps, pattern);
                long batchCandidates = (long)stamps * device.KeysPerBatch;
                Interlocked.Add(ref _sessionChecked, batchCandidates);
                HandleVerifiedHits(device.Info, keys, hits, pattern, allowAutoPause: true);

                cursor += stamps;
                windowChecked += batchCandidates;
                totalMeasured += batchCandidates;

                double windowElapsed = Stopwatch.GetElapsedTime(windowStart).TotalSeconds;
                if (windowElapsed >= _options.CalibrationWindow.TotalSeconds)
                {
                    double hps = windowChecked / windowElapsed;
                    samples.Add(hps);
                    rt.ActualHps = hps;
                    windowStart = Stopwatch.GetTimestamp();
                    windowChecked = 0;
                }
            }

            if (ct.IsCancellationRequested || IsCurrentlyPaused())
            {
                return null;
            }

            if (samples.Count == 0 && totalMeasured > 0)
            {
                double elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(calStart).TotalSeconds);
                samples.Add(totalMeasured / elapsed);
            }

            if (samples.Count == 0)
            {
                return null;
            }

            samples.Sort();
            double peak = samples[^1];
            double median = samples.Count % 2 == 1
                ? samples[samples.Count / 2]
                : 0.5 * (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]);

            return new CalibrationRecord(
                DeviceKey: device.Info.DeviceKey,
                DeviceName: device.Info.Name,
                DriverVersion: device.Info.DriverVersion,
                KernelVersion: device.KernelVersion,
                PeakHashesPerSecond: peak,
                MedianHashesPerSecond: median,
                IsFullCalibration: true,
                CalibratedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            rt.Error = ex.Message;
            throw;
        }
        finally
        {
            ZeroKeyGroup(keys);
        }
    }

    private void RunSearchWorkerLoop(IGpuSearchDevice device, KeyIdPattern pattern, CancellationToken ct)
    {
        WorkerRuntimeState rt = GetOrCreateRuntimeState(device.Info.DeviceKey);
        rt.Error = null;

        Ed25519KeyPair[] keys = GenerateKeyGroup(device.KeysPerBatch);
        byte[][] pubKeys = keys.Select(k => k.PublicKey).ToArray();
        uint cursor = 1u;
        uint end = CurrentTimestampUpperBound();

        double readyTimeSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        long sampleStart = Stopwatch.GetTimestamp();
        long sampleChecked = 0;
        int seenRateEpoch = -1;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int currentEpoch = Volatile.Read(ref _rateEpoch);
                double slider = IsCurrentlyPaused() ? 0.0 : Volatile.Read(ref _sliderPosition);
                double devicePeak = GetValidatedDevicePeakHps(device.Info);
                RateDirective directive = RateControl.FromSlider(slider, devicePeak);

                double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                if (currentEpoch != seenRateEpoch)
                {
                    seenRateEpoch = currentEpoch;
                    readyTimeSec = nowSec;
                    sampleStart = Stopwatch.GetTimestamp();
                    sampleChecked = 0;
                    if (directive.Mode == RateMode.Paused)
                    {
                        rt.ActualHps = 0;
                    }
                }
                else
                {
                    double sampleElapsed = Stopwatch.GetElapsedTime(sampleStart).TotalSeconds;
                    if (sampleElapsed >= 1.0)
                    {
                        rt.ActualHps = directive.Mode == RateMode.Paused ? 0.0 : sampleChecked / sampleElapsed;
                        sampleStart = Stopwatch.GetTimestamp();
                        sampleChecked = 0;
                    }
                }

                if (directive.Mode == RateMode.Paused)
                {
                    rt.ActualHps = 0;
                    Thread.Sleep(25);
                    continue;
                }

                if (nowSec < readyTimeSec)
                {
                    int sleepMs = (int)Math.Clamp((readyTimeSec - nowSec) * 1000.0, 1.0, 40.0);
                    Thread.Sleep(sleepMs);
                    continue;
                }

                if (cursor >= end)
                {
                    ZeroKeyGroup(keys);
                    keys = GenerateKeyGroup(device.KeysPerBatch);
                    pubKeys = keys.Select(k => k.PublicKey).ToArray();
                    cursor = 1u;
                    end = CurrentTimestampUpperBound();
                }

                uint stamps = RateControl.ComputeBatchTimestamps(
                    device.MaxCandidatesPerBatch,
                    device.KeysPerBatch,
                    directive,
                    end - cursor);

                var hits = device.ScanGroup(pubKeys, cursor, stamps, pattern);
                long batchCandidates = (long)stamps * device.KeysPerBatch;

                Interlocked.Add(ref _sessionChecked, batchCandidates);
                sampleChecked += batchCandidates;
                cursor += stamps;

                HandleVerifiedHits(device.Info, keys, hits, pattern, allowAutoPause: true);

                if (directive.Mode == RateMode.Capped)
                {
                    double afterSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    readyTimeSec = Math.Max(afterSec, readyTimeSec + (batchCandidates / (double)directive.TargetHashesPerSecond));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit
        }
        catch (Exception ex)
        {
            rt.Error = ex.Message;
            throw;
        }
        finally
        {
            rt.ActualHps = 0;
            ZeroKeyGroup(keys);
        }
    }

    private void HandleVerifiedHits(
        GpuDeviceInfo deviceInfo,
        Ed25519KeyPair[] keys,
        IReadOnlyList<uint>[] laneHits,
        KeyIdPattern pattern,
        bool allowAutoPause)
    {
        bool anySaved = false;

        for (int lane = 0; lane < keys.Length; lane++)
        {
            IReadOnlyList<uint> timestamps = laneHits[lane];
            if (timestamps.Count == 0)
            {
                continue;
            }

            foreach (uint ts in timestamps.OrderBy(t => t))
            {
                string fullFingerprint = OpenPgpV4Packet.ComputeFingerprint(keys[lane].PublicKey, ts);
                ulong keyIdValue = OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane].PublicKey, ts);
                if (!pattern.Matches(keyIdValue))
                {
                    throw new InvalidOperationException(
                        $"GPU hit verification failed on '{deviceInfo.Name}': keyId {keyIdValue:X16} did not match {pattern.CanonicalMask}.");
                }

                MatchedKeyRecord record = _vaultStore.SaveMatch(
                    keys[lane].PrivateSeed,
                    keys[lane].PublicKey,
                    ts,
                    fullFingerprint,
                    pattern.CanonicalMask,
                    deviceInfo.Name);

                Interlocked.Exchange(ref _totalMatchesCount, _vaultStore.LoadMatches().Count);
                anySaved = true;
                MatchFound?.Invoke(this, record);
            }
        }

        if (anySaved && allowAutoPause && _autoPauseOnHit)
        {
            _pausedExplicitly = true;
            _state = CoordinatorState.Paused;
            Interlocked.Increment(ref _rateEpoch);
            PublishTelemetry();
        }
    }

    private bool IsCurrentlyPaused() => _pausedExplicitly || _pausedByZeroSlider;

    private void SaveCalibrationRecord(CalibrationRecord record)
    {
        lock (_lifecycleLock)
        {
            _calibrations[record.DeviceKey] = record;
        }

        _stateStore.Update(s => s.Calibrations[record.DeviceKey] = record);
    }

    private void PersistTotalChecked()
    {
        long sessionDelta = Interlocked.Read(ref _sessionChecked);
        if (sessionDelta <= 0)
        {
            return;
        }

        ulong total = _persistedTotalChecked + (ulong)sessionDelta;
        string mask = _persistedPatternMask;
        _stateStore.Update(s =>
        {
            s.LastPatternMask = mask;
            if (total > s.TotalCheckedCandidates)
            {
                s.TotalCheckedCandidates = total;
            }
        });
    }

    private void FlushSessionCheckedIntoPersisted()
    {
        long delta = Interlocked.Exchange(ref _sessionChecked, 0);
        if (delta <= 0)
        {
            return;
        }

        _persistedTotalChecked += (ulong)delta;
        ulong total = _persistedTotalChecked;
        string mask = _persistedPatternMask;
        _stateStore.Update(s =>
        {
            s.LastPatternMask = mask;
            if (total > s.TotalCheckedCandidates)
            {
                s.TotalCheckedCandidates = total;
            }
        });
    }

    private void ReconcileActualDeviceInfo(IGpuSearchDevice device)
    {
        bool changed = false;
        lock (_lifecycleLock)
        {
            var list = _devices.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].DeviceKey, device.Info.DeviceKey, StringComparison.Ordinal))
                {
                    if (!string.Equals(list[i].BackendName, device.Info.BackendName, StringComparison.Ordinal) ||
                        !string.Equals(list[i].KernelVersion, device.KernelVersion, StringComparison.Ordinal))
                    {
                        list[i] = list[i] with
                        {
                            BackendName = device.Info.BackendName,
                            KernelVersion = device.KernelVersion
                        };
                        _devices = list;
                        changed = true;
                    }
                    break;
                }
            }
        }

        if (changed)
        {
            PublishTelemetry();
        }
    }

    private string ExpectedKernelFor(GpuDeviceInfo device) =>
        _options.ExpectedKernelVersionOverride ?? device.KernelVersion;

    private double GetValidatedDevicePeakHps(GpuDeviceInfo device)
    {
        lock (_lifecycleLock)
        {
            if (_calibrations.TryGetValue(device.DeviceKey, out CalibrationRecord? rec) &&
                CalibrationValidator.IsValidForDevice(rec, device, ExpectedKernelFor(device), requireFull: false))
            {
                return rec.PeakHashesPerSecond;
            }
            return 0.0;
        }
    }

    private WorkerRuntimeState GetOrCreateRuntimeState(string deviceKey)
    {
        lock (_lifecycleLock)
        {
            if (!_workerStates.TryGetValue(deviceKey, out WorkerRuntimeState? rt))
            {
                rt = new WorkerRuntimeState();
                _workerStates[deviceKey] = rt;
            }
            return rt;
        }
    }

    private void ResetTransientRates()
    {
        lock (_lifecycleLock)
        {
            foreach (WorkerRuntimeState rt in _workerStates.Values)
            {
                rt.ActualHps = 0;
            }
        }
    }

    private void PublishTelemetry()
    {
        CoordinatorTelemetry snap = BuildTelemetrySnapshot();
        LatestTelemetry = snap;
        TelemetryUpdated?.Invoke(this, snap);
    }

    private CoordinatorTelemetry BuildTelemetrySnapshot()
    {
        bool paused = IsCurrentlyPaused();
        double slider = paused ? 0.0 : Volatile.Read(ref _sliderPosition);
        double totalActual = 0.0;
        long totalTarget = 0;
        double totalPeak = 0.0;
        bool allFull = true;
        bool anyEnabled = false;
        bool isFullSpeed = slider >= 1.0 && !paused;

        var deviceSnaps = new List<GpuTelemetrySnapshot>(_devices.Count);
        lock (_lifecycleLock)
        {
            foreach (GpuDeviceInfo dev in _devices)
            {
                bool enabled = _enabledDevices.TryGetValue(dev.DeviceKey, out bool en) && en;
                _workerStates.TryGetValue(dev.DeviceKey, out WorkerRuntimeState? rt);
                _calibrations.TryGetValue(dev.DeviceKey, out CalibrationRecord? cal);

                bool fullCal = CalibrationValidator.IsValidForDevice(cal, dev, ExpectedKernelFor(dev), requireFull: true);
                bool provCal = CalibrationValidator.IsValidForDevice(cal, dev, ExpectedKernelFor(dev), requireFull: false);
                double peak = provCal ? cal!.PeakHashesPerSecond : 0.0;
                double actual = rt?.ActualHps ?? 0.0;

                RateDirective dir = enabled ? RateControl.FromSlider(slider, peak) : new RateDirective(RateMode.Paused, 0);
                long devTarget = dir.Mode == RateMode.Capped
                    ? dir.TargetHashesPerSecond
                    : (dir.Mode == RateMode.FullSpeed ? (long)Math.Round(peak) : 0L);

                if (enabled)
                {
                    anyEnabled = true;
                    totalActual += actual;
                    totalPeak += peak;
                    totalTarget += devTarget;
                    if (!fullCal)
                    {
                        allFull = false;
                    }
                }

                deviceSnaps.Add(new GpuTelemetrySnapshot(
                    DeviceKey: dev.DeviceKey,
                    Name: dev.Name,
                    BackendName: dev.BackendName,
                    IsDiscrete: dev.IsDiscrete,
                    IsEnabled: enabled,
                    ActualHps: actual,
                    TargetHps: devTarget,
                    PeakHps: peak,
                    IsFullCalibrated: fullCal,
                    IsProvisionalCalibrated: provCal,
                    Error: rt?.Error));
            }
        }

        ulong sessionChecked = (ulong)Math.Max(0L, Interlocked.Read(ref _sessionChecked));
        ulong totalChecked = _persistedTotalChecked + sessionChecked;
        double probability = _activePattern?.ComputeCumulativeProbability(totalChecked) ?? 0.0;
        double etaRate = _state is CoordinatorState.Running or CoordinatorState.Calibrating
            ? (totalActual > 0.0 ? totalActual : (isFullSpeed ? totalPeak : totalTarget))
            : (isFullSpeed ? totalPeak : (totalTarget > 0 ? totalTarget : totalPeak));
        TimeSpan? eta = (_state == CoordinatorState.Paused || etaRate <= 0.0)
            ? null
            : _activePattern?.EstimateMeanWait(etaRate);

        return new CoordinatorTelemetry(
            State: _state,
            CalibrationRemainingSeconds: _calibrationRemainingSeconds,
            TotalActualHps: totalActual,
            TotalTargetHps: totalTarget,
            IsFullSpeed: isFullSpeed,
            TotalPeakHps: totalPeak,
            AllEnabledFullyCalibrated: anyEnabled && allFull,
            SessionChecked: sessionChecked,
            TotalChecked: totalChecked,
            TotalMatchesCount: Volatile.Read(ref _totalMatchesCount),
            CumulativeProbability: probability,
            EstimatedMeanWait: eta,
            ErrorMessage: _errorMessage,
            Devices: deviceSnaps);
    }

    private static Ed25519KeyPair[] GenerateKeyGroup(int count)
    {
        var group = new Ed25519KeyPair[count];
        for (int i = 0; i < count; i++)
        {
            group[i] = Ed25519KeyGenerator.Generate();
        }
        return group;
    }

    private static void ZeroKeyGroup(Ed25519KeyPair[] keys)
    {
        foreach (Ed25519KeyPair pair in keys)
        {
            if (pair.PrivateSeed is not null)
            {
                CryptographicOperations.ZeroMemory(pair.PrivateSeed);
            }
        }
    }

    private static uint CurrentTimestampUpperBound()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (uint)Math.Clamp(now + 1L, 2L, uint.MaxValue);
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

    private sealed class WorkerRuntimeState
    {
        public double ActualHps;
        public string? Error;
    }
}
