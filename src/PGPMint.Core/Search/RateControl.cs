namespace PGPMint.Core.Search;

public enum RateMode
{
    Paused,
    Capped,
    FullSpeed
}

public readonly record struct RateDirective(RateMode Mode, long TargetHashesPerSecond);

public static class RateControl
{
    public const long FullSpeedSentinel = 1_000_000_000_000L; // 10^12

    public static PowerTier TierFromLegacySlider(double sliderPosition)
    {
        if (double.IsNaN(sliderPosition) || sliderPosition >= 0.99)
        {
            return PowerTier.Max;
        }

        if (sliderPosition >= 0.55)
        {
            return PowerTier.Efficiency;
        }

        return sliderPosition <= 0.30 ? PowerTier.Eco : PowerTier.Balanced;
    }

    public static double TierToLegacySlider(PowerTier tier) => tier switch
    {
        PowerTier.Eco => 0.15,
        PowerTier.Balanced => 0.35,
        PowerTier.Efficiency => 0.55,
        _ => 1.0
    };

    public static double GetTierRateFraction(PowerTier tier, EfficiencyProfile? profile) => tier switch
    {
        PowerTier.Max => 1.0,
        PowerTier.Eco => Math.Clamp(
            profile?.Eco?.RateFraction ?? EfficiencyCurveSelector.DefaultEcoFraction,
            EfficiencyCurveSelector.MinTierFraction,
            EfficiencyCurveSelector.MaxSubFullFraction),
        PowerTier.Balanced => Math.Clamp(
            profile?.Balanced?.RateFraction ?? EfficiencyCurveSelector.DefaultBalancedFraction,
            EfficiencyCurveSelector.MinTierFraction,
            EfficiencyCurveSelector.MaxSubFullFraction),
        PowerTier.Efficiency => Math.Clamp(
            profile?.Efficiency?.RateFraction ?? EfficiencyCurveSelector.DefaultEfficiencyFraction,
            EfficiencyCurveSelector.MinTierFraction,
            EfficiencyCurveSelector.MaxSubFullFraction),
        _ => 1.0
    };

    public static RateDirective FromTier(
        PowerTier tier,
        double calibratedBaselineHps,
        EfficiencyProfile? profile,
        double yieldScale = 1.0)
    {
        if (tier == PowerTier.Max)
        {
            return new RateDirective(RateMode.FullSpeed, FullSpeedSentinel);
        }

        if (calibratedBaselineHps <= 0.0 || !double.IsFinite(calibratedBaselineHps))
        {
            return new RateDirective(RateMode.FullSpeed, FullSpeedSentinel);
        }

        double clampedYield = double.IsFinite(yieldScale) ? Math.Clamp(yieldScale, 0.0, 1.0) : 1.0;
        if (clampedYield <= 0.0)
        {
            return new RateDirective(RateMode.Paused, 0L);
        }

        double fraction = GetTierRateFraction(tier, profile);
        long target = (long)Math.Round(calibratedBaselineHps * fraction * clampedYield, MidpointRounding.AwayFromZero);
        return new RateDirective(RateMode.Capped, Math.Max(1L, target));
    }

    public static RateDirective FromSlider(double sliderPosition, double calibratedBaselineHps)
    {
        double p = Math.Clamp(sliderPosition, 0.0, 1.0);
        if (p <= 0.0)
        {
            return new RateDirective(RateMode.Paused, 0L);
        }

        if (p >= 1.0)
        {
            return new RateDirective(RateMode.FullSpeed, FullSpeedSentinel);
        }

        if (calibratedBaselineHps <= 0.0)
        {
            return new RateDirective(RateMode.FullSpeed, FullSpeedSentinel);
        }

        long target = (long)Math.Round(p * calibratedBaselineHps, MidpointRounding.AwayFromZero);
        return new RateDirective(RateMode.Capped, Math.Max(1L, target));
    }

    public static uint ComputeBatchTimestamps(
        long maxCandidatesPerBatch,
        int keysPerBatch,
        RateDirective directive,
        uint remainingTimestamps)
    {
        if (remainingTimestamps == 0 || directive.Mode == RateMode.Paused)
        {
            return 0;
        }

        long maxStampsByDevice = Math.Max(1L, maxCandidatesPerBatch / keysPerBatch);
        long stamps = maxStampsByDevice;

        if (directive.Mode == RateMode.Capped)
        {
            // Keep capped batches <= 2^29 candidates and ~50ms period slices:
            // empirically verified on RTX 3080 to keep short 4-11ms bursts in low-voltage P5/P3 states.
            long cappedMaxStamps = Math.Min(maxStampsByDevice, (1L << 29) / keysPerBatch);
            long targetSliceStamps = Math.Max(1L, directive.TargetHashesPerSecond / (20L * keysPerBatch));
            stamps = Math.Min(cappedMaxStamps, targetSliceStamps);
        }

        stamps = Math.Min(stamps, remainingTimestamps);
        return (uint)Math.Max(1L, stamps);
    }
}

public static class CalibrationValidator
{
    internal static string WorkloadIdentity(GpuDeviceInfo device, KeyIdPattern pattern)
    {
        GpuKernelConfiguration? configuration = device.KernelConfiguration;
        string shape = configuration is null ? "legacy-shape" : FormattableString.Invariant(
            $"{configuration.KeysPerWorkItem}x{configuration.WorkgroupSize}/unroll-{configuration.OuterLoopUnroll}/{configuration.ScheduleVariant}");
        string suffixGroup = pattern.FixedHexLength <= 4
            ? FormattableString.Invariant($"exact-short-{pattern.FixedHexLength}")
            : pattern.FixedHexLength <= 8
                ? "exact-low32-5-8"
                : "exact-high64-9-16";
        return FormattableString.Invariant(
            $"v3/{device.BackendName}/{device.BackendDriverVersion ?? device.DriverVersion}/{shape}/{suffixGroup}/find-one-1/efficiency-curve");
    }

    internal static string ProfileKey(string deviceKey, string workloadIdentity) => $"{deviceKey}|{workloadIdentity}";

    public static double GetBaselineHps(CalibrationRecord? record)
    {
        if (record is null)
        {
            return 0.0;
        }

        return record.MedianHashesPerSecond > 0.0
            ? record.MedianHashesPerSecond
            : Math.Max(0.0, record.PeakHashesPerSecond);
    }

    public static bool IsValidForDevice(
        CalibrationRecord? record,
        GpuDeviceInfo device,
        string expectedKernelVersion,
        bool requireFull)
    {
        double baseline = GetBaselineHps(record);
        if (record is null || !double.IsFinite(baseline) || baseline <= 0.0)
        {
            return false;
        }

        if (requireFull &&
            (!record.IsFullCalibration ||
             record.EfficiencyProfile is null ||
             record.EfficiencyProfile.Eco is null ||
             record.EfficiencyProfile.Balanced is null ||
             record.EfficiencyProfile.Efficiency is null))
        {
            return false;
        }

        return string.Equals(record.DeviceKey, device.DeviceKey, StringComparison.Ordinal) &&
               string.Equals(record.DeviceName, device.Name, StringComparison.Ordinal) &&
               string.Equals(record.DriverVersion, device.DriverVersion, StringComparison.Ordinal) &&
               string.Equals(record.KernelVersion, expectedKernelVersion, StringComparison.Ordinal);
    }
}
