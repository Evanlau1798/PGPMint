namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
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
        SearchWorkMetrics sessionWork;
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
            SessionWork = sessionWork
        };
    }

    private sealed class WorkerRuntimeState
    {
        public double ActualHps;
        public string? Error;
    }
}
