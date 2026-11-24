namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    private void SaveCalibrationRecord(CalibrationRecord record)
    {
        string profileKey = CalibrationValidator.ProfileKey(record.DeviceKey, record.WorkloadIdentity!);
        lock (_lifecycleLock)
        {
            _calibrations[profileKey] = record;
        }

        _stateStore.Update(s => s.Calibrations[profileKey] = record);
    }

    private void InvalidateCalibration(GpuDeviceInfo device, KeyIdPattern pattern)
    {
        string profileKey = CalibrationValidator.ProfileKey(device.DeviceKey, CalibrationValidator.WorkloadIdentity(device, pattern));
        WorkerRuntimeState runtime = GetOrCreateRuntimeState(device.DeviceKey);
        lock (_lifecycleLock)
        {
            _calibrations.Remove(profileKey);
            Volatile.Write(ref runtime.ActualHps, 0.0);
            runtime.BatchSizer = null;
        }

        _stateStore.Update(s => s.Calibrations.Remove(profileKey));
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
                        !string.Equals(list[i].KernelVersion, device.KernelVersion, StringComparison.Ordinal) ||
                        !string.Equals(list[i].BackendDriverVersion, device.Info.BackendDriverVersion, StringComparison.Ordinal) ||
                        list[i].KernelConfiguration != device.Info.KernelConfiguration)
                    {
                        list[i] = list[i] with
                        {
                            BackendName = device.Info.BackendName,
                            KernelVersion = device.KernelVersion,
                            BackendDriverVersion = device.Info.BackendDriverVersion,
                            KernelConfiguration = device.Info.KernelConfiguration
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

    private CalibrationRecord? GetValidatedCalibrationLocked(GpuDeviceInfo device)
    {
        if (_activePattern is null)
        {
            return null;
        }

        string workload = CalibrationValidator.WorkloadIdentity(device, _activePattern);
        string key = CalibrationValidator.ProfileKey(device.DeviceKey, workload);
        return _calibrations.TryGetValue(key, out CalibrationRecord? record)
            && string.Equals(record.WorkloadIdentity, workload, StringComparison.Ordinal)
            && CalibrationValidator.IsValidForDevice(record, device, ExpectedKernelFor(device), requireFull: true)
            ? record
            : null;
    }

    private double GetValidatedDeviceBaselineHps(GpuDeviceInfo device)
    {
        lock (_lifecycleLock)
        {
            return CalibrationValidator.GetBaselineHps(GetValidatedCalibrationLocked(device));
        }
    }

    private double GetStoredDevicePeakHps(GpuDeviceInfo device)
    {
        lock (_lifecycleLock)
        {
            return GetValidatedCalibrationLocked(device)?.PeakHashesPerSecond ?? 0.0;
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
                rt.BatchSizer = null;
            }
        }
    }

    internal void PublishTelemetry(Func<CoordinatorTelemetry>? captureSnapshot = null)
    {
        CoordinatorTelemetry snap;
        lock (_lifecycleLock)
        {
            snap = captureSnapshot?.Invoke() ?? BuildTelemetrySnapshot();
            _latestTelemetry = snap;
        }
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
        SearchWorkMetrics sessionWork;
        ulong totalChecked;

        var deviceSnaps = new List<GpuTelemetrySnapshot>(_devices.Count);
        lock (_lifecycleLock)
        {
            foreach (GpuDeviceInfo dev in _devices)
            {
                bool enabled = _enabledDevices.TryGetValue(dev.DeviceKey, out bool en) && en;
                _workerStates.TryGetValue(dev.DeviceKey, out WorkerRuntimeState? rt);
                CalibrationRecord? cal = GetValidatedCalibrationLocked(dev);
                bool fullCal = cal is not null;
                double baseline = CalibrationValidator.GetBaselineHps(cal);
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
                    Error: rt?.Error)
                {
                    LastBatchWallMilliseconds = rt?.BatchSizer?.LastDispatchWallMilliseconds ?? 0.0
                });
            }

            sessionChecked = (ulong)Math.Max(0L, Interlocked.Read(ref _sessionChecked));
            totalChecked = _persistedTotalChecked + sessionChecked;
            sessionWork = _sessionWork;
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
            Devices: deviceSnaps)
        {
            SessionWork = sessionWork,
            VaultIsFull = _vaultIsFull,
            Failure = _searchFailure
        };
    }

    private sealed class WorkerRuntimeState
    {
        public double ActualHps;
        public volatile AdaptiveBatchSizer? BatchSizer;
        public string? Error;
    }
}
