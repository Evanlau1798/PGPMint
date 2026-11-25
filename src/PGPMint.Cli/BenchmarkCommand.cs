using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class BenchmarkCommand
{
    internal static async Task<int> RunAsync(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> infos, CommandLine options)
    {
        var pattern = KeyIdPattern.Parse(options.Get("pattern") ?? "1145141919810114");
        var mode = options.Get("mode") == "enumerate-all" ? SearchResultMode.EnumerateAll : SearchResultMode.FindOne;
        (backend as IWorkloadGpuBackend)?.SetWorkload(pattern);
        Console.WriteLine($"runtime={Environment.Version}; platform={System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}; mode={mode}; pattern={pattern.CanonicalMask}");
        foreach (GpuDeviceInfo info in infos)
        {
            using var device = backend.CreateDevice(info);
            GpuVerification.Verify(device);
            byte[][] keys = GpuVerification.PublicKeys();
            uint count = (uint)options.Number("timestamps", (int)Math.Min(1L << 25, device.MaxCandidatesPerBatch / 4));
            if ((long)count * 4 > device.MaxCandidatesPerBatch || (ulong)1_700_000_000 + count > (1UL << 32))
                throw new ArgumentException("Timestamp count exceeds the selected device's batch or timestamp limit.");
            for (int warmup = 0; warmup < 16; warmup++) Scan();
            var samples = new List<double>();
            for (int sample = 0; sample < 31; sample++)
            {
                long started = Stopwatch.GetTimestamp();
                var result = Scan();
                samples.Add(result.UniqueCandidatesCompleted / Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
            Console.WriteLine($"micro; device={device.Info.DeviceKey}; name={device.Info.Name}; backend={device.Info.BackendName}; driver={device.Info.DriverVersion}; kernel={device.KernelVersion}; workgroup={device.Info.KernelConfiguration?.WorkgroupSize}; GHps_median={samples.Order().ElementAt(15) / 1e9:F4}");

            SearchBatchResult Scan()
            {
                var result = device.ScanBatch(keys, 1_700_000_000, count, pattern, capacity: mode == SearchResultMode.FindOne ? 1 : 1024, resultMode: mode);
                if (!result.HashRangeFullyProcessed || !result.ResultsComplete || result.UniqueCandidatesCompleted != (long)count * 4)
                    throw new InvalidOperationException("Benchmark requires a complete, semantically equivalent batch.");
                return result;
            }
        }
        if (mode != SearchResultMode.FindOne)
        {
            Console.WriteLine("End-to-end coordinator measurement omitted: the application coordinator uses FindOne. The microbenchmark above is EnumerateAll only.");
            return 0;
        }

        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-benchmark-" + Guid.NewGuid().ToString("N"));
        try
        {
            char[] secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToCharArray();
            PasswordSeedProtector protector;
            try { protector = PasswordSeedProtector.Open(directory, secret); }
            finally { Array.Clear(secret); }
            using (protector)
            using (var coordinator = new MultiGpuCoordinator(new SelectedBackend(backend, infos), new AppStateStore(directory),
                new VaultStore(directory, protector), new CoordinatorOptions { FullCalibrationDuration = TimeSpan.FromSeconds(1) }))
            using (var cancelled = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
                Console.CancelKeyPress += cancel;
                try
                {
                    coordinator.Initialize();
                    coordinator.SetAutoPauseOnHit(false);
                    await coordinator.StartSearchAsync(pattern);
                    var deadline = Stopwatch.StartNew();
                    while (coordinator.CurrentState == CoordinatorState.Calibrating && deadline.Elapsed.TotalSeconds < 120 && !cancelled.IsCancellationRequested)
                        await Task.Delay(100);
                    if (coordinator.CurrentState != CoordinatorState.Running) throw new InvalidOperationException(coordinator.LatestTelemetry.ErrorMessage ?? "Benchmark did not reach the running state.");
                    int seconds = options.Number("seconds", 60);
                    for (int round = 0; round < options.Number("rounds", 3); round++)
                    {
                        ulong before = coordinator.LatestTelemetry.SessionChecked;
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed.TotalSeconds < seconds && !cancelled.IsCancellationRequested)
                        {
                            if (coordinator.CurrentState != CoordinatorState.Running)
                                throw new InvalidOperationException(coordinator.LatestTelemetry.ErrorMessage ?? "Benchmark stopped before the requested interval completed.");
                            await Task.Delay(100);
                        }
                        if (cancelled.IsCancellationRequested) throw new InvalidOperationException("Benchmark cancelled; the requested interval is incomplete.");
                        ulong completed = coordinator.LatestTelemetry.SessionChecked - before;
                        Console.WriteLine($"end-to-end; round={round + 1}; seconds={timer.Elapsed.TotalSeconds:F3}; GHps={completed / timer.Elapsed.TotalSeconds / 1e9:F4}; saved={coordinator.LatestTelemetry.TotalMatchesCount}; gpus={infos.Count}");
                    }
                }
                finally { await coordinator.StopAsync(); Console.CancelKeyPress -= cancel; }
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        return 0;
    }
}
