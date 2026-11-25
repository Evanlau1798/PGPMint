using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

public sealed class PowerTierEfficiencyTests
{
    [Theory]
    [InlineData(0.0, "0.0 MH/W")]
    [InlineData(-5.0, "0.0 MH/W")]
    [InlineData(double.NaN, "0.0 MH/W")]
    [InlineData(850_000.0, "0.85 MH/W")]
    [InlineData(996_000.0, "1.0 MH/W")]
    [InlineData(9_200_000.0, "9.2 MH/W")]
    [InlineData(32_000_000.0, "32.0 MH/W")]
    [InlineData(46_500_000.0, "46.5 MH/W")]
    [InlineData(99_940_000.0, "99.9 MH/W")]
    [InlineData(99_960_000.0, "0.10 GH/W")]
    [InlineData(100_000_000.0, "0.10 GH/W")]
    [InlineData(145_000_000.0, "0.15 GH/W")]
    [InlineData(1_234_000_000.0, "1.23 GH/W")]
    [InlineData(99_996_000_000.0, "100.0 GH/W")]
    [InlineData(125_400_000_000.0, "125.4 GH/W")]
    public void FormatEfficiencyPerWattPromotesAt100MhPerWatt(double hashesPerWatt, string expected)
    {
        Assert.Equal(expected, FormattingHelpers.FormatEfficiencyPerWatt(hashesPerWatt));
    }

    [Fact]
    public void CurveSelectorFindsFourDistinctTiersWithinCardAgnosticPowerBandsOnRtx3080Profile()
    {
        // Full descending curve on an RTX 3080 (23.5 GH/s @ 349 W) spanning 92% -> 5%
        EfficiencyCurveSample[] samples =
        [
            new(0.90, 21.15e9, 338.0, 91.0), // 96.8% W (> 88% Efficiency cap)
            new(0.82, 19.27e9, 326.0, 84.0), // 93.4% W (> 88% Efficiency cap)
            new(0.76, 17.86e9, 314.0, 77.0), // 90.0% W (> 88% Efficiency cap)
            new(0.72, 16.92e9, 305.0, 72.0), // 87.4% W (<= 88%, 55.5 MH/W -> Efficiency!)
            new(0.66, 15.51e9, 296.0, 66.0), // 84.8% W (52.4 MH/W)
            new(0.60, 14.10e9, 287.0, 58.0), // 82.2% W (49.1 MH/W)
            new(0.48, 11.28e9, 248.0, 47.0), // 71.1% W (45.5 MH/W, lower P0)
            new(0.35,  8.23e9, 227.0, 34.0), // 65.0% W (36.3 MH/W, P0 valley)
            new(0.26,  6.11e9, 103.0, 31.0), // 29.5% W (59.3 MH/W, P3 knee -> Balanced!)
            new(0.22,  5.17e9,  98.0, 28.0), // 28.1% W (52.8 MH/W)
            new(0.15,  3.53e9,  60.2, 26.0), // 17.2% W (58.6 MH/W, within 3% of 26% peak -> 26% wins on speed!)
            new(0.10,  2.35e9,  53.0, 22.0), // 15.2% W (P5 plateau knee -> Eco!)
            new(0.08,  1.88e9,  51.3, 21.0),
            new(0.05,  1.18e9,  46.4, 17.0)
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(23.5e9, 349.0, 96.0, samples);

        Assert.True(profile.HasPowerSensor);
        Assert.Equal(0.15, profile.Eco.RateFraction, 6);
        Assert.Equal("natural-plateau", profile.Eco.SelectionMode);
        Assert.Equal(0.26, profile.Balanced.RateFraction, 6);
        Assert.Equal("natural-peak", profile.Balanced.SelectionMode);
        Assert.Equal(0.72, profile.Efficiency.RateFraction, 6);
        Assert.Equal("upper-mid-peak", profile.Efficiency.SelectionMode);
        Assert.InRange(profile.Efficiency.RateFraction, 0.65, 0.78);
        Assert.True(profile.Efficiency.EstimatedWatts <= 349.0 * 0.88);
        Assert.True(profile.Efficiency.MeasuredHps / profile.Efficiency.EstimatedWatts > 11.28e9 / 248.0);
        Assert.True(profile.Eco.RateFraction < profile.Balanced.RateFraction);
        Assert.True(profile.Balanced.RateFraction < profile.Efficiency.RateFraction);
    }

    [Fact]
    public void CurveSelectorFullRampCapturesHighEfficiencyUpperMidAndConcaveSchedule()
    {
        // Concave ramp schedule: 0.92 at u=0 down to 0.05 at u=1, spending > 50% of ramp time below 35%
        Assert.Equal(0.92, EfficiencyCurveSelector.RampFractionAtProgress(0.0), 6);
        Assert.Equal(0.05, EfficiencyCurveSelector.RampFractionAtProgress(1.0), 6);
        double midFrac = EfficiencyCurveSelector.RampFractionAtProgress(0.5);
        Assert.InRange(midFrac, 0.25, 0.36);
        Assert.True(EfficiencyCurveSelector.RampFractionAtProgress(0.25) > midFrac);
        Assert.True(midFrac > EfficiencyCurveSelector.RampFractionAtProgress(0.75));

        // Efficient Ada-class GPU where 70% rate draws only 64% power (peak 85.4 MH/W > Max 78.1 MH/W)
        EfficiencyCurveSample[] adaSamples =
        [
            new(0.86, 21.5e9, 286.0, 86.0),
            new(0.75, 18.8e9, 224.0, 75.0), // 70.0% W, 83.9 MH/W
            new(0.70, 17.5e9, 205.0, 70.0), // 64.1% W, 85.4 MH/W -> Efficiency winner!
            new(0.60, 15.0e9, 182.0, 60.0), // 56.9% W, 82.4 MH/W
            new(0.45, 11.3e9, 145.0, 45.0),
            new(0.30,  7.5e9,  95.0, 30.0), // 29.7% W, 78.9 MH/W -> Balanced
            new(0.10,  2.5e9,  42.0, 12.0), // 13.1% W -> Eco
            new(0.05,  1.25e9, 36.0,  8.0)
        ];
        EfficiencyProfile ada = EfficiencyCurveSelector.Select(25.0e9, 320.0, 97.0, adaSamples);
        Assert.Equal(0.70, ada.Efficiency.RateFraction, 6);
        Assert.Equal("upper-mid-peak", ada.Efficiency.SelectionMode);

        // Thermal-wall GPU: 75% (93% W) and 72% (90% W) exceed 88% cap; 65% (76% W) wins cleanly
        EfficiencyCurveSample[] wallSamples =
        [
            new(0.78, 15.6e9, 285.0, 82.0), // 95% W
            new(0.72, 14.4e9, 270.0, 76.0), // 90% W (> 88% cap)
            new(0.65, 13.0e9, 228.0, 66.0), // 76% W (57.0 MH/W -> Efficiency!)
            new(0.58, 11.6e9, 215.0, 60.0), // 71.7% W (54.0 MH/W)
            new(0.30,  6.0e9, 120.0, 34.0), // 40% W -> Balanced
            new(0.10,  2.0e9,  62.0, 14.0), // 20.7% W -> Eco
            new(0.05,  1.0e9,  56.0, 10.0)
        ];
        EfficiencyProfile wall = EfficiencyCurveSelector.Select(20.0e9, 300.0, 96.0, wallSamples);
        Assert.Equal(0.65, wall.Efficiency.RateFraction, 6);
    }

    [Fact]
    public void CurveSelectorSmoothsSingleTickPowerSpikeOnDenseContinuousRamp()
    {
        // 16-point dense ramp with a single-sample sensor glitch (95 W dip at 62%) AND an artificial
        // tail short-bin MeasuredHps spike at 5% (5x inflated) — neither may hijack Efficiency or Balanced.
        EfficiencyCurveSample[] dense =
        [
            new(0.90, 18.0e9, 290.0, 90.0),
            new(0.84, 16.8e9, 276.0, 84.0),
            new(0.76, 15.2e9, 260.0, 76.0), // 86.7% W, 58.5 MH/W
            new(0.72, 14.4e9, 248.0, 72.0), // 82.7% W, 58.1 MH/W
            new(0.68, 13.6e9, 236.0, 68.0), // 78.7% W, 57.6 MH/W
            new(0.62, 12.4e9,  95.0, 62.0), // Single-tick NVML glitch (neighbors are 236W & 226W -> smoothed to 226W = 54.9 MH/W!)
            new(0.58, 11.6e9, 226.0, 58.0),
            new(0.50, 10.0e9, 212.0, 50.0),
            new(0.40,  8.0e9, 195.0, 40.0),
            new(0.32,  6.4e9, 140.0, 32.0),
            new(0.26,  5.2e9,  95.0, 26.0), // 54.7 MH/W -> Balanced
            new(0.22,  4.4e9,  88.0, 22.0),
            new(0.16,  3.2e9,  76.0, 16.0),
            new(0.12,  2.4e9,  56.0, 12.0),
            new(0.08,  1.6e9,  49.0,  8.0),
            new(0.05,  5.5e9,  45.0,  5.0)  // Artificial 5.5x tail-bin spike: must be clamped so Balanced stays at 26%
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(20.0e9, 300.0, 96.0, dense);
        Assert.NotEqual(0.62, profile.Efficiency.RateFraction, 6);
        Assert.Equal(0.76, profile.Efficiency.RateFraction, 6);
        Assert.Equal(0.26, profile.Balanced.RateFraction, 6);
        Assert.NotEqual("synthetic-eco", profile.Eco.SelectionMode);
        Assert.True(profile.Eco.RateFraction < profile.Balanced.RateFraction);

        // Edge case: all samples > MaxBalancedFraction (0.45) must still preserve Eco < Balanced < Efficiency <= 0.85
        EfficiencyCurveSample[] highOnlyPower =
        [
            new(0.90, 18.0e9, 270.0, 90.0),
            new(0.82, 16.4e9, 245.0, 82.0)
        ];
        EfficiencyProfile highPower = EfficiencyCurveSelector.Select(20.0e9, 300.0, 95.0, highOnlyPower);
        Assert.True(highPower.Eco.RateFraction < highPower.Balanced.RateFraction);
        Assert.True(highPower.Balanced.RateFraction < highPower.Efficiency.RateFraction);
        Assert.True(highPower.Efficiency.RateFraction <= EfficiencyCurveSelector.MaxSubFullFraction);

        EfficiencyCurveSample[] highOnlyUtil =
        [
            new(0.90, 18.0e9, 0.0, 90.0),
            new(0.82, 16.4e9, 0.0, 82.0)
        ];
        EfficiencyProfile highUtil = EfficiencyCurveSelector.Select(20.0e9, 0.0, 95.0, highOnlyUtil);
        Assert.True(highUtil.Eco.RateFraction < highUtil.Balanced.RateFraction);
        Assert.True(highUtil.Balanced.RateFraction < highUtil.Efficiency.RateFraction);
        Assert.True(highUtil.Efficiency.RateFraction <= EfficiencyCurveSelector.MaxSubFullFraction);
    }

    [Fact]
    public void WddmNodeMetadataStructMatchesWdkPack1LayoutOf78Bytes()
    {
        // WDK d3dkmdt.h wraps _DXGK_NODEMETADATA and _D3DKMT_NODEMETADATA under #pragma pack(push, 1):
        // 4 (NodeOrdinalAndAdapterIndex) + 4 (EngineType) + 64 (WCHAR[32]) + 4 (Flags) + 1 + 1 = 78 bytes.
        Assert.Equal(78, WindowsGpuTelemetrySensor.NodeMetadataStructSize);
        Assert.Equal(78, WindowsGpuTelemetrySensor.NodeMetadataMarshalSize);
    }

    [Theory]
    [InlineData(1, "3D", true)]                    // DXGK_ENGINE_TYPE_3D
    [InlineData(5, "SceneAssembly", true)]         // DXGK_ENGINE_TYPE_SCENE_ASSEMBLY
    [InlineData(0, "Compute", true)]               // DXGK_ENGINE_TYPE_OTHER (Compute)
    [InlineData(0, "High Priority Cuda", true)]    // DXGK_ENGINE_TYPE_OTHER (CUDA)
    [InlineData(0, "VR", true)]                    // DXGK_ENGINE_TYPE_OTHER (VR)
    [InlineData(0, "vr_0", true)]                  // DXGK_ENGINE_TYPE_OTHER (VR indexed)
    [InlineData(2, "VideoDecode", false)]          // DXGK_ENGINE_TYPE_VIDEO_DECODE
    [InlineData(3, "VideoEncode", false)]          // DXGK_ENGINE_TYPE_VIDEO_ENCODE (Discord Clips / NVENC)
    [InlineData(4, "VideoProcessing", false)]      // DXGK_ENGINE_TYPE_VIDEO_PROCESSING
    [InlineData(6, "Copy", false)]                 // DXGK_ENGINE_TYPE_COPY
    [InlineData(7, "Overlay", false)]              // DXGK_ENGINE_TYPE_OVERLAY
    [InlineData(8, "Crypto", false)]               // DXGK_ENGINE_TYPE_CRYPTO
    [InlineData(9, "VideoCodec", false)]           // DXGK_ENGINE_TYPE_VIDEO_CODEC
    [InlineData(0, "Security", false)]             // DXGK_ENGINE_TYPE_OTHER non-compute
    [InlineData(0, "OFA_0", false)]                // Optical Flow Accelerator
    [InlineData(0, "LegacyOverlay", false)]        // Legacy overlay
    public void WddmEngineFilterExcludesVideoEncodeDecodeAndCopyWhileKeeping3DAndCompute(
        int engineType,
        string friendlyName,
        bool expectedContending)
    {
        Assert.Equal(expectedContending, WindowsGpuTelemetrySensor.IsComputeContendingEngine(engineType, friendlyName));
    }

    [Fact]
    public void CurveSelectorFallsBackToPowerBandsWhenNaturalPeakDoesNotSeparateFromFullSpeed()
    {
        EfficiencyCurveSample[] samples =
        [
            new(0.60, 13.5e9, 210.0, 66.0), // 70% of 300W (<= 72%, 64.3 MH/W -> Efficiency)
            new(0.48, 10.6e9, 195.0, 56.0), // 65% of 300W
            new(0.35,  7.7e9, 155.0, 44.0), // 51.7% of 300W (<= 55% -> Balanced)
            new(0.22,  5.0e9, 125.0, 32.0),
            new(0.15,  3.2e9, 105.0, 24.0),
            new(0.08,  1.8e9,  84.0, 16.0), // 28% of 300W -> Eco
            new(0.05,  1.1e9,  75.0, 12.0)
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(22.0e9, 300.0, 95.0, samples);

        Assert.True(profile.HasPowerSensor);
        Assert.Equal(0.08, profile.Eco.RateFraction, 6);
        Assert.True(profile.Eco.EstimatedWatts <= 300.0 * 0.32);
        Assert.Equal(0.35, profile.Balanced.RateFraction, 6);
        Assert.Equal(0.60, profile.Efficiency.RateFraction, 6);
        Assert.Equal("upper-mid-peak", profile.Efficiency.SelectionMode);
    }

    [Fact]
    public void CurveSelectorBalancedPoolNeverStealsUpperMidSamplesFromEfficiency()
    {
        // Even if 0.60 sample reports <= 55% watts, Balanced must stay <= MaxBalancedFraction (0.45)
        EfficiencyCurveSample[] samples =
        [
            new(0.60, 15.0e9, 150.0, 62.0), // 50% of 300W, 100 MH/W -> reserved for Efficiency
            new(0.48, 11.5e9, 140.0, 50.0),
            new(0.35,  7.0e9, 195.0, 40.0), // 65% (> 60%, triggers bandBal fallback)
            new(0.22,  4.2e9, 155.0, 30.0), // 51.7% (<= 55%, <= 0.45 -> Balanced winner!)
            new(0.15,  2.8e9, 120.0, 22.0),
            new(0.08,  1.4e9,  80.0, 14.0), // 26.7% -> Eco
            new(0.05,  0.9e9,  72.0, 10.0)
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(22.0e9, 300.0, 95.0, samples);

        Assert.Equal(0.22, profile.Balanced.RateFraction, 6);
        Assert.Equal("power-band-fallback", profile.Balanced.SelectionMode);
        Assert.Equal(0.60, profile.Efficiency.RateFraction, 6);
        Assert.Equal("upper-mid-peak", profile.Efficiency.SelectionMode);
        Assert.True(profile.Eco.RateFraction < profile.Balanced.RateFraction);
        Assert.True(profile.Balanced.RateFraction < profile.Efficiency.RateFraction);
    }

    [Fact]
    public void CurveSelectorUsesGpuUtilizationFallbackWhenPowerSensorIsUnavailable()
    {
        EfficiencyCurveSample[] samples =
        [
            new(0.60, 6.0e9, 0.0, 70.0), // <= 75% util -> Efficiency
            new(0.48, 4.8e9, 0.0, 58.0),
            new(0.35, 3.5e9, 0.0, 46.0), // <= 55% util & <= 0.45 -> Balanced
            new(0.22, 2.2e9, 0.0, 34.0),
            new(0.15, 1.5e9, 0.0, 24.0), // <= 28% util -> Eco
            new(0.08, 0.8e9, 0.0, 14.0),
            new(0.05, 0.5e9, 0.0, 10.0)
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(10.0e9, 0.0, 96.0, samples);

        Assert.False(profile.HasPowerSensor);
        Assert.Equal(0.15, profile.Eco.RateFraction, 6);
        Assert.Equal(0.35, profile.Balanced.RateFraction, 6);
        Assert.Equal(0.60, profile.Efficiency.RateFraction, 6);
        Assert.Equal("gpu-util-fallback", profile.Eco.SelectionMode);
        Assert.Equal("gpu-util-fallback", profile.Balanced.SelectionMode);
        Assert.Equal("gpu-util-fallback", profile.Efficiency.SelectionMode);
    }

    [Fact]
    public void CurveSelectorHandlesFlatConstantPowerSensorWithoutThrowing()
    {
        EfficiencyCurveSample[] samples =
        [
            new(0.60, 6.0e9, 45.0, 92.0),
            new(0.48, 4.8e9, 45.0, 92.0),
            new(0.35, 3.5e9, 45.0, 92.0),
            new(0.22, 2.2e9, 45.0, 92.0),
            new(0.15, 1.5e9, 45.0, 92.0),
            new(0.08, 0.8e9, 45.0, 92.0),
            new(0.05, 0.5e9, 45.0, 92.0)
        ];

        EfficiencyProfile profile = EfficiencyCurveSelector.Select(10.0e9, 45.0, 92.0, samples);

        Assert.True(profile.HasPowerSensor);
        Assert.True(profile.Eco.RateFraction < profile.Balanced.RateFraction);
        Assert.True(profile.Balanced.RateFraction < profile.Efficiency.RateFraction);
    }

    [Fact]
    public void OverloadYieldControllerIgnoresDesktopNoiseYieldsOnHeavyOtherAppAndRecoversAfterCalm()
    {
        var profile = new EfficiencyProfile(
            FullLoadWatts: 346.5,
            FullLoadGpuUtil: 85.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.08, 52.5, 19.0, 1.68e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.22, 96.9, 35.0, 4.50e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.48, 240.0, 48.0, 10.40e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        var controller = new OverloadYieldController();

        var normalBal = new GpuSensorSample(true, 100.0, 350.0, true, 36.0, 28.0, 8.0);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Balanced, profile, normalBal, 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        var normalEff = new GpuSensorSample(true, 248.0, 350.0, true, 56.0, 48.0, 8.0);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Efficiency, profile, normalEff, 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        var gameSample = new GpuSensorSample(true, 260.0, 350.0, true, 90.0, 25.0, 65.0);
        for (int i = 0; i < 12; i++)
        {
            controller.Update(PowerTier.Balanced, profile, gameSample, 0.25);
        }
        Assert.True(controller.IsYielding);
        Assert.Equal(0.0, controller.YieldScale, 6);

        var calmSample = new GpuSensorSample(true, 50.0, 350.0, true, 8.0, 0.0, 8.0);
        for (int i = 0; i < 6; i++)
        {
            controller.Update(PowerTier.Balanced, profile, calmSample, 0.25);
        }
        Assert.Equal(0.0, controller.YieldScale, 6);

        for (int i = 0; i < 10; i++)
        {
            controller.Update(PowerTier.Balanced, profile, calmSample, 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);
        Assert.False(controller.IsYielding);

        for (int i = 0; i < 6; i++)
        {
            controller.Update(PowerTier.Balanced, profile, gameSample, 0.25);
        }
        Assert.True(controller.IsYielding);
        Assert.Equal(1.0, controller.Update(PowerTier.Max, profile, gameSample, 0.25), 6);
        Assert.False(controller.IsYielding);
    }

    [Fact]
    public void OverloadYieldControllerHandlesGpuUtilizationOnlyFallbackForNonNvidiaGpus()
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

        // Normal desktop noise (10% other util, 55% total) in Balanced (< 65% ceiling) must NOT yield
        var normal = new GpuSensorSample(false, 0.0, 0.0, true, 55.0, 45.0, 10.0);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(1.0, controller.Update(PowerTier.Balanced, profile, normal, 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        // Another app loads GPU (other 55%, total 90% >= 65% ceiling) -> yields to 0.0
        var busyOther = new GpuSensorSample(false, 0.0, 0.0, true, 90.0, 35.0, 55.0);
        for (int i = 0; i < 12; i++)
        {
            controller.Update(PowerTier.Balanced, profile, busyOther, 0.25);
        }
        Assert.True(controller.IsYielding);
        Assert.Equal(0.0, controller.YieldScale, 6);

        // Other app exits (other 5%, total 8% < 53%) -> recovers to 1.0 after 2s calm window
        var calm = new GpuSensorSample(false, 0.0, 0.0, true, 8.0, 3.0, 5.0);
        for (int i = 0; i < 16; i++)
        {
            controller.Update(PowerTier.Balanced, profile, calm, 0.25);
        }
        Assert.Equal(1.0, controller.YieldScale, 6);
        Assert.False(controller.IsYielding);

        // Max tier never yields on util-only GPUs either
        for (int i = 0; i < 8; i++)
        {
            controller.Update(PowerTier.Efficiency, profile, busyOther, 0.25);
        }
        Assert.True(controller.IsYielding);
        Assert.Equal(1.0, controller.Update(PowerTier.Max, profile, busyOther, 0.25), 6);
        Assert.False(controller.IsYielding);
    }

    [Fact]
    public void OverloadYieldControllerIgnoresPostTierSwitchPowerCooldownAndBriefSpikes()
    {
        var profile = new EfficiencyProfile(
            FullLoadWatts: 347.0,
            FullLoadGpuUtil: 85.0,
            HasPowerSensor: true,
            Eco: new TierOperatingPoint(0.08, 53.0, 20.0, 1.7e9, "natural-plateau"),
            Balanced: new TierOperatingPoint(0.22, 98.0, 30.0, 4.7e9, "natural-peak"),
            Efficiency: new TierOperatingPoint(0.48, 240.0, 48.0, 10.4e9, "upper-mid-peak"),
            Samples: Array.Empty<EfficiencyCurveSample>());

        var controller = new OverloadYieldController();

        double[] cooldownWatts = [320.0, 290.0, 260.0, 220.0, 180.0, 145.0, 115.0, 98.0, 95.0, 94.0];
        foreach (double w in cooldownWatts)
        {
            var cooling = new GpuSensorSample(true, w, 350.0, true, 30.0, 25.0, 5.0);
            Assert.Equal(1.0, controller.Update(PowerTier.Balanced, profile, cooling, 0.25), 6);
        }
        Assert.False(controller.IsYielding);

        var briefSpike = new GpuSensorSample(true, 270.0, 350.0, true, 85.0, 25.0, 60.0);
        Assert.Equal(1.0, controller.Update(PowerTier.Balanced, profile, briefSpike, 0.25), 6);
        Assert.False(controller.IsYielding);

        var steady = new GpuSensorSample(true, 95.0, 350.0, true, 28.0, 23.0, 5.0);
        Assert.Equal(1.0, controller.Update(PowerTier.Balanced, profile, steady, 0.25), 6);

        for (int i = 0; i < 8; i++)
        {
            controller.Update(PowerTier.Balanced, profile, briefSpike, 0.25);
        }
        Assert.True(controller.IsYielding);
    }

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

    [Fact]
    public async Task OverloadYieldPausesBalancedTierAndAutoRecoversWhenOtherProcessStops()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-yield-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("10DE:2206:00000000:A1:0", 42, "Mock RTX 3080", 0x10DE, 0x2206, 0, 0xA1, 10_000_000_000UL, "32.0.16.1088", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 95.0, 350.0, true, 32.0, 27.0, 5.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);

            var efficiencyProfile = new EfficiencyProfile(
                FullLoadWatts: 346.5,
                FullLoadGpuUtil: 85.0,
                HasPowerSensor: true,
                Eco: new TierOperatingPoint(0.08, 52.5, 19.0, 80_000.0, "natural-plateau"),
                Balanced: new TierOperatingPoint(0.22, 96.9, 35.0, 220_000.0, "natural-peak"),
                Efficiency: new TierOperatingPoint(0.48, 240.0, 48.0, 480_000.0, "upper-mid-peak"),
                Samples: Array.Empty<EfficiencyCurveSample>());

            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Balanced;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    1_000_000.0, 1_000_000.0, true, DateTimeOffset.UtcNow, workload, efficiencyProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(80),
                    TelemetryInterval = TimeSpan.FromMilliseconds(40),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            Assert.Equal(PowerTier.Balanced, coordinator.SelectedTier);

            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0 &&
                !coordinator.LatestTelemetry.IsOverloadYielding,
                TimeSpan.FromSeconds(4));

            sensor.Current = new GpuSensorSample(true, 280.0, 350.0, true, 92.0, 15.0, 77.0);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.IsOverloadYielding,
                TimeSpan.FromSeconds(4));

            sensor.Current = new GpuSensorSample(true, 55.0, 350.0, true, 6.0, 0.0, 6.0);
            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                !coordinator.LatestTelemetry.IsOverloadYielding &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0,
                TimeSpan.FromSeconds(6));

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DynamicMedianUsesMedianWattsAndDoesNotFlipSensorlessProfile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-dynwatt-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new SimpleFakeBackend(gpu);
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 320.0, 350.0, true, 95.0, 90.0, 5.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);
            var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            // Seed sensorless profile (HasPowerSensor=false, tier EstimatedWatts=0)
            var sensorlessProfile = EfficiencyProfile.CreateFallback(50_000.0, fullLoadWatts: 0.0, fullLoadGpuUtil: 90.0);
            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Max;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    50_000.0, 50_000.0, true, initialTimestamp, workload, sensorlessProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    DynamicMedianInterval = TimeSpan.FromMilliseconds(160),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            await coordinator.StartSearchAsync(pattern);
            await WaitUntilAsync(() => stateStore.Load().Calibrations[profileKey].CalibratedAtUtc > initialTimestamp, TimeSpan.FromSeconds(4));
            await coordinator.StopAsync();

            EfficiencyProfile? updatedProfile = stateStore.Load().Calibrations[profileKey].EfficiencyProfile;
            Assert.NotNull(updatedProfile);
            Assert.False(updatedProfile!.HasPowerSensor);
            Assert.Equal(0.0, updatedProfile.FullLoadWatts, 6);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DynamicMedianSkipsBaselineUpdateWhileOtherProcessContendsForGpu()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-contend-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 101, "Mock GPU", 0x10DE, 1, 0, 1, 10_000_000_000UL, "1.0", true);
            var backend = new SimpleFakeBackend(gpu);
            // Start uncontested so samples begin accumulating, then launch a game mid-interval
            var sensor = new ControllableFakeSensor(new GpuSensorSample(true, 348.5, 350.0, true, 96.0, 92.0, 4.0));
            var stateStore = new AppStateStore(tempDir);
            var pattern = KeyIdPattern.Parse("FFFFFFFFFFFFFFFF");
            string workload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string profileKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, workload);
            var initialTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);

            var cleanProfile = new EfficiencyProfile(
                FullLoadWatts: 348.5,
                FullLoadGpuUtil: 95.0,
                HasPowerSensor: true,
                Eco: new TierOperatingPoint(0.05, 46.0, 22.0, 1_000_000.0, "natural-plateau"),
                Balanced: new TierOperatingPoint(0.15, 60.0, 29.0, 3_000_000.0, "natural-peak"),
                Efficiency: new TierOperatingPoint(0.48, 248.0, 47.0, 10_000_000.0, "upper-mid-peak"),
                Samples: Array.Empty<EfficiencyCurveSample>());

            stateStore.Update(s =>
            {
                s.TargetInput = pattern.Suffix;
                s.PowerTier = PowerTier.Max;
                s.Calibrations[profileKey] = new CalibrationRecord(
                    gpu.DeviceKey, gpu.Name, gpu.DriverVersion, gpu.KernelVersion,
                    23_500_000_000.0, 23_000_000_000.0, true, initialTimestamp, workload, cleanProfile);
            });

            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                new CoordinatorOptions
                {
                    CalibrationWindow = TimeSpan.FromMilliseconds(50),
                    DynamicMedianInterval = TimeSpan.FromMilliseconds(260),
                    TelemetryInterval = TimeSpan.FromMilliseconds(35),
                    SensorPollInterval = TimeSpan.FromMilliseconds(35),
                    ExpectedKernelVersionOverride = gpu.KernelVersion
                },
                sensor);

            coordinator.Initialize();
            await coordinator.StartSearchAsync(pattern);

            await WaitUntilAsync(() =>
                coordinator.CurrentState == CoordinatorState.Running &&
                coordinator.LatestTelemetry.TotalActualHps > 0.0 &&
                !coordinator.LatestTelemetry.IsGpuContended,
                TimeSpan.FromSeconds(4));

            // Inject mid-interval game contention (32% other GPU, 335 W): accumulated samples must be discarded
            sensor.Current = new GpuSensorSample(true, 335.0, 350.0, true, 96.0, 44.0, 32.0);
            await WaitUntilAsync(() => coordinator.LatestTelemetry.IsGpuContended, TimeSpan.FromSeconds(4));

            // Stay contended well past DynamicMedianInterval (260ms): baseline and FullLoadWatts MUST stay untouched
            await Task.Delay(500);
            CalibrationRecord duringGame = stateStore.Load().Calibrations[profileKey];
            Assert.Equal(initialTimestamp, duringGame.CalibratedAtUtc);
            Assert.Equal(23_000_000_000.0, duringGame.MedianHashesPerSecond, 6);
            Assert.Equal(348.5, duringGame.EfficiencyProfile!.FullLoadWatts, 6);

            // Once competing game exits (OtherProcessGpuUtil = 4%, 349 W), contention clears and DynamicMedian resumes
            // using strictly clean post-game wattage samples (no leaked 335 W).
            sensor.Current = new GpuSensorSample(true, 349.0, 350.0, true, 98.0, 94.0, 4.0);
            await WaitUntilAsync(() =>
                !coordinator.LatestTelemetry.IsGpuContended &&
                stateStore.Load().Calibrations[profileKey].CalibratedAtUtc > initialTimestamp,
                TimeSpan.FromSeconds(4));

            CalibrationRecord afterGame = stateStore.Load().Calibrations[profileKey];
            Assert.Equal(349.0, afterGame.EfficiencyProfile!.FullLoadWatts, 6);

            await coordinator.StopAsync();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LegacyStateMigrationAndPartialEfficiencyProfileAreHandledSafely()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "pm-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var gpu = new GpuDeviceInfo("GPU-0", 1, "Legacy GPU", 0x10DE, 1, 0, 1, 8_000_000_000UL, "1.0", true);
            var pattern = KeyIdPattern.Parse("1145141919810");
            string currentWorkload = CalibrationValidator.WorkloadIdentity(gpu, pattern);
            string currentKey = CalibrationValidator.ProfileKey(gpu.DeviceKey, currentWorkload);
            string legacyV2Workload = currentWorkload.Replace("v3/", "v2/", StringComparison.Ordinal);
            string legacyV2Key = CalibrationValidator.ProfileKey(gpu.DeviceKey, legacyV2Workload);
            Directory.CreateDirectory(tempDir);

            // State with both a complete legacy v2 profile and a partial current v3 profile missing Balanced:
            // must not throw, must preserve TotalChecked & PowerTier, and must require fresh v3 calibration.
            string partialJson = $$"""
            {
              "HasCompletedSetup": true,
              "TargetInput": "1145141919810",
              "LastPatternMask": "{{pattern.CanonicalMask}}",
              "TotalCheckedCandidates": 1832181651148985,
              "SliderPosition": 0.25,
              "EnabledDeviceKeys": ["GPU-0"],
              "Calibrations": {
                "{{legacyV2Key}}": {
                  "DeviceKey": "GPU-0",
                  "DeviceName": "Legacy GPU",
                  "DriverVersion": "1.0",
                  "KernelVersion": "d3d11-sha1-four-v4",
                  "PeakHashesPerSecond": 22000000000,
                  "MedianHashesPerSecond": 21000000000,
                  "IsFullCalibration": true,
                  "CalibratedAtUtc": "2026-11-25T10:00:00Z",
                  "WorkloadIdentity": "{{legacyV2Workload}}",
                  "EfficiencyProfile": {
                    "FullLoadWatts": 340,
                    "FullLoadGpuUtil": 85,
                    "HasPowerSensor": true,
                    "Eco": { "RateFraction": 0.08, "EstimatedWatts": 52, "EstimatedGpuUtil": 19, "MeasuredHps": 1700000000, "SelectionMode": "natural-plateau" },
                    "Balanced": { "RateFraction": 0.22, "EstimatedWatts": 98, "EstimatedGpuUtil": 30, "MeasuredHps": 4700000000, "SelectionMode": "natural-peak" },
                    "Efficiency": { "RateFraction": 0.48, "EstimatedWatts": 245, "EstimatedGpuUtil": 48, "MeasuredHps": 10400000000, "SelectionMode": "upper-mid-peak" }
                  }
                },
                "{{currentKey}}": {
                  "DeviceKey": "GPU-0",
                  "DeviceName": "Legacy GPU",
                  "DriverVersion": "1.0",
                  "KernelVersion": "d3d11-sha1-four-v4",
                  "PeakHashesPerSecond": 22000000000,
                  "MedianHashesPerSecond": 21000000000,
                  "IsFullCalibration": true,
                  "CalibratedAtUtc": "2026-11-25T10:00:00Z",
                  "WorkloadIdentity": "{{currentWorkload}}",
                  "EfficiencyProfile": {
                    "FullLoadWatts": 340,
                    "FullLoadGpuUtil": 85,
                    "HasPowerSensor": true,
                    "Eco": { "RateFraction": 0.08, "EstimatedWatts": 52, "EstimatedGpuUtil": 19, "MeasuredHps": 1700000000, "SelectionMode": "natural-plateau" }
                  }
                }
              }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "state.json"), partialJson);

            var stateStore = new AppStateStore(tempDir);
            var backend = new SimpleFakeBackend(gpu);
            using var coordinator = new MultiGpuCoordinator(
                backend,
                stateStore,
                new DpapiVaultStore(tempDir),
                telemetrySensor: NullGpuTelemetrySensor.Instance);

            coordinator.Initialize();
            coordinator.SetTargetPattern(pattern);

            Assert.Equal(PowerTier.Eco, coordinator.SelectedTier);
            Assert.Equal(1832181651148985UL, coordinator.LatestTelemetry.TotalChecked);
            Assert.True(coordinator.NeedsFullCalibration());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LocalizationStringBundlesHaveMatchingKeysAndAllFourTierStrings()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string enPath = Path.Combine(repoRoot, "src", "PGPMint.App", "Strings", "en-US.json");
        string zhPath = Path.Combine(repoRoot, "src", "PGPMint.App", "Strings", "zh-Hant.json");
        Assert.True(File.Exists(enPath), $"Missing {enPath}");
        Assert.True(File.Exists(zhPath), $"Missing {zhPath}");

        using JsonDocument enDoc = JsonDocument.Parse(File.ReadAllText(enPath));
        using JsonDocument zhDoc = JsonDocument.Parse(File.ReadAllText(zhPath));

        HashSet<string> enKeys = enDoc.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> zhKeys = zhDoc.RootElement.GetProperty("strings").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(enKeys, zhKeys);
        foreach (string required in new[] { "TierEco", "TierBalanced", "TierEfficiency", "TierMax", "Recalibrate", "Recalibrating", "PowerTooltipFormat", "GpuLoadTooltipFormat", "YieldingBadge", "YieldingSuffix" })
        {
            Assert.Contains(required, enKeys);
            Assert.False(string.IsNullOrWhiteSpace(enDoc.RootElement.GetProperty("strings").GetProperty(required).GetString()));
            Assert.False(string.IsNullOrWhiteSpace(zhDoc.RootElement.GetProperty("strings").GetProperty(required).GetString()));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for expected coordinator condition.");
    }

    private sealed class ControllableFakeSensor(GpuSensorSample initial) : IGpuTelemetrySensor
    {
        private readonly object _gate = new();
        private GpuSensorSample _current = initial;

        public GpuSensorSample Current
        {
            get { lock (_gate) return _current; }
            set { lock (_gate) _current = value; }
        }

        public bool HasPowerSensor(GpuDeviceInfo device) => Current.HasPowerWatts;
        public GpuSensorSample Sample(GpuDeviceInfo device) => Current;
        public void Dispose() { }
    }

    private sealed class SimpleFakeBackend(GpuDeviceInfo info) : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => new SimpleFakeDevice(deviceInfo);
    }

    private sealed class SimpleFakeDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 16_384;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024) =>
            ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

        public SearchBatchResult ScanBatch(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024,
            SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
            }

            Thread.Sleep(3);
            return new SearchBatchResult([[], [], [], []], (long)timestampCount * KeysPerBatch, (long)timestampCount * KeysPerBatch);
        }

        public void Dispose() { }
    }
}

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
