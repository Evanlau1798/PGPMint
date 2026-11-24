using System.Diagnostics;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
public sealed class CoordinatorAdaptiveGpuTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Hybrid")]
    [InlineData("OpenCL")]
    [InlineData("DirectCompute")]
    public async Task MeasuresFullAndCappedBatchesAndStopLatency(string backendKind)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-adaptive-" + Guid.NewGuid().ToString("N"));
        try
        {
            IGpuBackend backend = backendKind switch
            {
                "OpenCL" => new HybridGpuBackend(null, enableOpenCl: true, enableVulkan: false),
                "DirectCompute" => new DirectComputeBackend(),
                _ => new HybridGpuBackend()
            };
            var options = new CoordinatorOptions
            {
                CalibrationWindow = TimeSpan.FromMilliseconds(125),
                FullCalibrationDuration = TimeSpan.FromMilliseconds(750),
                TelemetryInterval = TimeSpan.FromMilliseconds(45)
            };
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), new DpapiVaultStore(directory), options);
            coordinator.Initialize();
            var fullSpeed = ObserveBatchTimes(coordinator, fullSpeed: true);
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("0000000000000000"));
            double[] fullBatchTimes = await fullSpeed.WaitAsync(TimeSpan.FromSeconds(10));
            var capped = ObserveBatchTimes(coordinator, fullSpeed: false);
            coordinator.SetSliderPosition(0.5);
            double[] cappedBatchTimes = await capped.WaitAsync(TimeSpan.FromSeconds(10));

            long started = Stopwatch.GetTimestamp();
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            double stopWallMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.Equal(0L, coordinator.LatestTelemetry.SessionWork.RescanEvaluations);

            GpuDeviceInfo device = coordinator.GetEnabledDevices()[0];
            output.WriteLine($"{device.Name} {device.DriverVersion} {device.BackendName}/{device.KernelVersion} " +
                $"full batch ms={Format(fullBatchTimes)} capped batch ms={Format(cappedBatchTimes)} stop ms={stopWallMilliseconds:F2}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Format(IEnumerable<double> samples) =>
        string.Join(", ", samples.Select(ms => FormattableString.Invariant($"{ms:F2}")));

    private static Task<double[]> ObserveBatchTimes(MultiGpuCoordinator coordinator, bool fullSpeed)
    {
        var result = new TaskCompletionSource<double[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new List<double>();
        coordinator.TelemetryUpdated += OnTelemetry;
        return result.Task;

        void OnTelemetry(object? sender, CoordinatorTelemetry telemetry)
        {
            if (telemetry.State != CoordinatorState.Running || telemetry.IsFullSpeed != fullSpeed)
            {
                return;
            }

            GpuTelemetrySnapshot device = telemetry.Devices.First(gpu => gpu.IsEnabled);
            if (!device.IsFullCalibrated || device.LastBatchWallMilliseconds <= 0 ||
                (!fullSpeed && device.TargetHps <= 0))
            {
                return;
            }

            lock (samples)
            {
                samples.Add(device.LastBatchWallMilliseconds);
                if (samples.Count == 6)
                {
                    coordinator.TelemetryUpdated -= OnTelemetry;
                    result.TrySetResult(samples.Skip(1).ToArray());
                }
            }
        }
    }
}
