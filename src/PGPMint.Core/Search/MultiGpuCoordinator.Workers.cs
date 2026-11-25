using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    internal const uint MinimumCreationTimestamp = 946_684_800u; // 2000-01-01T00:00:00Z

    private CalibrationRecord? RunZeroWasteCalibrationOnOpenDevice(
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

        try
        {
            // Warmup batch (counted toward search progress & hits so nothing is wasted)
            if (!ct.IsCancellationRequested && !IsCurrentlyPaused())
            {
                uint warmupStamps = RateControl.ComputeBatchTimestamps(
                    batchSizer.NextCandidateLimit,
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                ScanSearchBatch(device, keys, pubKeys, cursor, warmupStamps, pattern, batchSizer, ct, failureToken, out bool retired);
                cursor = AdvanceKeyGroupCursor(keys, cursor, warmupStamps, end, retired);
            }

            if (ct.IsCancellationRequested || IsCurrentlyPaused())
            {
                return null;
            }

            // Prime sensor baseline tick (D3DKMT delta anchor)
            UpdateRuntimeSensor(rt, _telemetrySensor.Sample(device.Info));

            double totalBudgetSec = Math.Max(0.05, _options.FullCalibrationDuration.TotalSeconds);
            double fullBudgetSec = Math.Max(0.02, totalBudgetSec * 0.207); // ~6.2s out of 30s; leaves 7 x 3.4s = 23.8s
            double fullWarmupSkipSec = totalBudgetSec >= 5.0 ? Math.Min(1.5, fullBudgetSec * 0.25) : 0.0;
            double fullSampleWindowSec = Math.Min(_options.CalibrationWindow.TotalSeconds, Math.Max(0.02, fullBudgetSec * 0.4));

            var allFullHpsSamples = new List<double>();
            var settledFullHpsSamples = new List<double>();
            var allFullWattSamples = new List<double>();
            var settledFullWattSamples = new List<double>();
            var allFullUtilSamples = new List<double>();
            var settledFullUtilSamples = new List<double>();

            long calStart = Stopwatch.GetTimestamp();
            long stage1Start = calStart;
            long windowStart = stage1Start;
            long windowChecked = 0;
            long stage1TotalMeasured = 0;

            // Stage 1: Full-speed maximum output baseline (~21% of budget, ~6.2s out of 30s)
            while (!ct.IsCancellationRequested && !IsCurrentlyPaused())
            {
                double stage1Elapsed = Stopwatch.GetElapsedTime(stage1Start).TotalSeconds;
                if (stage1Elapsed >= fullBudgetSec && stage1TotalMeasured > 0)
                {
                    break;
                }

                if (cursor >= end)
                {
                    RotateKeyGroup(keyPool, ref keys, ref pubKeys, ref end, ref cursor, ct);
                }

                uint stamps = RateControl.ComputeBatchTimestamps(
                    batchSizer.NextCandidateLimit,
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, batchSizer, ct, failureToken, out bool retired);
                cursor = AdvanceKeyGroupCursor(keys, cursor, stamps, end, retired);
                windowChecked += batchCandidates;
                stage1TotalMeasured += batchCandidates;

                double windowElapsed = Stopwatch.GetElapsedTime(windowStart).TotalSeconds;
                if (windowElapsed >= fullSampleWindowSec)
                {
                    double hps = windowChecked / windowElapsed;
                    Volatile.Write(ref rt.ActualHps, hps);
                    GpuSensorSample sensor = _telemetrySensor.Sample(device.Info);
                    UpdateRuntimeSensor(rt, sensor);

                    allFullHpsSamples.Add(hps);
                    if (sensor.HasPowerWatts && sensor.PowerWatts > 0.0) allFullWattSamples.Add(sensor.PowerWatts);
                    if (sensor.HasGpuUtil) allFullUtilSamples.Add(sensor.TotalGpuUtil);

                    if (Stopwatch.GetElapsedTime(stage1Start).TotalSeconds >= fullWarmupSkipSec)
                    {
                        settledFullHpsSamples.Add(hps);
                        if (sensor.HasPowerWatts && sensor.PowerWatts > 0.0) settledFullWattSamples.Add(sensor.PowerWatts);
                        if (sensor.HasGpuUtil) settledFullUtilSamples.Add(sensor.TotalGpuUtil);
                    }

                    windowStart = Stopwatch.GetTimestamp();
                    windowChecked = 0;
                }
            }

            if (ct.IsCancellationRequested || IsCurrentlyPaused())
            {
                return null;
            }

            List<double> fullHpsSource = settledFullHpsSamples.Count > 0 ? settledFullHpsSamples : allFullHpsSamples;
            if (fullHpsSource.Count == 0 && stage1TotalMeasured > 0)
            {
                double elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(stage1Start).TotalSeconds);
                fullHpsSource.Add(stage1TotalMeasured / elapsed);
                GpuSensorSample tailSensor = _telemetrySensor.Sample(device.Info);
                UpdateRuntimeSensor(rt, tailSensor);
                if (tailSensor.HasPowerWatts && tailSensor.PowerWatts > 0.0) allFullWattSamples.Add(tailSensor.PowerWatts);
                if (tailSensor.HasGpuUtil) allFullUtilSamples.Add(tailSensor.TotalGpuUtil);
            }

            if (fullHpsSource.Count == 0)
            {
                return null;
            }

            double baselineHps = ComputeMedian(fullHpsSource);
            double peakHps = fullHpsSource.Max();
            double fullLoadWatts = ComputeMedian(settledFullWattSamples.Count > 0 ? settledFullWattSamples : allFullWattSamples);
            double fullLoadGpuUtil = ComputeMedian(settledFullUtilSamples.Count > 0 ? settledFullUtilSamples : allFullUtilSamples);

            // Stage 2: Zero-waste step-down efficiency curve sweep (~79% of budget, ~23.8s out of 30s)
            double[] fractions = EfficiencyCurveSelector.DefaultStepDownFractions;
            double remainingSec = Math.Max(0.0, totalBudgetSec - Stopwatch.GetElapsedTime(calStart).TotalSeconds);
            var curveSamples = new List<EfficiencyCurveSample>(fractions.Length);

            if (remainingSec > 0.005 && baselineHps > 0.0)
            {
                double stepBudgetSec = remainingSec / fractions.Length;
                double settleSec = stepBudgetSec * 0.66; // ~2.24s settle out of 3.4s (> 2.0s P0->P3/P5 hysteresis)
                double sensorIntervalSec = Math.Min(0.15, Math.Max(0.02, (stepBudgetSec - settleSec) * 0.25));

                foreach (double frac in fractions)
                {
                    if (ct.IsCancellationRequested || IsCurrentlyPaused())
                    {
                        return null;
                    }

                    long targetHps = Math.Max(1L, (long)Math.Round(baselineHps * frac, MidpointRounding.AwayFromZero));
                    var stepDirective = new RateDirective(RateMode.Capped, targetHps);

                    long stepStart = Stopwatch.GetTimestamp();
                    double readyTimeSec = stepStart / (double)Stopwatch.Frequency;
                    long measStart = 0;
                    long measCandidates = 0;
                    long stepTotalCandidates = 0;
                    long lastSensorTick = stepStart;
                    var stepWatts = new List<double>();
                    var stepUtils = new List<double>();

                    while (!ct.IsCancellationRequested && !IsCurrentlyPaused())
                    {
                        double stepElapsed = Stopwatch.GetElapsedTime(stepStart).TotalSeconds;
                        if (stepElapsed >= stepBudgetSec && stepTotalCandidates > 0)
                        {
                            break;
                        }

                        if (measStart == 0 && stepElapsed >= settleSec)
                        {
                            measStart = Stopwatch.GetTimestamp();
                            lastSensorTick = measStart;
                            GpuSensorSample prime = _telemetrySensor.Sample(device.Info);
                            UpdateRuntimeSensor(rt, prime);
                        }

                        if (measStart != 0 && Stopwatch.GetElapsedTime(lastSensorTick).TotalSeconds >= sensorIntervalSec)
                        {
                            GpuSensorSample s = _telemetrySensor.Sample(device.Info);
                            UpdateRuntimeSensor(rt, s);
                            if (s.HasPowerWatts && s.PowerWatts > 0.0) stepWatts.Add(s.PowerWatts);
                            if (s.HasGpuUtil) stepUtils.Add(s.TotalGpuUtil);
                            lastSensorTick = Stopwatch.GetTimestamp();
                        }

                        double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                        if (nowSec < readyTimeSec)
                        {
                            double remainStepMs = Math.Max(0.0, (stepBudgetSec - Stopwatch.GetElapsedTime(stepStart).TotalSeconds) * 1000.0);
                            if (remainStepMs <= 0.5 && stepTotalCandidates > 0)
                            {
                                break;
                            }

                            int sleepMs = (int)Math.Clamp(Math.Min((readyTimeSec - nowSec) * 1000.0, Math.Max(1.0, remainStepMs)), 1.0, 25.0);
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
                            stepDirective,
                            end - cursor);

                        long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, batchSizer, ct, failureToken, out bool retired);
                        cursor = AdvanceKeyGroupCursor(keys, cursor, stamps, end, retired);
                        stepTotalCandidates += batchCandidates;
                        if (measStart != 0)
                        {
                            measCandidates += batchCandidates;
                            double measElapsedLive = Stopwatch.GetElapsedTime(measStart).TotalSeconds;
                            if (measElapsedLive >= 0.25)
                            {
                                Volatile.Write(ref rt.ActualHps, measCandidates / measElapsedLive);
                            }
                        }
                        else if (stepElapsed >= 0.3)
                        {
                            Volatile.Write(ref rt.ActualHps, stepTotalCandidates / stepElapsed);
                        }

                        double afterSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                        readyTimeSec = Math.Max(afterSec, readyTimeSec + (batchCandidates / (double)targetHps));
                    }

                    if (ct.IsCancellationRequested || IsCurrentlyPaused())
                    {
                        return null;
                    }

                    // Ensure at least one final sensor reading per step (covers short test durations)
                    GpuSensorSample endSample = _telemetrySensor.Sample(device.Info);
                    UpdateRuntimeSensor(rt, endSample);
                    if (endSample.HasPowerWatts && endSample.PowerWatts > 0.0) stepWatts.Add(endSample.PowerWatts);
                    if (endSample.HasGpuUtil) stepUtils.Add(endSample.TotalGpuUtil);

                    double stepHps;
                    if (measStart != 0 && measCandidates > 0)
                    {
                        double measElapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(measStart).TotalSeconds);
                        stepHps = measCandidates / measElapsed;
                    }
                    else
                    {
                        double stepElapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(stepStart).TotalSeconds);
                        stepHps = stepTotalCandidates / stepElapsed;
                    }

                    Volatile.Write(ref rt.ActualHps, stepHps);
                    curveSamples.Add(new EfficiencyCurveSample(
                        RateFraction: frac,
                        MeasuredHps: stepHps,
                        MeasuredWatts: ComputeMedian(stepWatts),
                        MeasuredGpuUtil: ComputeMedian(stepUtils)));
                }
            }

            if (ct.IsCancellationRequested || IsCurrentlyPaused())
            {
                return null;
            }

            EfficiencyProfile profile = EfficiencyCurveSelector.Select(
                baselineHps,
                fullLoadWatts,
                fullLoadGpuUtil,
                curveSamples);

            return new CalibrationRecord(
                DeviceKey: device.Info.DeviceKey,
                DeviceName: device.Info.Name,
                DriverVersion: device.Info.DriverVersion,
                KernelVersion: device.KernelVersion,
                PeakHashesPerSecond: peakHps,
                MedianHashesPerSecond: baselineHps,
                IsFullCalibration: true,
                CalibratedAtUtc: DateTimeOffset.UtcNow,
                WorkloadIdentity: CalibrationValidator.WorkloadIdentity(device.Info, pattern),
                EfficiencyProfile: profile);
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
        long lastSensorPollTs = Stopwatch.GetTimestamp();
        double sensorPollIntervalSec = Math.Max(0.05, _options.SensorPollInterval.TotalSeconds);
        bool sensorPrimed = false;

        // Dynamic median tracking: active ONLY while tier is continuously at Max (FullSpeed).
        var fullSpeedSamples = new List<double>();
        var fullSpeedWattSamples = new List<double>();
        long dynamicMedianStart = Stopwatch.GetTimestamp();
        int minMedianSamples = Math.Max(1, _options.MinDynamicMedianSamples);

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

                if (epochBefore != seenRateEpoch || epochAfter != epochBefore)
                {
                    yieldController.Reset();
                    Volatile.Write(ref rt.IsOverloadYielding, 0);
                }

                // Poll GPU power & engine utilization sensor periodically (for live MH/W badge + overload yield)
                double sensorDtSec = Stopwatch.GetElapsedTime(lastSensorPollTs).TotalSeconds;
                if (!userPaused && (!sensorPrimed || sensorDtSec >= sensorPollIntervalSec))
                {
                    GpuSensorSample sensor = _telemetrySensor.Sample(device.Info);
                    UpdateRuntimeSensor(rt, sensor);
                    if (activeTier == PowerTier.Max && sensor.HasPowerWatts && sensor.PowerWatts > 0.0)
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
                    : RateControl.FromTier(activeTier, deviceBaseline, calRecord?.EfficiencyProfile, yieldController.YieldScale);

                double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

                if (epochBefore != seenRateEpoch || epochAfter != epochBefore || directive.Mode != lastMode)
                {
                    seenRateEpoch = epochAfter;
                    lastMode = directive.Mode;
                    readyTimeSec = nowSec;
                    sampleStart = Stopwatch.GetTimestamp();
                    sampleChecked = 0;
                    fullSpeedSamples.Clear();
                    fullSpeedWattSamples.Clear();
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

                        if (directive.Mode == RateMode.FullSpeed && hps > 0.0)
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
                    Volatile.Write(ref rt.ActualHps, 0.0);
                    Volatile.Write(ref rt.IsOverloadYielding, 0);
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

    private bool HandleVerifiedHits(
        GpuDeviceInfo deviceInfo,
        Ed25519KeyPair[] keys,
        IReadOnlyList<uint>[] laneHits,
        KeyIdPattern pattern,
        bool allowAutoPause,
        CancellationToken failureToken)
    {
        for (int lane = 0; lane < keys.Length; lane++)
        {
            IReadOnlyList<uint> timestamps = laneHits[lane];
            if (timestamps.Count == 0)
            {
                continue;
            }

            uint ts = timestamps[0];
            string fingerprint = OpenPgpV4Packet.ComputeFingerprint(keys[lane].PublicKey, ts);
            ulong keyId = OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane].PublicKey, ts);
            if (!pattern.Matches(keyId))
            {
                throw new InvalidOperationException(
                    $"GPU hit verification failed on '{deviceInfo.Name}': keyId {keyId:X16} did not match {pattern.CanonicalMask}.");
            }

            lock (_lifecycleLock)
            {
                _sessionWork = _sessionWork with { VerifiedHits = _sessionWork.VerifiedHits + 1 };
            }

            MatchedKeyRecord record;
            bool stopAfterSave;
            lock (_submissionGate)
            {
                if (_submissionFailed || Volatile.Read(ref _autoPauseTriggered) != 0 || failureToken.IsCancellationRequested)
                {
                    return false;
                }

                int storedCount;
                bool wasAdded;
                try
                {
                    record = _vaultStore.SaveMatch(keys[lane].PrivateSeed, keys[lane].PublicKey, ts, fingerprint,
                        pattern.CanonicalMask, deviceInfo.Name, out storedCount, out wasAdded);
                }
                catch (Exception ex)
                {
                    _submissionFailed = true;
                    if (ex is VaultFullException)
                    {
                        _vaultIsFull = true;
                        _errorMessage = ex.Message;
                        throw;
                    }

                    throw new SearchSessionException(SearchFailure.SaveFailed, ex.Message, ex);
                }

                Interlocked.Exchange(ref _totalMatchesCount, storedCount);
                if (!wasAdded)
                {
                    return true;
                }

                lock (_lifecycleLock)
                {
                    _sessionWork = _sessionWork with { SavedKeys = _sessionWork.SavedKeys + 1 };
                }

                _vaultIsFull = storedCount >= DpapiVaultStore.MaxStoredMatches;
                stopAfterSave = _vaultIsFull || (allowAutoPause && _autoPauseOnHit);
                if (stopAfterSave)
                {
                    Interlocked.Exchange(ref _autoPauseTriggered, 1);
                    Interlocked.Increment(ref _rateEpoch);
                }
            }

            if (stopAfterSave)
            {
                _activeOpCts?.Cancel();
            }

            NotifyMatchFound(record);
            return true;
        }

        return false;
    }

    private void NotifyMatchFound(MatchedKeyRecord record)
    {
        if (MatchFound is not { } handlers)
        {
            return;
        }

        foreach (EventHandler<MatchedKeyRecord> listener in handlers.GetInvocationList())
        {
            try
            {
                listener(this, record);
            }
            catch (Exception ex)
            {
                Trace.TraceError("A PGPMint match notification failed: {0}", ex.GetType().Name);
            }
        }
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
