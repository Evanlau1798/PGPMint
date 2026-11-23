using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
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
    IReadOnlyList<GpuTelemetrySnapshot> Devices);

public sealed class CoordinatorOptions
{
    public TimeSpan FullCalibrationDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CalibrationWindow { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan DynamicMedianInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MinDynamicMedianSamples { get; init; } = 3;
    public TimeSpan TelemetryInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public string? ExpectedKernelVersionOverride { get; init; }
}

public sealed class MultiGpuCoordinator : IDisposable
{
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
    private double _sliderPosition = 1.0;
    private double _lastPositiveSliderPosition = 1.0;
    private volatile bool _autoPauseOnHit = true;
    private volatile bool _pausedExplicitly;
    private volatile bool _pausedByZeroSlider;
    private int _autoPauseTriggered;
    private int _rateEpoch;

    private ulong _persistedTotalChecked;
    private long _sessionChecked;
    private int _totalMatchesCount;

    private volatile CoordinatorState _state = CoordinatorState.Idle;
    private double _calibrationRemainingSeconds;
    private string? _errorMessage;

    private CancellationTokenSource? _activeOpCts;
    private Task? _activeOpTask;
    private bool _disposed;

    public IReadOnlyList<GpuDeviceInfo> Devices => _devices;
    public CoordinatorState CurrentState => _state;
    public double SliderPosition => Volatile.Read(ref _sliderPosition);
    public double FullCalibrationDurationSeconds => Math.Max(1.0, _options.FullCalibrationDuration.TotalSeconds);
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

    public void NotifyVaultCountChanged(int count)
    {
        Interlocked.Exchange(ref _totalMatchesCount, Math.Max(0, count));
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
            Interlocked.Exchange(ref _sessionChecked, 0);
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
                Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
                PublishTelemetry();
                return;
            }

            _state = CoordinatorState.Stopping;
        }

        PublishTelemetry();

        try
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS already disposed by completing session
            }

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
            ResetTransientRates();
            lock (_lifecycleLock)
            {
                _pausedExplicitly = false;
                _pausedByZeroSlider = false;
                Interlocked.Exchange(ref _autoPauseTriggered, 0);
                Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
                if (_activeOpTask is null && _state != CoordinatorState.Faulted)
                {
                    _state = CoordinatorState.Idle;
                }
            }
            PublishTelemetry();
        }
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
        int calClockActive = 0;

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
                    if (_state == CoordinatorState.Calibrating && Volatile.Read(ref calClockActive) == 1)
                    {
                        double elapsed = Stopwatch.GetElapsedTime(Volatile.Read(ref calStartTimestamp)).TotalSeconds;
                        Volatile.Write(ref _calibrationRemainingSeconds, Math.Max(0.0, _options.FullCalibrationDuration.TotalSeconds - elapsed));
                    }
                    else if (_state != CoordinatorState.Calibrating)
                    {
                        Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
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

            // Phase 1: Automatic 30s full-speed measurement on open devices when baseline is missing.
            // If interrupted by pause (e.g. AutoPauseOnHit on simple suffix), wait while paused and retry upon Resume.
            bool pausedNotified = false;
            while (!failureLinkedCts.IsCancellationRequested && NeedsFullCalibration())
            {
                if (IsCurrentlyPaused())
                {
                    Volatile.Write(ref calClockActive, 0);
                    if (!pausedNotified || _state != CoordinatorState.Paused)
                    {
                        _state = CoordinatorState.Paused;
                        Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
                        ResetTransientRates();
                        PublishTelemetry();
                        pausedNotified = true;
                    }
                    await Task.Delay(40, failureLinkedCts.Token).ConfigureAwait(false);
                    continue;
                }

                pausedNotified = false;
                Volatile.Write(ref calStartTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref _calibrationRemainingSeconds, _options.FullCalibrationDuration.TotalSeconds);
                Volatile.Write(ref calClockActive, 1);
                _state = CoordinatorState.Calibrating;
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
                Volatile.Write(ref calClockActive, 0);

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
                    Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
                    break;
                }
            }

            if (!failureLinkedCts.IsCancellationRequested)
            {
                _state = IsCurrentlyPaused() ? CoordinatorState.Paused : CoordinatorState.Running;
                PublishTelemetry();

                // Phase 2: Continuous multi-GPU search (dynamic median updates only while in FullSpeed mode)
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
            ResetTransientRates();
            bool publishIdle = false;
            lock (_lifecycleLock)
            {
                _pausedExplicitly = false;
                _pausedByZeroSlider = false;
                Interlocked.Exchange(ref _autoPauseTriggered, 0);
                Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
                if (ReferenceEquals(_activeOpCts, sessionCts))
                {
                    _activeOpCts = null;
                    _activeOpTask = null;
                }

                if (_state is not (CoordinatorState.Faulted or CoordinatorState.Stopping))
                {
                    _state = CoordinatorState.Idle;
                    publishIdle = true;
                }
            }

            sessionCts.Dispose();
            if (publishIdle)
            {
                PublishTelemetry();
            }
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
                    Volatile.Write(ref rt.ActualHps, hps);
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

            return BuildCalibrationRecordFromSamples(device, samples, priorPeakHps: 0.0);
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
        RateMode lastMode = RateMode.Paused;

        // Dynamic median tracking: active ONLY while slider is continuously at FullSpeed (100%).
        var fullSpeedSamples = new List<double>();
        long dynamicMedianStart = Stopwatch.GetTimestamp();
        int minMedianSamples = Math.Max(1, _options.MinDynamicMedianSamples);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int epochBefore = Volatile.Read(ref _rateEpoch);
                double slider = IsCurrentlyPaused() ? 0.0 : Volatile.Read(ref _sliderPosition);
                double deviceBaseline = GetValidatedDeviceBaselineHps(device.Info);
                RateDirective directive = RateControl.FromSlider(slider, deviceBaseline);
                int epochAfter = Volatile.Read(ref _rateEpoch);

                double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                if (epochBefore != seenRateEpoch || epochAfter != epochBefore || directive.Mode != lastMode)
                {
                    seenRateEpoch = epochAfter;
                    lastMode = directive.Mode;
                    readyTimeSec = nowSec;
                    sampleStart = Stopwatch.GetTimestamp();
                    sampleChecked = 0;
                    fullSpeedSamples.Clear();
                    dynamicMedianStart = Stopwatch.GetTimestamp();

                    if (directive.Mode == RateMode.Paused)
                    {
                        Volatile.Write(ref rt.ActualHps, 0.0);
                    }
                }
                else
                {
                    if (directive.Mode != RateMode.FullSpeed)
                    {
                        fullSpeedSamples.Clear();
                        dynamicMedianStart = Stopwatch.GetTimestamp();
                    }

                    double sampleElapsed = Stopwatch.GetElapsedTime(sampleStart).TotalSeconds;
                    if (sampleElapsed >= _options.CalibrationWindow.TotalSeconds)
                    {
                        double hps = directive.Mode == RateMode.Paused ? 0.0 : sampleChecked / sampleElapsed;
                        Volatile.Write(ref rt.ActualHps, hps);
                        sampleStart = Stopwatch.GetTimestamp();
                        sampleChecked = 0;

                        if (directive.Mode == RateMode.FullSpeed && hps > 0.0)
                        {
                            fullSpeedSamples.Add(hps);
                            if (Stopwatch.GetElapsedTime(dynamicMedianStart) >= _options.DynamicMedianInterval &&
                                fullSpeedSamples.Count >= minMedianSamples)
                            {
                                double priorPeak = GetStoredDevicePeakHps(device.Info.DeviceKey);
                                CalibrationRecord updated = BuildCalibrationRecordFromSamples(device, fullSpeedSamples, priorPeak);
                                SaveCalibrationRecord(updated);
                                fullSpeedSamples.Clear();
                                dynamicMedianStart = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                }

                if (directive.Mode == RateMode.Paused)
                {
                    Volatile.Write(ref rt.ActualHps, 0.0);
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
            Volatile.Write(ref rt.ActualHps, 0.0);
            ZeroKeyGroup(keys);
        }
    }

    private static CalibrationRecord BuildCalibrationRecordFromSamples(
        IGpuSearchDevice device,
        List<double> samples,
        double priorPeakHps)
    {
        var sorted = new List<double>(samples);
        sorted.Sort();
        double peak = Math.Max(priorPeakHps, sorted[^1]);
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);

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

    private void HandleVerifiedHits(
        GpuDeviceInfo deviceInfo,
        Ed25519KeyPair[] keys,
        IReadOnlyList<uint>[] laneHits,
        KeyIdPattern pattern,
        bool allowAutoPause)
    {
        bool shouldAutoPause = allowAutoPause && _autoPauseOnHit;

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

                if (shouldAutoPause)
                {
                    if (Interlocked.Exchange(ref _autoPauseTriggered, 1) != 0)
                    {
                        // Another worker already recorded the hit and triggered auto-stop.
                        return;
                    }

                    // Signal all other GPU workers to stop dispatching immediately before disk I/O.
                    Interlocked.Increment(ref _rateEpoch);
                    try
                    {
                        _activeOpCts?.Cancel();
                    }
                    catch
                    {
                        // Ignore if CTS is already cancelling/disposed
                    }
                }

                MatchedKeyRecord record = _vaultStore.SaveMatch(
                    keys[lane].PrivateSeed,
                    keys[lane].PublicKey,
                    ts,
                    fullFingerprint,
                    pattern.CanonicalMask,
                    deviceInfo.Name,
                    out int storedCount);

                Interlocked.Exchange(ref _totalMatchesCount, storedCount);

                MatchFound?.Invoke(this, record);

                // Cap to at most 1 saved match per GPU batch to prevent simple suffixes from flooding storage/UI.
                return;
            }
        }
    }

    private bool IsCurrentlyPaused() =>
        _pausedExplicitly || _pausedByZeroSlider || Volatile.Read(ref _autoPauseTriggered) != 0;

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
        ulong total;
        string mask;
        lock (_lifecycleLock)
        {
            long sessionDelta = Interlocked.Read(ref _sessionChecked);
            if (sessionDelta <= 0)
            {
                return;
            }

            total = _persistedTotalChecked + (ulong)sessionDelta;
            mask = _persistedPatternMask;
        }

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
        ulong total;
        string mask;
        lock (_lifecycleLock)
        {
            long delta = Interlocked.Exchange(ref _sessionChecked, 0);
            if (delta <= 0)
            {
                return;
            }

            _persistedTotalChecked += (ulong)delta;
            total = _persistedTotalChecked;
            mask = _persistedPatternMask;
        }

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

    private double GetValidatedDeviceBaselineHps(GpuDeviceInfo device)
    {
        lock (_lifecycleLock)
        {
            if (_calibrations.TryGetValue(device.DeviceKey, out CalibrationRecord? rec) &&
                CalibrationValidator.IsValidForDevice(rec, device, ExpectedKernelFor(device), requireFull: true))
            {
                return CalibrationValidator.GetBaselineHps(rec);
            }
            return 0.0;
        }
    }

    private double GetStoredDevicePeakHps(string deviceKey)
    {
        lock (_lifecycleLock)
        {
            return _calibrations.TryGetValue(deviceKey, out CalibrationRecord? rec)
                ? rec.PeakHashesPerSecond
                : 0.0;
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
                Volatile.Write(ref rt.ActualHps, 0.0);
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
        double totalBaseline = 0.0;
        bool allFull = true;
        bool anyEnabled = false;
        bool isFullSpeed = (_state == CoordinatorState.Calibrating) || (slider >= 1.0 && !paused);
        ulong sessionChecked;
        ulong totalChecked;

        var deviceSnaps = new List<GpuTelemetrySnapshot>(_devices.Count);
        lock (_lifecycleLock)
        {
            foreach (GpuDeviceInfo dev in _devices)
            {
                bool enabled = _enabledDevices.TryGetValue(dev.DeviceKey, out bool en) && en;
                _workerStates.TryGetValue(dev.DeviceKey, out WorkerRuntimeState? rt);
                _calibrations.TryGetValue(dev.DeviceKey, out CalibrationRecord? cal);

                bool fullCal = CalibrationValidator.IsValidForDevice(cal, dev, ExpectedKernelFor(dev), requireFull: true);
                double baseline = fullCal ? CalibrationValidator.GetBaselineHps(cal) : 0.0;
                double actual = rt is not null ? Volatile.Read(ref rt.ActualHps) : 0.0;

                RateDirective dir = enabled ? RateControl.FromSlider(slider, baseline) : new RateDirective(RateMode.Paused, 0);
                long devTarget = dir.Mode == RateMode.Capped
                    ? dir.TargetHashesPerSecond
                    : (dir.Mode == RateMode.FullSpeed ? (long)Math.Round(baseline) : 0L);

                if (enabled)
                {
                    anyEnabled = true;
                    totalActual += actual;
                    totalBaseline += baseline;
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
                    BaselineHps: baseline,
                    IsFullCalibrated: fullCal,
                    Error: rt?.Error));
            }

            sessionChecked = (ulong)Math.Max(0L, Interlocked.Read(ref _sessionChecked));
            totalChecked = _persistedTotalChecked + sessionChecked;
        }

        double probability = _activePattern?.ComputeCumulativeProbability(totalChecked) ?? 0.0;

        // Compute ETA strictly from live measured speed while active (no speculative estimates).
        TimeSpan? eta = (_state is CoordinatorState.Running or CoordinatorState.Calibrating && totalActual > 0.0)
            ? _activePattern?.EstimateMeanWait(totalActual)
            : null;

        return new CoordinatorTelemetry(
            State: _state,
            CalibrationRemainingSeconds: Volatile.Read(ref _calibrationRemainingSeconds),
            TotalActualHps: totalActual,
            TotalTargetHps: totalTarget,
            IsFullSpeed: isFullSpeed,
            TotalBaselineHps: totalBaseline,
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
