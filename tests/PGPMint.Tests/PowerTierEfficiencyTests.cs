using System.Text.Json;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

public sealed partial class PowerTierEfficiencyTests
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
}
