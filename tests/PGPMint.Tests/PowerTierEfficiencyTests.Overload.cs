using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

public sealed partial class PowerTierEfficiencyTests
{
    [Fact]
    public void OverloadYieldControllerFillsSpareHeadroomWhileSharingGpuWithGameAndHoldsDeadband()
    {
        var profile = new EfficiencyProfile(
            FullLoadWatts: 348.0,
            FullLoadGpuUtil: 97.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.05, 46.4, 22.0, 1.20e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.15, 60.1, 29.0, 3.52e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.48, 248.4, 47.0, 11.30e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        var controller = new OverloadYieldController();

        // 1) Immediately after Max -> Efficiency switch while gaming, 1s settle grace prevents instant cliff-drop
        var switchResidual = new GpuSensorSample(true, 335.0, 350.0, true, 95.0, 45.0, 32.0);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Efficiency, profile, switchResidual, 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        // 2) Mild shared overload trims smoothly into partial scale without dropping to 0.0,
        // then recovers partially after SharedRecoveryHoldSeconds (1.5s) when spare headroom appears.
        var mildOverload = new GpuSensorSample(true, 300.0, 350.0, true, 88.0, 43.0, 45.0);
        for (int i = 0; i < 4; i++) // 1.0s (0.5s hold + 0.5s smooth trim)
        {
            controller.Update(PowerTier.Efficiency, profile, mildOverload, 0.25);
        }
        double trimmedScale = controller.YieldScale;
        Assert.InRange(trimmedScale, 0.40, 0.85);
        Assert.True(controller.IsYielding);

        var mediumGameWithHeadroom = new GpuSensorSample(true, 228.0, 350.0, true, 58.0, 0.0, 45.0);
        for (int i = 0; i < 5; i++) // 1.25s (< 1.5s SharedRecoveryHoldSeconds -> still holding)
        {
            Assert.Equal(trimmedScale, controller.Update(PowerTier.Efficiency, profile, mediumGameWithHeadroom, 0.25), 6);
        }
        for (int i = 0; i < 3; i++) // reaches 1.5s hold + 0.5s gentle ramp
        {
            controller.Update(PowerTier.Efficiency, profile, mediumGameWithHeadroom, 0.25);
        }
        Assert.True(controller.YieldScale > trimmedScale);

        // 3) Sustained heavy spike pushes Efficiency all the way down to 0.0
        var heavyGame = new GpuSensorSample(true, 320.0, 350.0, true, 94.0, 20.0, 74.0);
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, profile, heavyGame, 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);
        Assert.True(controller.IsYielding);

        // 4) Game still runs at ~45% GPU / 228 W / 58% total (plenty of headroom below 285.7 W & 85% ceiling).
        // Must stay at 0.0 for the first 2.75s (< 3.0s SharedWakeHoldSeconds), then gently wake and scale up.
        for (int i = 0; i < 11; i++) // 2.75s
        {
            Assert.Equal(0.0, controller.Update(PowerTier.Efficiency, profile, mediumGameWithHeadroom, 0.25), 6);
        }
        for (int i = 0; i < 5; i++) // reaches 3.0s wake hold + 1.0s gentle ramp
        {
            controller.Update(PowerTier.Efficiency, profile, mediumGameWithHeadroom, 0.25);
        }
        double recoveredScale = controller.YieldScale;
        Assert.InRange(recoveredScale, 0.15, 0.70);
        Assert.True(controller.IsYielding);

        // 5) Once total power/util enters the deadband (272 W in [263..286], 79% in [75..85]),
        // YieldScale must hold completely steady without oscillating up or down.
        var equilibrium = new GpuSensorSample(true, 272.0, 350.0, true, 79.0, 34.0, 45.0);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(recoveredScale, controller.Update(PowerTier.Efficiency, profile, equilibrium, 0.25), 6);
        }

        // 6) Meanwhile in Balanced (ceiling ~85 W), the same 228 W game consumes the entire budget -> stays at 0.0.
        controller.Reset();
        for (int i = 0; i < 20; i++)
        {
            controller.Update(PowerTier.Balanced, profile, mediumGameWithHeadroom, 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);
    }

    [Fact]
    public void OverloadYieldControllerRecoversToFullOnLowPowerWarmAndUtilOnlyGpus()
    {
        // A) Low-power 75 W laptop GPU with Efficiency calibrated at 75% (56.25 W, ceiling 66 W)
        var laptopProfile = new EfficiencyProfile(
            FullLoadWatts: 75.0,
            FullLoadGpuUtil: 96.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.10, 18.0, 20.0, 0.4e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.30, 38.0, 42.0, 1.2e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.60, 56.25, 68.0, 2.4e9, "upper-mid-fallback"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        var controller = new OverloadYieldController();
        for (int i = 0; i < 8; i++)
        {
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, 56.25, 75.0, true, 70.0, 68.0, 2.0), 0.25);
        }
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, 72.0, 75.0, true, 96.0, 26.0, 70.0), 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);

        // Game closes: self wattage rises proportionally from 10 W idle up to 56.25 W as YieldScale recovers -> must reach 1.0
        for (int i = 0; i < 24; i++)
        {
            double selfWatts = 10.0 + (controller.YieldScale * 46.25);
            double selfUtil = 2.0 + (controller.YieldScale * 68.0);
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, selfWatts, 75.0, true, selfUtil, selfUtil - 2.0, 2.0), 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);
        Assert.False(controller.IsYielding);

        // B) Power-only fallback (no D3DKMT util) on the same 75 W laptop GPU
        controller.Reset();
        for (int i = 0; i < 16; i++) // clear 3s power cooldown grace
        {
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, 56.25, 75.0, false, 0.0, 0.0, 0.0), 0.25);
        }
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, 72.0, 75.0, false, 0.0, 0.0, 0.0), 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);
        for (int i = 0; i < 40; i++)
        {
            double selfWatts = 10.0 + (controller.YieldScale * 46.25);
            controller.Update(PowerTier.Efficiency, laptopProfile, new GpuSensorSample(true, selfWatts, 75.0, false, 0.0, 0.0, 0.0), 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);

        // C) Warm 348 W GPU after gaming running 6% warmer than calibrated (264 W vs 248.4 W, ceiling 285.7 W)
        var warmProfile = new EfficiencyProfile(
            FullLoadWatts: 348.0,
            FullLoadGpuUtil: 97.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.05, 46.4, 22.0, 1.2e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.15, 60.1, 29.0, 3.5e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.48, 248.4, 47.0, 11.3e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());
        controller.Reset();
        for (int i = 0; i < 6; i++) controller.Update(PowerTier.Efficiency, warmProfile, new GpuSensorSample(true, 248.0, 350.0, true, 50.0, 47.0, 3.0), 0.25);
        for (int i = 0; i < 16; i++) controller.Update(PowerTier.Efficiency, warmProfile, new GpuSensorSample(true, 330.0, 350.0, true, 95.0, 20.0, 75.0), 0.25);
        Assert.Equal(0.0, controller.YieldScale, 6);
        for (int i = 0; i < 24; i++)
        {
            double selfWatts = 40.0 + (controller.YieldScale * 224.0); // reaches 264 W at YieldScale=1.0
            controller.Update(PowerTier.Efficiency, warmProfile, new GpuSensorSample(true, selfWatts, 350.0, true, 52.0, 47.0, 5.0), 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);

        // D) Non-NVIDIA GPU (util-only) with Balanced at 54% and Efficiency at 74% -> must recover all the way to 1.0
        var amdProfile = new EfficiencyProfile(
            FullLoadWatts: 0.0,
            FullLoadGpuUtil: 96.0,
            HasPowerSensor: false,
            Eco: new TierOperatingPoint(0.12, 0.0, 26.0, 1.2e9, "gpu-util-fallback"),
            Balanced: new TierOperatingPoint(0.35, 0.0, 54.0, 3.5e9, "gpu-util-fallback"),
            Efficiency: new TierOperatingPoint(0.60, 0.0, 74.0, 6.0e9, "gpu-util-fallback"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        foreach ((PowerTier tier, double tierUtil) in new[] { (PowerTier.Balanced, 54.0), (PowerTier.Efficiency, 74.0) })
        {
            controller.Reset();
            for (int i = 0; i < 6; i++) controller.Update(tier, amdProfile, new GpuSensorSample(false, 0.0, 0.0, true, tierUtil, tierUtil, 0.0), 0.25);
            for (int i = 0; i < 16; i++) controller.Update(tier, amdProfile, new GpuSensorSample(false, 0.0, 0.0, true, 94.0, 20.0, 74.0), 0.25);
            Assert.Equal(0.0, controller.YieldScale, 6);

            for (int i = 0; i < 24; i++)
            {
                double selfUtil = controller.YieldScale * tierUtil;
                controller.Update(tier, amdProfile, new GpuSensorSample(false, 0.0, 0.0, true, selfUtil + 4.0, selfUtil, 4.0), 0.25);
            }
            Assert.Equal(1.0, controller.YieldScale, 6);
            Assert.False(controller.IsYielding);
        }

        // E) Efficiency calibrated near 87.4% of FullLoadWatts (305 W / 349 W) must stay at 1.0 when alone (even warm at 312 W),
        // fill headroom when a medium game runs at 228 W / 45%, and yield smoothly when a heavy game hits 338 W / 92%.
        var upperEffProfile = new EfficiencyProfile(
            FullLoadWatts: 349.0,
            FullLoadGpuUtil: 96.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.10, 53.0, 22.0, 2.35e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.26, 103.0, 31.0, 6.11e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.72, 305.0, 72.0, 16.92e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        controller.Reset();
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Efficiency, upperEffProfile, new GpuSensorSample(true, 312.0, 350.0, true, 76.0, 72.0, 4.0), 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, upperEffProfile, new GpuSensorSample(true, 338.0, 350.0, true, 94.0, 24.0, 70.0), 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);

        // Medium game leaves headroom below Efficiency's ~307 W recovery threshold -> wakes and runs
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, upperEffProfile, new GpuSensorSample(true, 228.0, 350.0, true, 58.0, 0.0, 45.0), 0.25);
        }
        Assert.InRange(controller.YieldScale, 0.15, 0.70);

        // F) 75 W laptop GPU with Efficiency calibrated at 88% (66 W): hardPowerCeiling must remain < 75 W (trips at 74.5 W
        // even without OtherProcessGpuUtil), and calm recovery must still reach 1.0 even when warm at 70 W.
        var laptop88Profile = new EfficiencyProfile(
            FullLoadWatts: 75.0,
            FullLoadGpuUtil: 96.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.10, 18.0, 20.0, 0.4e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.30, 38.0, 42.0, 1.2e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.72, 66.0, 74.0, 2.9e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        controller.Reset();
        for (int i = 0; i < 16; i++) // clear 3s power cooldown grace while running alone warm at 70 W
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Efficiency, laptop88Profile, new GpuSensorSample(true, 70.0, 75.0, true, 76.0, 74.0, 2.0), 0.25), 6);
        }
        // Runaway load at 74.5 W (< 75 W TDP) with low reported OtherProcessGpuUtil must still trip hardPowerCeiling
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Efficiency, laptop88Profile, new GpuSensorSample(true, 74.5, 75.0, true, 98.0, 90.0, 8.0), 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);
        // After load clears, warm laptop running up to 69.5 W recovers cleanly back to 1.0
        for (int i = 0; i < 24; i++)
        {
            double selfWatts = 12.0 + (controller.YieldScale * 57.5); // 69.5 W at YieldScale=1.0
            controller.Update(PowerTier.Efficiency, laptop88Profile, new GpuSensorSample(true, selfWatts, 75.0, true, 76.0, 74.0, 2.0), 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);
    }

    [Fact]
    public void OverloadYieldControllerFillsSpareHeadroomOnGpuUtilOnlyFallback()
    {
        var profile = new EfficiencyProfile(
            FullLoadWatts: 0.0,
            FullLoadGpuUtil: 95.0,
            HasPowerSensor: false,
            Eco: new TierOperatingPoint(0.10, 0.0, 20.0, 1.0e9, "gpu-util-fallback"),
            Balanced: new TierOperatingPoint(0.30, 0.0, 45.0, 3.0e9, "gpu-util-fallback"),
            Efficiency: new TierOperatingPoint(0.55, 0.0, 68.0, 5.5e9, "gpu-util-fallback"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        var controller = new OverloadYieldController();

        // Advance past switch grace, then drive to 0.0 with heavy game util
        var normal = new GpuSensorSample(false, 0.0, 0.0, true, 60.0, 52.0, 8.0);
        for (int i = 0; i < 8; i++) controller.Update(PowerTier.Efficiency, profile, normal, 0.25);

        var heavyUtil = new GpuSensorSample(false, 0.0, 0.0, true, 92.0, 20.0, 72.0);
        for (int i = 0; i < 16; i++) controller.Update(PowerTier.Efficiency, profile, heavyUtil, 0.25);
        Assert.Equal(0.0, controller.YieldScale, 6);

        // Game eases to 45% other / 55% total (< 70% Efficiency recovery threshold) -> wakes and fills headroom
        var sharedHeadroom = new GpuSensorSample(false, 0.0, 0.0, true, 55.0, 0.0, 45.0);
        for (int i = 0; i < 16; i++) controller.Update(PowerTier.Efficiency, profile, sharedHeadroom, 0.25);
        double scale = controller.YieldScale;
        Assert.InRange(scale, 0.15, 0.70);

        // Deadband [70%..82%] holds steady
        var deadband = new GpuSensorSample(false, 0.0, 0.0, true, 76.0, 31.0, 45.0);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(scale, controller.Update(PowerTier.Efficiency, profile, deadband, 0.25), 6);
        }
    }

    [Fact]
    public async Task StepDownCalibrationPopulatesEfficiencyProfileAndSupportsManualRecalibration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-tier-cal-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 42, "Mock RTX 3080", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 180.0, 350.0, true, 60.0, 55.0, 5.0));
            var stateStore = new AppStateStore(tempDir);
            var vault = new DpapiVaultStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                vault,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(420),
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    TelemetryInterval = TimeSpan.FromMilliseconds(30),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            coordinator.SetTargetPattern(pattern);
            Assert.True(coordinator.NeedsFullCalibration());

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => coordinator.CurrentState == CoordinatorState.Running, TimeSpan.FromSeconds(5));

            Assert.False(coordinator.NeedsFullCalibration());
            Assert.True(coordinator.LatestTelemetry.AllEnabledFullyCalibrated);
            Assert.True(coordinator.LatestTelemetry.HasPowerSensor);
            Assert.True(coordinator.LatestTelemetry.TotalPowerWatts > 0.0);

            Assert.False(coordinator.InvalidateCalibrationsForRecalibration());

            AppPersistedState persisted = stateStore.Load();
            CalibrationRecord rec = Assert.Single(persisted.Calibrations.Values);
            Assert.NotNull(rec.EfficiencyProfile);
            Assert.True(rec.EfficiencyProfile!.HasPowerSensor);
            Assert.True(rec.EfficiencyProfile.Samples.Count >= 6);
            Assert.True(rec.EfficiencyProfile.Samples.Max(s => s.RateFraction) > 0.65);
            Assert.True(rec.EfficiencyProfile.Samples.Min(s => s.RateFraction) < 0.20);
            Assert.StartsWith("v3/", rec.WorkloadIdentity, StringComparison.Ordinal);
            Assert.True(rec.EfficiencyProfile.Eco.RateFraction < rec.EfficiencyProfile.Balanced.RateFraction);
            Assert.True(rec.EfficiencyProfile.Balanced.RateFraction < rec.EfficiencyProfile.Efficiency.RateFraction);

            await coordinator.StopAsync();
            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);

            Assert.True(coordinator.InvalidateCalibrationsForRecalibration());
            Assert.True(coordinator.NeedsFullCalibration());
            Assert.Empty(stateStore.Load().Calibrations);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StoppingDuringStage2StepDownCalibrationDoesNotPersistPartialProfile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-stage2-stop-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 42, "Mock GPU", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 180.0, 350.0, true, 60.0, 55.0, 5.0));
            var stateStore = new AppStateStore(tempDir);

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(1600), // Stage 1 ~272ms, Stage 2 ~1328ms
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    TelemetryInterval = TimeSpan.FromMilliseconds(30),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("FFFFFFFFFFFFFFFF"));

            // Wait until well inside Stage 2 step-down sweep (~600ms elapsed, ~1000ms remaining)
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Calibrating &&
                coordinator.LatestTelemetry.CalibrationRemainingSeconds is > 0.2 and < 1.15,
                TimeSpan.FromSeconds(4));

            await coordinator.StopAsync();

            Assert.Equal(CoordinatorState.Idle, coordinator.CurrentState);
            Assert.True(coordinator.NeedsFullCalibration());
            Assert.Empty(stateStore.Load().Calibrations);
            Assert.True(stateStore.Load().TotalCheckedCandidates > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
