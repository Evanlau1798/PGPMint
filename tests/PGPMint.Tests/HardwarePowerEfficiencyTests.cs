using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
public sealed class HardwarePowerEfficiencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealGpu30sStepDownCalibrationSelectsFourDistinctTiers()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-hw-eff-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var backend = new HybridGpuBackend();
            IReadOnlyList<GpuDeviceInfo> gpus = backend.EnumerateHardwareGpus();
            Assert.NotEmpty(gpus);

            var stateStore = new AppStateStore(tempDir);
            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromSeconds(30),
                    CalibrationWindow = TimeSpan.FromSeconds(1),
                    TelemetryInterval = TimeSpan.FromMilliseconds(250),
                    SensorPollInterval = TimeSpan.FromMilliseconds(250)
                });

            coordinator.Initialize();
            var pattern = KeyIdPattern.Parse("0000000000000000");
            await coordinator.StartSearchAsync(pattern);

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while (DateTime.UtcNow < deadline && coordinator.CurrentState != CoordinatorState.Running)
            {
                await Task.Delay(100);
            }

            Assert.Equal(CoordinatorState.Running, coordinator.CurrentState);
            CalibrationRecord rec = Assert.Single(stateStore.Load().Calibrations.Values);
            EfficiencyProfile profile = Assert.IsType<EfficiencyProfile>(rec.EfficiencyProfile);

            output.WriteLine($"GPU={rec.DeviceName} Full={rec.MedianHashesPerSecond / 1e9:F2} GH/s @ {profile.FullLoadWatts:F1} W (HasPower={profile.HasPowerSensor})");
            output.WriteLine($"  Eco:        frac={profile.Eco.RateFraction:P0} est={profile.Eco.EstimatedWatts:F1} W mode={profile.Eco.SelectionMode}");
            output.WriteLine($"  Balanced:   frac={profile.Balanced.RateFraction:P0} est={profile.Balanced.EstimatedWatts:F1} W mode={profile.Balanced.SelectionMode}");
            output.WriteLine($"  Efficiency: frac={profile.Efficiency.RateFraction:P0} est={profile.Efficiency.EstimatedWatts:F1} W mode={profile.Efficiency.SelectionMode}");
            foreach (EfficiencyCurveSample s in profile.Samples)
            {
                output.WriteLine($"  Sample {s.RateFraction:P0}: {s.MeasuredHps / 1e9:F2} GH/s | {s.MeasuredWatts:F1} W | Util {s.MeasuredGpuUtil:F0}%");
            }

            Assert.True(profile.Eco.RateFraction < profile.Balanced.RateFraction);
            Assert.True(profile.Balanced.RateFraction < profile.Efficiency.RateFraction);
            if (profile.HasPowerSensor && profile.FullLoadWatts > 0.0)
            {
                Assert.True(profile.Balanced.EstimatedWatts <= profile.FullLoadWatts * 0.60,
                    $"Expected Balanced watts ({profile.Balanced.EstimatedWatts:F1}W) <= 60% of full ({profile.FullLoadWatts:F1}W).");
                Assert.True(profile.Eco.EstimatedWatts < profile.Balanced.EstimatedWatts,
                    $"Expected Eco watts ({profile.Eco.EstimatedWatts:F1}W) < Balanced watts ({profile.Balanced.EstimatedWatts:F1}W).");
                Assert.True(profile.Efficiency.EstimatedWatts <= profile.FullLoadWatts * 0.90,
                    $"Expected Efficiency watts ({profile.Efficiency.EstimatedWatts:F1}W) <= 90% of full ({profile.FullLoadWatts:F1}W).");
            }

            coordinator.SetPowerTier(PowerTier.Efficiency);
            await Task.Delay(3500);
            CoordinatorTelemetry effSnap = coordinator.LatestTelemetry;
            output.WriteLine($"Live Efficiency: {effSnap.TotalActualHps / 1e9:F2} GH/s @ {effSnap.TotalPowerWatts:F1} W (yielding={effSnap.IsOverloadYielding})");
            Assert.False(effSnap.IsOverloadYielding);

            coordinator.SetPowerTier(PowerTier.Balanced);
            await Task.Delay(3500);
            CoordinatorTelemetry balSnap = coordinator.LatestTelemetry;
            output.WriteLine($"Live Balanced:   {balSnap.TotalActualHps / 1e9:F2} GH/s @ {balSnap.TotalPowerWatts:F1} W (yielding={balSnap.IsOverloadYielding})");
            Assert.False(balSnap.IsOverloadYielding);

            coordinator.SetPowerTier(PowerTier.Eco);
            await Task.Delay(3500);
            CoordinatorTelemetry ecoSnap = coordinator.LatestTelemetry;
            output.WriteLine($"Live Eco:        {ecoSnap.TotalActualHps / 1e9:F2} GH/s @ {ecoSnap.TotalPowerWatts:F1} W (yielding={ecoSnap.IsOverloadYielding})");
            Assert.False(ecoSnap.IsOverloadYielding);

            await coordinator.StopAsync();
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
