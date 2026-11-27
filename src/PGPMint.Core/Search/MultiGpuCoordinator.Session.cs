using System.Diagnostics;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    private sealed class SearchSessionException(SearchFailure failure, string message, Exception? innerException = null)
        : InvalidOperationException(message, innerException)
    {
        public SearchFailure Failure { get; } = failure;
    }

    private sealed class SessionDevice(IGpuSearchDevice device)
    {
        public IGpuSearchDevice? Device { get; set; } = device;
        public bool RecoveryAttempted { get; set; }
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
                _state = CoordinatorState.Idle;
                Volatile.Write(ref _calibrationRemainingSeconds, 0.0);
            }
            else
            {
                _state = CoordinatorState.Stopping;
            }
        }

        PublishTelemetry();
        if (runningTask is null)
        {
            return;
        }

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
            _progressLedger.Clear();
            Interlocked.Exchange(ref _sessionChecked, 0);
            _stateStore.Update(s =>
            {
                s.LastPatternMask = pattern.CanonicalMask;
                s.TotalCheckedCandidates = 0;
                s.ProgressLedger.Clear();
            });
        }
    }

    private async Task RunMultiGpuSessionAsync(
        IReadOnlyList<GpuDeviceInfo> enabled,
        KeyIdPattern pattern,
        CancellationTokenSource sessionCts)
    {
        CancellationToken ct = sessionCts.Token;
        var devices = new List<SessionDevice>(enabled.Count);
        using var failureCts = new CancellationTokenSource();
        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct, failureCts.Token);
        CancellationToken workerToken = workerCts.Token;
        CancellationToken failureToken = failureCts.Token;
        using var telemetryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task? telemetryLoop = null;
        long calStartTimestamp = Stopwatch.GetTimestamp();
        int calClockActive = 0;

        try
        {
            if (!CheckVaultCapacity())
            {
                PublishTelemetry();
                return;
            }

            foreach (GpuDeviceInfo info in enabled)
            {
                IGpuSearchDevice created = _backend.CreateDevice(info);
                devices.Add(new SessionDevice(created));
                ReconcileActualDeviceInfo(created);
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
            while (!workerToken.IsCancellationRequested && NeedsFullCalibration())
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
                    await Task.Delay(40, workerToken).ConfigureAwait(false);
                    continue;
                }

                pausedNotified = false;
                Volatile.Write(ref calStartTimestamp, Stopwatch.GetTimestamp());
                Volatile.Write(ref _calibrationRemainingSeconds, _options.FullCalibrationDuration.TotalSeconds);
                Volatile.Write(ref calClockActive, 1);
                _state = CoordinatorState.Calibrating;
                PublishTelemetry();

                CalibrationRecord?[] records;
                using (WindowsComputeQoS.AcquireProcessScope())
                {
                    var calTasks = devices.Select(slot => WindowsComputeQoS.RunDedicatedWorker(() => RunOnDevice(slot, (device, _) =>
                        RunZeroWasteCalibrationOnOpenDevice(device, pattern, workerToken, failureToken)),
                        "PGPMint-GpuWorker",
                        aboveNormalPriority: true)).ToArray();

                    records = await Task.WhenAll(calTasks).ConfigureAwait(false);
                }
                Volatile.Write(ref calClockActive, 0);

                if (workerToken.IsCancellationRequested)
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

            if (!workerToken.IsCancellationRequested)
            {
                _state = IsCurrentlyPaused() ? CoordinatorState.Paused : CoordinatorState.Running;
                PublishTelemetry();

                // Phase 2: Continuous multi-GPU search (dynamic median updates only while in FullSpeed mode)
                var workerTasks = devices.Select(slot => WindowsComputeQoS.RunDedicatedWorker(() => RunOnDevice(slot, (device, recovered) =>
                {
                    if (recovered && GetValidatedDeviceBaselineHps(device.Info) <= 0)
                    {
                        using IDisposable recoveryQoS = WindowsComputeQoS.AcquireProcessScope();
                        CalibrationRecord? record = RunZeroWasteCalibrationOnOpenDevice(device, pattern, workerToken, failureToken);
                        if (record is not null)
                        {
                            SaveCalibrationRecord(record);
                        }
                    }

                    RunSearchWorkerLoop(device, pattern, workerToken, failureToken);
                    return true;
                }), "PGPMint-GpuWorker", aboveNormalPriority: true)).ToArray();

                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean user stop
        }
        catch (Exception ex)
        {
            if (ex is not VaultFullException)
            {
                _errorMessage = ex.Message;
                _searchFailure = ex is SearchSessionException failure ? failure.Failure : SearchFailure.None;
                _vaultIsFull = false;
            }
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

            foreach (SessionDevice slot in devices)
            {
                try { slot.Device?.Dispose(); } catch { }
            }

            FlushSessionCheckedIntoPersisted();
            ResetTransientRates();
            bool publishIdle = false;
            lock (_lifecycleLock)
            {
                _pausedExplicitly = false;
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

        T RunOnDevice<T>(SessionDevice slot, Func<IGpuSearchDevice, bool, T> run)
        {
            bool recovered = false;
            try
            {
                while (true)
                {
                    workerToken.ThrowIfCancellationRequested();
                    IGpuSearchDevice device = slot.Device!;
                    try
                    {
                        return run(device, recovered);
                    }
                    catch (GpuDeviceLostException)
                    {
                        workerToken.ThrowIfCancellationRequested();
                        InvalidateCalibration(device.Info, pattern);
                        if (slot.RecoveryAttempted)
                        {
                            throw;
                        }

                        slot.RecoveryAttempted = true;
                        slot.Device = null;
                        device.Dispose();
                        workerToken.ThrowIfCancellationRequested();
                        IGpuSearchDevice replacement = _backend.CreateDevice(device.Info);
                        slot.Device = replacement;
                        ReconcileActualDeviceInfo(replacement);
                        recovered = true;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !workerToken.IsCancellationRequested)
            {
                failureCts.Cancel();
                throw;
            }
        }
    }
}
