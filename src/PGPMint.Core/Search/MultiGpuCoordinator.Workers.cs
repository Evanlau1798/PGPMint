using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    internal const uint MinimumCreationTimestamp = 946_684_800u; // 2000-01-01T00:00:00Z
    private const double ContendedOtherGpuUtilThreshold = 10.0;

    private void RunSearchWorkerLoop(
        IGpuSearchDevice device,
        KeyIdPattern pattern,
        CancellationToken ct,
        CancellationToken failureToken)
    {
        WorkerRuntimeState rt = GetOrCreateRuntimeState(device.Info.DeviceKey);
        rt.Error = null;
        var batchSizer = new AdaptiveBatchSizer(device.MaxCandidatesPerBatch, device.KeysPerBatch, pattern.FixedHexLength);
        rt.BatchSizer = batchSizer;

        using var keyPool = new KeySchedulePool(device.KeysPerBatch);
        PreparedKeyGroup initialGroup = keyPool.TakeNext(ct);
        Ed25519KeyPair[] keys = initialGroup.Keys;
        byte[][] pubKeys = initialGroup.PublicKeys;
        uint end = CurrentTimestampUpperBound();
        uint cursor = InitialTimestampCursor(end);

        double readyTimeSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        long sampleStart = Stopwatch.GetTimestamp();
        long sampleChecked = 0;
        int seenRateEpoch = -1;
        RateMode lastMode = RateMode.Paused;

        var yieldController = new OverloadYieldController();
        var thermalGuard = new ThermalGuardController();
        long lastSensorPollTs = Stopwatch.GetTimestamp();
        double sensorPollIntervalSec = Math.Max(0.05, _options.SensorPollInterval.TotalSeconds);
        bool sensorPrimed = false;

        // Dynamic median tracking: active ONLY while tier is continuously at Max (FullSpeed)
        // and no other process is contending for the GPU.
        var fullSpeedSamples = new List<double>();
        var fullSpeedWattSamples = new List<double>();
        long dynamicMedianStart = Stopwatch.GetTimestamp();
        int minMedianSamples = Math.Max(1, _options.MinDynamicMedianSamples);
        bool lastSampleContended = false;
        bool windowHadGpuContention = false;
        bool lastPublishedHpsContended = false;

        IDisposable? qosScope = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int epochBefore = Volatile.Read(ref _rateEpoch);
                bool userPaused = IsCurrentlyPaused();
                PowerTier activeTier = SelectedTier;
                CalibrationRecord? calRecord = GetValidatedDeviceCalibration(device.Info);
                double deviceBaseline = CalibrationValidator.GetBaselineHps(calRecord);
                int epochAfter = Volatile.Read(ref _rateEpoch);

                if (epochAfter != epochBefore)
                {
                    continue;
                }

                bool rateEpochChanged = epochBefore != seenRateEpoch;
                if (rateEpochChanged)
                {
                    yieldController.Reset();
                    thermalGuard.Reset();
                    Volatile.Write(ref rt.IsOverloadYielding, 0);
                    Volatile.Write(ref rt.IsThermalGuardActive, 0);
                    if (!userPaused)
                    {
                        // Re-anchor sensor baseline on tier switch without consuming the post-switch
                        // grace window using pre-switch elapsed time or residual watts.
                        GpuSensorSample prime = _telemetrySensor.Sample(device.Info);
                        UpdateRuntimeSensor(rt, prime);
                        thermalGuard.Update(MaxGpuTemperatureCelsius, prime, sensorPollIntervalSec);
                        Volatile.Write(ref rt.IsThermalGuardActive, thermalGuard.IsThrottling ? 1 : 0);
                        lastSampleContended = prime.HasGpuUtil && prime.OtherProcessGpuUtil >= ContendedOtherGpuUtilThreshold;
                        Volatile.Write(ref rt.IsGpuContended, (lastSampleContended || lastPublishedHpsContended) ? 1 : 0);
                        sensorPrimed = true;
                        lastSensorPollTs = Stopwatch.GetTimestamp();
                    }
                }

                // Poll GPU power & engine utilization sensor periodically (for live MH/W badge + overload yield)
                double sensorDtSec = Stopwatch.GetElapsedTime(lastSensorPollTs).TotalSeconds;
                if (!userPaused && !rateEpochChanged && (!sensorPrimed || sensorDtSec >= sensorPollIntervalSec))
                {
                    GpuSensorSample sensor = _telemetrySensor.Sample(device.Info);
                    UpdateRuntimeSensor(rt, sensor);
                    bool contended = sensor.HasGpuUtil && sensor.OtherProcessGpuUtil >= ContendedOtherGpuUtilThreshold;
                    lastSampleContended = contended;
                    Volatile.Write(ref rt.IsGpuContended, (lastSampleContended || lastPublishedHpsContended) ? 1 : 0);

                    thermalGuard.Update(MaxGpuTemperatureCelsius, sensor, sensorDtSec);
                    Volatile.Write(ref rt.IsThermalGuardActive, thermalGuard.IsThrottling ? 1 : 0);

                    if (contended)
                    {
                        windowHadGpuContention = true;
                        fullSpeedSamples.Clear();
                        fullSpeedWattSamples.Clear();
                        dynamicMedianStart = Stopwatch.GetTimestamp();
                    }
                    else if (activeTier == PowerTier.Max && !thermalGuard.IsThrottling && sensor.HasPowerWatts && sensor.PowerWatts > 0.0)
                    {
                        fullSpeedWattSamples.Add(sensor.PowerWatts);
                    }

                    if (sensorPrimed)
                    {
                        yieldController.Update(activeTier, calRecord?.EfficiencyProfile, sensor, sensorDtSec);
                    }
                    else
                    {
                        sensorPrimed = true;
                        if (activeTier == PowerTier.Max)
                        {
                            yieldController.Reset();
                        }
                    }

                    Volatile.Write(ref rt.IsOverloadYielding, yieldController.IsYielding ? 1 : 0);
                    lastSensorPollTs = Stopwatch.GetTimestamp();
                }

                RateDirective directive = userPaused
                    ? new RateDirective(RateMode.Paused, 0L)
                    : RateControl.FromTier(activeTier, deviceBaseline, calRecord?.EfficiencyProfile, yieldController.YieldScale, thermalGuard.ThermalScale);

                double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                if (rateEpochChanged || directive.Mode != lastMode)
                {
                    seenRateEpoch = epochBefore;
                    lastMode = directive.Mode;
                    readyTimeSec = nowSec;
                    sampleStart = Stopwatch.GetTimestamp();
                    sampleChecked = 0;
                    fullSpeedSamples.Clear();
                    fullSpeedWattSamples.Clear();
                    dynamicMedianStart = Stopwatch.GetTimestamp();
                    windowHadGpuContention = lastSampleContended;

                    if (directive.Mode == RateMode.Paused)
                    {
                        Volatile.Write(ref rt.ActualHps, 0.0);
                        lastPublishedHpsContended = false;
                        Volatile.Write(ref rt.IsGpuContended, lastSampleContended ? 1 : 0);
                    }
                }
                else
                {
                    if (directive.Mode != RateMode.FullSpeed)
                    {
                        fullSpeedSamples.Clear();
                        fullSpeedWattSamples.Clear();
                        dynamicMedianStart = Stopwatch.GetTimestamp();
                    }

                    double sampleElapsed = Stopwatch.GetElapsedTime(sampleStart).TotalSeconds;
                    if (sampleElapsed >= _options.CalibrationWindow.TotalSeconds)
                    {
                        double hps = directive.Mode == RateMode.Paused ? 0.0 : sampleChecked / sampleElapsed;
                        Volatile.Write(ref rt.ActualHps, hps);
                        sampleStart = Stopwatch.GetTimestamp();
                        sampleChecked = 0;

                        bool thisWindowContended = windowHadGpuContention;
                        windowHadGpuContention = lastSampleContended;
                        lastPublishedHpsContended = directive.Mode != RateMode.Paused && thisWindowContended;
                        Volatile.Write(ref rt.IsGpuContended, (lastSampleContended || lastPublishedHpsContended) ? 1 : 0);

                        if (directive.Mode == RateMode.FullSpeed && hps > 0.0 && !thisWindowContended && !lastSampleContended)
                        {
                            fullSpeedSamples.Add(hps);
                            if (Stopwatch.GetElapsedTime(dynamicMedianStart) >= _options.DynamicMedianInterval &&
                                fullSpeedSamples.Count >= minMedianSamples)
                            {
                                CalibrationRecord? current = GetValidatedDeviceCalibration(device.Info);
                                double priorPeak = current?.PeakHashesPerSecond ?? 0.0;
                                EfficiencyProfile? preservedProfile = current?.EfficiencyProfile;
                                if (preservedProfile is { HasPowerSensor: true } && fullSpeedWattSamples.Count >= minMedianSamples)
                                {
                                    double medianWatts = ComputeMedian(fullSpeedWattSamples);
                                    if (medianWatts > 0.0)
                                    {
                                        preservedProfile = preservedProfile with { FullLoadWatts = medianWatts };
                                    }
                                }

                                CalibrationRecord updated = BuildCalibrationRecordFromSamples(
                                    device,
                                    pattern,
                                    fullSpeedSamples,
                                    priorPeak,
                                    preservedProfile);
                                SaveCalibrationRecord(updated);
                                fullSpeedSamples.Clear();
                                fullSpeedWattSamples.Clear();
                                dynamicMedianStart = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                }

                if (userPaused)
                {
                    qosScope?.Dispose();
                    qosScope = null;
                    lastSampleContended = false;
                    windowHadGpuContention = false;
                    lastPublishedHpsContended = false;
                    sensorPrimed = false;
                    thermalGuard.Reset();
                    lastSensorPollTs = Stopwatch.GetTimestamp();
                    Volatile.Write(ref rt.ActualHps, 0.0);
                    Volatile.Write(ref rt.IsOverloadYielding, 0);
                    Volatile.Write(ref rt.IsGpuContended, 0);
                    Volatile.Write(ref rt.IsThermalGuardActive, 0);
                    Thread.Sleep(25);
                    continue;
                }

                if (directive.Mode == RateMode.Paused)
                {
                    // Overload yield to zero: keep worker awake polling sensors until other GPU workload clears
                    Volatile.Write(ref rt.ActualHps, 0.0);
                    Thread.Sleep(40);
                    continue;
                }

                qosScope ??= WindowsComputeQoS.AcquireProcessScope();

                if (nowSec < readyTimeSec)
                {
                    int sleepMs = (int)Math.Clamp((readyTimeSec - nowSec) * 1000.0, 1.0, 40.0);
                    Thread.Sleep(sleepMs);
                    continue;
                }

                if (cursor >= end)
                {
                    RotateKeyGroup(keyPool, ref keys, ref pubKeys, ref end, ref cursor, ct);
                }

                uint stamps = RateControl.ComputeBatchTimestamps(
                    batchSizer.NextCandidateLimit,
                    device.KeysPerBatch,
                    directive,
                    end - cursor);

                long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, batchSizer, ct, failureToken, out bool retired);
                sampleChecked += batchCandidates;
                cursor = AdvanceKeyGroupCursor(keys, cursor, stamps, end, retired);

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
            qosScope?.Dispose();
            ResetWorkerRuntime(rt);
            ZeroKeyGroup(keys);
        }
    }

    private static void RotateKeyGroup(
        KeySchedulePool keyPool,
        ref Ed25519KeyPair[] keys,
        ref byte[][] pubKeys,
        ref uint end,
        ref uint cursor,
        CancellationToken ct)
    {
        ZeroKeyGroup(keys);
        keys = Array.Empty<Ed25519KeyPair>();
        PreparedKeyGroup nextGroup = keyPool.TakeNext(ct);
        keys = nextGroup.Keys;
        pubKeys = nextGroup.PublicKeys;
        end = CurrentTimestampUpperBound();
        cursor = InitialTimestampCursor(end);
    }

    private static void UpdateRuntimeSensor(WorkerRuntimeState rt, GpuSensorSample sensor)
    {
        if (sensor.HasPowerWatts && sensor.PowerWatts > 0.0)
        {
            Volatile.Write(ref rt.PowerWatts, sensor.PowerWatts);
            Volatile.Write(ref rt.PowerLimitWatts, sensor.PowerLimitWatts);
            Volatile.Write(ref rt.HasPowerSensor, 1);
        }

        if (sensor.HasGpuUtil)
        {
            Volatile.Write(ref rt.GpuUtilPercent, Math.Clamp(sensor.TotalGpuUtil, 0.0, 100.0));
            Volatile.Write(ref rt.HasGpuUtilSensor, 1);
        }

        if (sensor.HasTemperature && sensor.TemperatureCelsius > 0.0)
        {
            Volatile.Write(ref rt.TemperatureCelsius, sensor.TemperatureCelsius);
            Volatile.Write(ref rt.HasTemperatureSensor, 1);
        }

        Volatile.Write(ref rt.IsThermalThrottled, sensor.IsThermalThrottled ? 1 : 0);
    }

    private static double ComputeMedian(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);
    }

    private static CalibrationRecord BuildCalibrationRecordFromSamples(
        IGpuSearchDevice device,
        KeyIdPattern pattern,
        List<double> samples,
        double priorPeakHps,
        EfficiencyProfile? existingProfile)
    {
        var sorted = new List<double>(samples);
        sorted.Sort();
        double peak = Math.Max(priorPeakHps, sorted[^1]);
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);

        EfficiencyProfile profile = existingProfile ?? EfficiencyProfile.CreateFallback(median);

        return new CalibrationRecord(
            DeviceKey: device.Info.DeviceKey,
            DeviceName: device.Info.Name,
            DriverVersion: device.Info.DriverVersion,
            KernelVersion: device.KernelVersion,
            PeakHashesPerSecond: peak,
            MedianHashesPerSecond: median,
            IsFullCalibration: true,
            CalibratedAtUtc: DateTimeOffset.UtcNow,
            WorkloadIdentity: CalibrationValidator.WorkloadIdentity(device.Info, pattern),
            EfficiencyProfile: profile);
    }

    private bool IsCurrentlyPaused() =>
        _pausedExplicitly || Volatile.Read(ref _autoPauseTriggered) != 0;

    private long ScanSearchBatch(
        IGpuSearchDevice device,
        Ed25519KeyPair[] keys,
        IReadOnlyList<byte[]> publicKeys,
        uint start,
        uint count,
        KeyIdPattern pattern,
        AdaptiveBatchSizer batchSizer,
        CancellationToken cancellationToken,
        CancellationToken failureToken,
        out bool retired)
    {
        retired = false;
        long batchStarted = Stopwatch.GetTimestamp();
        SearchBatchResult batch = device.ScanBatch(publicKeys, start, count, pattern,
            resultMode: SearchResultMode.FindOne, cancellationToken: cancellationToken);
        long returnedHits = batch.ReturnedHits;
        if (batch.WasCancelled && !cancellationToken.IsCancellationRequested)
        {
            throw new SearchSessionException(SearchFailure.IncompleteResults, $"Unexpected batch cancellation on '{device.Info.Name}'.");
        }

        if (!batch.WasCancelled && (!batch.ResultsComplete || !batch.HashRangeFullyProcessed ||
            returnedHits > 1 || batch.Hits.Length != keys.Length ||
            batch.Hits.Any(lane => lane.Count > 0 && (lane[0] < start || (ulong)lane[0] >= (ulong)start + count))))
        {
            throw new SearchSessionException(SearchFailure.IncompleteResults, $"Incomplete or invalid GPU search result on '{device.Info.Name}'.");
        }

        batchSizer.Observe((uint)(batch.UniqueCandidatesCompleted / device.KeysPerBatch),
            batch.DispatchWallMilliseconds ?? Stopwatch.GetElapsedTime(batchStarted).TotalMilliseconds);
        lock (_lifecycleLock)
        {
            Interlocked.Add(ref _sessionChecked, batch.UniqueCandidatesCompleted);
            _sessionWork = _sessionWork with
            {
                UniqueCandidatesCompleted = _sessionWork.UniqueCandidatesCompleted + batch.UniqueCandidatesCompleted,
                HashEvaluations = _sessionWork.HashEvaluations + batch.HashEvaluations,
                ReturnedHits = _sessionWork.ReturnedHits + returnedHits,
                ReportedRescanEvaluations = _sessionWork.RescanEvaluations + batch.RescanEvaluations
            };
        }

        // A user Stop lets completed readback reach the vault. A worker failure prevents new saves.
        if (!batch.WasCancelled && !failureToken.IsCancellationRequested && Volatile.Read(ref _autoPauseTriggered) == 0)
        {
            retired = HandleVerifiedHits(device.Info, keys, batch.Hits, pattern, allowAutoPause: true, failureToken);
        }

        return batch.UniqueCandidatesCompleted;
    }

    private static uint AdvanceKeyGroupCursor(Ed25519KeyPair[] keys, uint cursor, uint count, uint end, bool retired)
    {
        if (!retired)
        {
            return cursor + count;
        }

        ZeroKeyGroup(keys);
        return end;
    }

    private static uint InitialTimestampCursor(uint upperBound) =>
        upperBound > MinimumCreationTimestamp ? MinimumCreationTimestamp : 1u;

    private static void ZeroKeyGroup(Ed25519KeyPair[] keys) =>
        KeySchedulePool.ZeroKeys(keys);

    private static uint CurrentTimestampUpperBound()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (uint)Math.Clamp(now + 1L, 2L, uint.MaxValue);
    }
}
