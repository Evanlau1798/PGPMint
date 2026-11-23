using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    private const long SmallBatchCandidateLimit = 1L << 18;

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
                    Math.Min(device.MaxCandidatesPerBatch, SmallBatchCandidateLimit),
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                ScanSearchBatch(device, keys, pubKeys, cursor, warmupStamps, pattern, ct);
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
                    SearchBatchLimit(device, pattern),
                    device.KeysPerBatch,
                    new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel),
                    end - cursor);

                long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, ct);

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
        bool firstBatch = true;

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

                long batchLimit = firstBatch ? Math.Min(device.MaxCandidatesPerBatch, SmallBatchCandidateLimit)
                    : SearchBatchLimit(device, pattern);
                uint stamps = RateControl.ComputeBatchTimestamps(
                    batchLimit,
                    device.KeysPerBatch,
                    directive,
                    end - cursor);

                long batchCandidates = ScanSearchBatch(device, keys, pubKeys, cursor, stamps, pattern, ct);
                firstBatch = false;
                sampleChecked += batchCandidates;
                cursor += stamps;

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
        bool allowAutoPause,
        CancellationToken cancellationToken)
    {
        for (int lane = 0; lane < keys.Length; lane++)
        {
            IReadOnlyList<uint> timestamps = laneHits[lane];
            if (timestamps.Count == 0)
            {
                continue;
            }

            foreach (uint ts in timestamps)
            {
                string fullFingerprint = OpenPgpV4Packet.ComputeFingerprint(keys[lane].PublicKey, ts);
                ulong keyIdValue = OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane].PublicKey, ts);
                if (!pattern.Matches(keyIdValue))
                {
                    throw new InvalidOperationException(
                        $"GPU hit verification failed on '{deviceInfo.Name}': keyId {keyIdValue:X16} did not match {pattern.CanonicalMask}.");
                }

                lock (_lifecycleLock)
                {
                    _sessionWork = _sessionWork with { VerifiedHits = _sessionWork.VerifiedHits + 1 };
                }

                MatchedKeyRecord record;
                bool shouldAutoPause;
                lock (_submissionGate)
                {
                    if (_submissionFailed || Volatile.Read(ref _autoPauseTriggered) != 0 || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    int storedCount;
                    bool wasAdded;
                    try
                    {
                        record = _vaultStore.SaveMatch(keys[lane].PrivateSeed, keys[lane].PublicKey, ts, fullFingerprint,
                            pattern.CanonicalMask, deviceInfo.Name, out storedCount, out wasAdded);
                    }
                    catch
                    {
                        _submissionFailed = true;
                        throw;
                    }

                    Interlocked.Exchange(ref _totalMatchesCount, storedCount);
                    if (wasAdded)
                    {
                        lock (_lifecycleLock)
                        {
                            _sessionWork = _sessionWork with { SavedKeys = _sessionWork.SavedKeys + 1 };
                        }
                    }

                    shouldAutoPause = allowAutoPause && _autoPauseOnHit;
                    if (shouldAutoPause)
                    {
                        Interlocked.Exchange(ref _autoPauseTriggered, 1);
                        Interlocked.Increment(ref _rateEpoch);
                    }
                }

                if (shouldAutoPause)
                {
                    _activeOpCts?.Cancel();
                }

                MatchFound?.Invoke(this, record);

                // Cap to at most 1 saved match per GPU batch to prevent simple suffixes from flooding storage/UI.
                return;
            }
        }
    }

    private bool IsCurrentlyPaused() =>
        _pausedExplicitly || _pausedByZeroSlider || Volatile.Read(ref _autoPauseTriggered) != 0;

    private static long SearchBatchLimit(IGpuSearchDevice device, KeyIdPattern pattern) =>
        pattern.FixedHexLength <= 4 ? Math.Min(device.MaxCandidatesPerBatch, SmallBatchCandidateLimit)
            : device.MaxCandidatesPerBatch;

    private long ScanSearchBatch(
        IGpuSearchDevice device,
        Ed25519KeyPair[] keys,
        IReadOnlyList<byte[]> publicKeys,
        uint start,
        uint count,
        KeyIdPattern pattern,
        CancellationToken cancellationToken)
    {
        SearchBatchResult batch = device.ScanBatch(publicKeys, start, count, pattern,
            resultMode: SearchResultMode.FindOne, cancellationToken: cancellationToken);
        long returnedHits = batch.ReturnedHits;
        lock (_lifecycleLock)
        {
            Interlocked.Add(ref _sessionChecked, batch.UniqueCandidatesCompleted);
            _sessionWork = _sessionWork with
            {
                UniqueCandidatesCompleted = _sessionWork.UniqueCandidatesCompleted + batch.UniqueCandidatesCompleted,
                HashEvaluations = _sessionWork.HashEvaluations + batch.HashEvaluations,
                ReturnedHits = _sessionWork.ReturnedHits + returnedHits
            };
        }

        if (batch.WasCancelled)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"Unexpected batch cancellation on '{device.Info.Name}'.");
            }

            return batch.UniqueCandidatesCompleted;
        }

        if (!batch.ResultsComplete || !batch.HashRangeFullyProcessed ||
            returnedHits > 1 || batch.Hits.Length != keys.Length ||
            batch.Hits.Any(lane => lane.Count > 0 && (lane[0] < start || (ulong)lane[0] >= (ulong)start + count)))
        {
            throw new InvalidOperationException($"Incomplete or invalid GPU search result on '{device.Info.Name}'.");
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            HandleVerifiedHits(device.Info, keys, batch.Hits, pattern, allowAutoPause: true, cancellationToken);
        }

        return batch.UniqueCandidatesCompleted;
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
}
