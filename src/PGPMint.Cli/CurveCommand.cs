using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class CurveCommand
{
    private sealed record DeviceSensorSample(double PowerWatts, double GpuUtilPercent, double TempC, bool HasTemp, bool IsThermalThrottled);

    internal static async Task<int> RunAsync(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> infos, CommandLine options)
    {
        KeyIdPattern pattern = KeyIdPattern.Parse(options.Get("pattern") ?? "1145141919810114");
        int settleSec = options.Number("settle-seconds", 3);
        int measureSec = options.Number("measure-seconds", 5);
        string? outCsv = options.Get("out-csv");
        string? outJson = options.Get("out-json");
        bool useVault = options.Has("data-dir");

        (backend as IWorkloadGpuBackend)?.SetWorkload(pattern);

        VaultSession? lease = null;
        PasswordSeedProtector? protector = null;
        AppStateStore? stateStore = null;
        VaultStore? vaultStore = null;

        if (useVault)
        {
            string dataDir = Program.DataDirectory(options);
            lease = new VaultSession(dataDir);
            protector = Program.OpenVaultProtector(lease, options);
            stateStore = new AppStateStore(dataDir);
            vaultStore = new VaultStore(dataDir, protector);
        }

        using IGpuTelemetrySensor sensor = MultiGpuCoordinator.CreateDefaultTelemetrySensor();
        var openDevices = new List<IGpuSearchDevice>(infos.Count);
        try
        {
            foreach (GpuDeviceInfo info in infos)
            {
                openDevices.Add(backend.CreateDevice(info));
            }

            Console.WriteLine($"Curve sweep: devices={openDevices.Count}; pattern={pattern.CanonicalMask}; settle={settleSec}s; measure={measureSec}s; zero-waste={useVault}");

            var rows = new List<CliCurveRow>(21);

            // 0% idle baseline
            await Task.Delay(TimeSpan.FromSeconds(settleSec));
            var idleSamples = await SampleAggregateAsync(sensor, openDevices, TimeSpan.FromSeconds(measureSec));
            rows.Add(BuildRow(0, 0.0, 0L, idleSamples));
            Console.WriteLine($"    0% | 0.000 GH/s | {rows[^1].PowerMedianWatts,6:F2} W |       - | temp={(rows[^1].TempMaxCelsius?.ToString("F0", CultureInfo.InvariantCulture) ?? "-")}C");

            // Anchor full speed first for sub-100% duty-cycle pacing
            var workers = new CurveWorkerState[openDevices.Count];
            for (int i = 0; i < openDevices.Count; i++)
            {
                workers[i] = new CurveWorkerState(openDevices[i]);
            }

            try
            {
                double fullSpeedBaselineHps = 0.0;
                var curveSamples = new List<EfficiencyCurveSample>(20);
                double fullLoadWatts = 0.0;
                double fullLoadUtil = 0.0;

                for (int pct = 100; pct >= 5; pct -= 5)
                {
                    double frac = pct / 100.0;
                    bool isFull = pct == 100;
                    long stepTargetTotalHps = !isFull && fullSpeedBaselineHps > 0.0
                        ? Math.Max(1L, (long)Math.Round(fullSpeedBaselineHps * frac, MidpointRounding.AwayFromZero))
                        : RateControl.FullSpeedSentinel;

                    var aggSamples = new List<DeviceSensorSample>(64);
                    long[] perDevCands = new long[workers.Length];
                    long stepTotalCands = 0;
                    using var stepCts = new CancellationTokenSource();

                    Task[] tasks = new Task[workers.Length];
                    long stepStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < workers.Length; i++)
                    {
                        int idx = i;
                        long perWorkerTarget = isFull
                            ? RateControl.FullSpeedSentinel
                            : Math.Max(1L, (long)Math.Round(stepTargetTotalHps / (double)workers.Length, MidpointRounding.AwayFromZero));
                        RateDirective dir = isFull
                            ? new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel)
                            : new RateDirective(RateMode.Capped, perWorkerTarget);

                        tasks[idx] = Task.Run(() =>
                        {
                            long total, measured;
                            RunWorkerStep(workers[idx], pattern, dir, settleSec, measureSec, vaultStore, stepCts.Token, out total, out measured);
                            perDevCands[idx] = measured;
                            Interlocked.Add(ref stepTotalCands, total);
                        }, CancellationToken.None);
                    }

                    // Poll NVML/D3D telemetry during the measurement window [settleSec .. settleSec+measureSec]
                    await Task.Delay(TimeSpan.FromSeconds(settleSec));
                    long measureWallStartTs = Stopwatch.GetTimestamp();
                    var pollWatch = Stopwatch.StartNew();
                    while (pollWatch.Elapsed.TotalSeconds < measureSec)
                    {
                        aggSamples.Add(SampleOnce(sensor, openDevices));
                        await Task.Delay(100);
                    }

                    await Task.WhenAll(tasks);
                    double elapsedMeasureSec = Math.Max(0.001, Stopwatch.GetElapsedTime(measureWallStartTs).TotalSeconds);
                    double actualHps = perDevCands.Sum() / elapsedMeasureSec;
                    long totalForStep = Interlocked.Read(ref stepTotalCands);

                    if (useVault && stateStore is not null && totalForStep > 0)
                    {
                        stateStore.Update(s =>
                        {
                            s.LastPatternMask = pattern.CanonicalMask;
                            s.TotalCheckedCandidates += (ulong)totalForStep;
                        });
                    }

                    CliCurveRow row = BuildRow(pct, actualHps / 1e9, totalForStep, aggSamples);
                    rows.Add(row);

                    if (isFull)
                    {
                        fullSpeedBaselineHps = actualHps;
                        fullLoadWatts = row.PowerMedianWatts;
                        fullLoadUtil = row.GpuUtilMedianPercent;
                    }
                    else if (frac <= EfficiencyCurveSelector.RampStartFraction + 0.031)
                    {
                        curveSamples.Add(new EfficiencyCurveSample(frac, actualHps, row.PowerMedianWatts, row.GpuUtilMedianPercent));
                    }

                    string tempStr = row.TempMaxCelsius is { } tc ? $"{tc:F0}C" : "-";
                    string thrStr = row.IsThermalThrottled ? " THERMAL" : string.Empty;
                    Console.WriteLine($"  {pct,3}% | {row.ActualGhps,6:F3} GH/s | {row.PowerMedianWatts,6:F2} W | {row.Mhpw,6:F2} MH/W | util={row.GpuUtilMedianPercent,3:F0}% | temp={tempStr}{thrStr}");
                }

                EfficiencyProfile profile = EfficiencyCurveSelector.Select(fullSpeedBaselineHps, fullLoadWatts, fullLoadUtil, curveSamples);
                Console.WriteLine($"Profile: IsHighIdlePowerFloor={profile.IsHighIdlePowerFloor}");
                Console.WriteLine($"  Eco        : {profile.Eco.RateFraction:P1} ({profile.Eco.MeasuredHps / 1e9:F3} GH/s @ {profile.Eco.EstimatedWatts:F1} W, mode={profile.Eco.SelectionMode})");
                Console.WriteLine($"  Balanced   : {profile.Balanced.RateFraction:P1} ({profile.Balanced.MeasuredHps / 1e9:F3} GH/s @ {profile.Balanced.EstimatedWatts:F1} W, mode={profile.Balanced.SelectionMode})");
                Console.WriteLine($"  Efficiency : {profile.Efficiency.RateFraction:P1} ({profile.Efficiency.MeasuredHps / 1e9:F3} GH/s @ {profile.Efficiency.EstimatedWatts:F1} W, mode={profile.Efficiency.SelectionMode})");

                if (!string.IsNullOrWhiteSpace(outCsv))
                {
                    await WriteCsvAsync(Path.GetFullPath(outCsv), rows);
                    Console.WriteLine($"Wrote CSV: {Path.GetFullPath(outCsv)}");
                }

                if (!string.IsNullOrWhiteSpace(outJson))
                {
                    var report = new CliCurveReport(
                        DateTimeOffset.UtcNow.ToString("O"),
                        pattern.CanonicalMask,
                        settleSec,
                        measureSec,
                        openDevices.Select(d => d.Info.Name).ToArray(),
                        rows,
                        profile);
                    string jsonPath = Path.GetFullPath(outJson);
                    Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
                    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, CliJsonContext.Default.CliCurveReport));
                    Console.WriteLine($"Wrote JSON: {jsonPath}");
                }
            }
            finally
            {
                foreach (CurveWorkerState w in workers)
                {
                    w.Dispose();
                }
            }
        }
        finally
        {
            foreach (IGpuSearchDevice d in openDevices)
            {
                d.Dispose();
            }

            protector?.Dispose();
            lease?.Dispose();
        }

        return 0;
    }

    private static void RunWorkerStep(
        CurveWorkerState w,
        KeyIdPattern pattern,
        RateDirective directive,
        int settleSec,
        int measureSec,
        VaultStore? vaultStore,
        CancellationToken ct,
        out long totalCandidates,
        out long measureCandidates)
    {
        totalCandidates = 0;
        measureCandidates = 0;
        long startTs = Stopwatch.GetTimestamp();
        double totalWindowSec = settleSec + measureSec;
        double readyTimeSec = startTs / (double)Stopwatch.Frequency;
        bool inMeasure = false;

        while (!ct.IsCancellationRequested)
        {
            double elapsed = Stopwatch.GetElapsedTime(startTs).TotalSeconds;
            if (!inMeasure && elapsed >= settleSec)
            {
                inMeasure = true;
                measureCandidates = 0;
            }

            if (elapsed >= totalWindowSec)
            {
                break;
            }

            double nowSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (directive.Mode == RateMode.Capped && nowSec < readyTimeSec)
            {
                int sleepMs = (int)Math.Clamp((readyTimeSec - nowSec) * 1000.0, 1.0, 20.0);
                Thread.Sleep(sleepMs);
                continue;
            }

            if (w.Cursor >= w.End)
            {
                w.RotateKeys();
            }

            uint stamps = RateControl.ComputeBatchTimestamps(w.Sizer.NextCandidateLimit, w.Device.KeysPerBatch, directive, w.End - w.Cursor);
            long before = Stopwatch.GetTimestamp();
            SearchBatchResult res = w.Device.ScanBatch(w.PubKeys, w.Cursor, stamps, pattern, capacity: 1, resultMode: SearchResultMode.FindOne);
            double wallMs = res.DispatchWallMilliseconds ?? Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            if (!res.HashRangeFullyProcessed || !res.ResultsComplete)
            {
                throw new InvalidOperationException($"GPU returned incomplete batch on {w.Device.Info.Name}.");
            }

            uint completedStamps = (uint)Math.Clamp(res.UniqueCandidatesCompleted / Math.Max(1, w.Device.KeysPerBatch), 0, stamps);
            w.Sizer.Observe(Math.Max(1u, completedStamps), wallMs);

            long cands = res.UniqueCandidatesCompleted;
            totalCandidates += cands;
            if (inMeasure)
            {
                measureCandidates += cands;
            }

            bool rotated = false;
            for (int lane = 0; lane < w.Keys.Length && lane < res.Hits.Length; lane++)
            {
                if (res.Hits[lane].Count == 0) continue;
                uint ts = res.Hits[lane][0];
                string fp = OpenPgpV4Packet.ComputeFingerprint(w.Keys[lane].PublicKey, ts);
                ulong kid = OpenPgpV4Packet.ComputeKeyIdUInt64(w.Keys[lane].PublicKey, ts);
                if (!pattern.Matches(kid))
                {
                    throw new InvalidOperationException("GPU hit verification failed CPU SHA-1 oracle.");
                }

                if (vaultStore is not null)
                {
                    vaultStore.SaveMatch(w.Keys[lane].PrivateSeed, w.Keys[lane].PublicKey, ts, fp, pattern.CanonicalMask, w.Device.Info.Name, out _);
                }

                w.RotateKeys();
                rotated = true;
                break;
            }

            if (!rotated)
            {
                ulong next = (ulong)w.Cursor + stamps;
                if (next >= w.End) w.RotateKeys();
                else w.Cursor = (uint)next;
            }

            if (directive.Mode == RateMode.Capped)
            {
                double afterSec = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                readyTimeSec = Math.Max(afterSec, readyTimeSec + (cands / (double)directive.TargetHashesPerSecond));
            }
        }
    }

    private static async Task<List<DeviceSensorSample>> SampleAggregateAsync(IGpuTelemetrySensor sensor, List<IGpuSearchDevice> devices, TimeSpan duration)
    {
        var list = new List<DeviceSensorSample>((int)(duration.TotalMilliseconds / 100) + 4);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            list.Add(SampleOnce(sensor, devices));
            await Task.Delay(100);
        }
        return list;
    }

    private static DeviceSensorSample SampleOnce(IGpuTelemetrySensor sensor, List<IGpuSearchDevice> devices)
    {
        double watts = 0.0;
        double utilSum = 0.0;
        int utilCount = 0;
        double maxTemp = 0.0;
        bool hasTemp = false;
        bool thermal = false;

        foreach (IGpuSearchDevice dev in devices)
        {
            GpuSensorSample s = sensor.Sample(dev.Info);
            if (s.HasPowerWatts && s.PowerWatts > 0.0) watts += s.PowerWatts;
            if (s.HasGpuUtil) { utilSum += s.TotalGpuUtil; utilCount++; }
            if (s.HasTemperature && s.TemperatureCelsius > 0.0)
            {
                hasTemp = true;
                if (s.TemperatureCelsius > maxTemp) maxTemp = s.TemperatureCelsius;
            }
            if (s.IsThermalThrottled) thermal = true;
        }

        return new DeviceSensorSample(watts, utilCount > 0 ? utilSum / utilCount : 0.0, maxTemp, hasTemp, thermal);
    }

    private static CliCurveRow BuildRow(int pct, double ghps, long stepCands, List<DeviceSensorSample> samples)
    {
        double pMed = ComputeMedian(samples.Where(s => s.PowerWatts > 0.0).Select(s => s.PowerWatts));
        double utilMed = ComputeMedian(samples.Select(s => s.GpuUtilPercent));
        double? maxTemp = samples.Any(s => s.HasTemp) ? samples.Where(s => s.HasTemp).Max(s => s.TempC) : null;
        double mhpw = pMed > 0.0 ? ghps * 1000.0 / pMed : 0.0;
        return new CliCurveRow(pct, ghps, pMed, mhpw, utilMed, maxTemp, samples.Any(s => s.IsThermalThrottled), stepCands);
    }

    private static double ComputeMedian(IEnumerable<double> source)
    {
        var sorted = source.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (sorted.Count == 0) return 0.0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }

    private static async Task WriteCsvAsync(string path, List<CliCurveRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder("target_pct,actual_ghps,power_median_w,mhpw,gpu_util_median_pct,temp_max_c,thermal_throttled,step_candidates\n");
        foreach (CliCurveRow r in rows)
        {
            string tc = r.TempMaxCelsius?.ToString("F0", CultureInfo.InvariantCulture) ?? string.Empty;
            sb.AppendLine(FormattableString.Invariant($"{r.TargetPercent},{r.ActualGhps:F6},{r.PowerMedianWatts:F3},{r.Mhpw:F3},{r.GpuUtilMedianPercent:F2},{tc},{r.IsThermalThrottled},{r.StepCandidates}"));
        }
        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private sealed class CurveWorkerState : IDisposable
    {
        public IGpuSearchDevice Device { get; }
        public AdaptiveBatchSizer Sizer { get; }
        public Ed25519KeyPair[] Keys { get; private set; }
        public byte[][] PubKeys { get; private set; }
        public uint Cursor { get; set; }
        public uint End { get; private set; }

        public CurveWorkerState(IGpuSearchDevice device)
        {
            Device = device;
            Sizer = new AdaptiveBatchSizer(device.MaxCandidatesPerBatch, device.KeysPerBatch, 16);
            Keys = [];
            PubKeys = [];
            RotateKeys();
        }

        public void RotateKeys()
        {
            ZeroKeys();
            var arr = new Ed25519KeyPair[Device.KeysPerBatch];
            for (int i = 0; i < arr.Length; i++) arr[i] = Ed25519KeyGenerator.Generate();
            Keys = arr;
            PubKeys = arr.Select(k => k.PublicKey).ToArray();
            End = (uint)Math.Clamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120, MultiGpuCoordinator.MinimumCreationTimestamp, uint.MaxValue);
            Cursor = MultiGpuCoordinator.MinimumCreationTimestamp;
        }

        private void ZeroKeys()
        {
            foreach (Ed25519KeyPair k in Keys)
            {
                if (k.PrivateSeed is { } seed) CryptographicOperations.ZeroMemory(seed);
            }
        }

        public void Dispose()
        {
            ZeroKeys();
            Keys = [];
            PubKeys = [];
        }
    }
}
