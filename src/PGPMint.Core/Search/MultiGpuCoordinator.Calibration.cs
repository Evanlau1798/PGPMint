using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
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
            double fullBudgetSec = Math.Max(0.02, totalBudgetSec * 0.17); // ~5.1s out of 30s; leaves ~24.9s continuous ramp
            double fullWarmupSkipSec = totalBudgetSec >= 5.0 ? Math.Min(1.5, fullBudgetSec * 0.30) : 0.0;
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

            // Stage 1: Full-speed maximum output baseline (~17% of budget, ~5.1s out of 30s)
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

            // Stage 2: Zero-waste continuous descending efficiency curve ramp (~83% of budget, ~24.9s out of 30s)
            double remainingSec = Math.Max(0.0, totalBudgetSec - Stopwatch.GetElapsedTime(calStart).TotalSeconds);
            int targetSamples = Math.Clamp((int)Math.Round(remainingSec / 0.52), 8, 48);
            var curveSamples = new List<EfficiencyCurveSample>(targetSamples + 2);

            if (remainingSec > 0.005 && baselineHps > 0.0)
            {
                double sampleWindowSec = Math.Clamp(remainingSec / targetSamples, 0.025, 0.55);
                double sensorIntervalSec = Math.Min(0.125, Math.Max(0.015, sampleWindowSec * 0.30));

                long rampStart = Stopwatch.GetTimestamp();
                double readyTimeSec = rampStart / (double)Stopwatch.Frequency;
                long binStart = rampStart;
                double binStartFrac = EfficiencyCurveSelector.RampStartFraction;
                long binCandidates = 0;
                double binBudgetSec = 0.0;
                long lastSensorTick = rampStart;
                var binWatts = new List<double>();
                var binUtils = new List<double>();

                while (!ct.IsCancellationRequested && !IsCurrentlyPaused())
                {
                    double rampElapsed = Stopwatch.GetElapsedTime(rampStart).TotalSeconds;
                    if (rampElapsed >= remainingSec && (curveSamples.Count > 0 || binCandidates > 0))
                    {
                        break;
                    }

                    double progress = Math.Clamp(rampElapsed / remainingSec, 0.0, 1.0);
                    double currentFrac = EfficiencyCurveSelector.RampFractionAtProgress(progress);
                    long targetHps = Math.Max(1L, (long)Math.Round(baselineHps * currentFrac, MidpointRounding.AwayFromZero));
                    var rampDirective = new RateDirective(RateMode.Capped, targetHps);

                    if (Stopwatch.GetElapsedTime(lastSensorTick).TotalSeconds >= sensorIntervalSec)
                    {
                        GpuSensorSample s = _telemetrySensor.Sample(device.Info);
                        UpdateRuntimeSensor(rt, s);
                        if (s.HasPowerWatts && s.PowerWatts > 0.0) binWatts.Add(s.PowerWatts);
                        if (s.HasGpuUtil) binUtils.Add(s.TotalGpuUtil);
                        lastSensorTick = Stopwatch.GetTimestamp();
                    }

                    double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    double binElapsed = Stopwatch.GetElapsedTime(binStart).TotalSeconds;
                    // Flush only once the last dispatched batch's pacing sleep has completed (nowSec >= readyTimeSec)
                    // so every bin spans whole [dispatch + sleep] cycles without cross-bin sawtooth distortion.
                    if (nowSec >= readyTimeSec && binElapsed >= sampleWindowSec && binCandidates > 0)
                    {
                        if (binWatts.Count == 0 && binUtils.Count == 0)
                        {
                            GpuSensorSample s = _telemetrySensor.Sample(device.Info);
                            UpdateRuntimeSensor(rt, s);
                            if (s.HasPowerWatts && s.PowerWatts > 0.0) binWatts.Add(s.PowerWatts);
                            if (s.HasGpuUtil) binUtils.Add(s.TotalGpuUtil);
                            lastSensorTick = Stopwatch.GetTimestamp();
                        }

                        double effectiveElapsed = Math.Max(binElapsed, binBudgetSec);
                        double binHps = binCandidates / Math.Max(0.001, effectiveElapsed);
                        double binFrac = Math.Clamp(
                            0.5 * (binStartFrac + currentFrac),
                            EfficiencyCurveSelector.MinTierFraction,
                            EfficiencyCurveSelector.RampStartFraction);
                        Volatile.Write(ref rt.ActualHps, binHps);
                        curveSamples.Add(new EfficiencyCurveSample(
                            RateFraction: binFrac,
                            MeasuredHps: binHps,
                            MeasuredWatts: ComputeMedian(binWatts),
                            MeasuredGpuUtil: ComputeMedian(binUtils)));

                        binStart = Stopwatch.GetTimestamp();
                        binStartFrac = currentFrac;
                        binCandidates = 0;
                        binBudgetSec = 0.0;
                        binWatts.Clear();
                        binUtils.Clear();
                    }

                    if (nowSec < readyTimeSec)
                    {
                        double remainRampMs = Math.Max(0.0, (remainingSec - Stopwatch.GetElapsedTime(rampStart).TotalSeconds) * 1000.0);
                        if (remainRampMs <= 0.5 && (curveSamples.Count > 0 || binCandidates > 0))
                        {
                            break;
                        }

                        int sleepMs = (int)Math.Clamp(Math.Min((readyTimeSec - nowSec) * 1000.0, Math.Max(1.0, remainRampMs)), 1.0, 20.0);
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
                        rampDirective,
                        end - cursor);

                    long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, batchSizer, ct, failureToken, out bool retired);
                    cursor = AdvanceKeyGroupCursor(keys, cursor, stamps, end, retired);
                    binCandidates += batchCandidates;
                    double batchBudgetSec = batchCandidates / (double)targetHps;
                    binBudgetSec += batchBudgetSec;

                    double afterSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                    readyTimeSec = Math.Max(afterSec, readyTimeSec + batchBudgetSec);
                }

                if (ct.IsCancellationRequested || IsCurrentlyPaused())
                {
                    return null;
                }

                // Flush the final ramp bin only when it covers enough duration (or is the sole bin),
                // normalizing by max(wallElapsed, commandedBudgetSec) so an un-slept tail batch cannot inflate HPS.
                double tailElapsed = Stopwatch.GetElapsedTime(binStart).TotalSeconds;
                if (binCandidates > 0 && (curveSamples.Count == 0 || tailElapsed >= sampleWindowSec * 0.5))
                {
                    GpuSensorSample endSample = _telemetrySensor.Sample(device.Info);
                    UpdateRuntimeSensor(rt, endSample);
                    if (endSample.HasPowerWatts && endSample.PowerWatts > 0.0) binWatts.Add(endSample.PowerWatts);
                    if (endSample.HasGpuUtil) binUtils.Add(endSample.TotalGpuUtil);

                    double tailDenom = Math.Max(0.001, Math.Max(tailElapsed, binBudgetSec));
                    double tailProgress = Math.Clamp(Stopwatch.GetElapsedTime(rampStart).TotalSeconds / remainingSec, 0.0, 1.0);
                    double tailFrac = Math.Clamp(
                        0.5 * (binStartFrac + EfficiencyCurveSelector.RampFractionAtProgress(tailProgress)),
                        EfficiencyCurveSelector.MinTierFraction,
                        EfficiencyCurveSelector.RampStartFraction);
                    double tailHps = binCandidates / tailDenom;
                    Volatile.Write(ref rt.ActualHps, tailHps);
                    curveSamples.Add(new EfficiencyCurveSample(
                        RateFraction: tailFrac,
                        MeasuredHps: tailHps,
                        MeasuredWatts: ComputeMedian(binWatts),
                        MeasuredGpuUtil: ComputeMedian(binUtils)));
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

}
