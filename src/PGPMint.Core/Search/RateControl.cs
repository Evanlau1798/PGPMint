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

    // Two-stage UI slider layout:
    //   10..990     -> Capped 1%..99% (10 units per 1%)
    //   991..1099   -> Resistance detent zone (thumb & value stay pinned at 990 / 99%)
    //   1100..1120  -> Full Speed (snaps thumb to 1120 / Full Speed, enables dynamic median tracking)
    public const double UiSliderMin = 10.0;
    public const double UiSliderCappedMax = 990.0;
    public const double UiSliderFullSpeedThreshold = 1100.0;
    public const double UiSliderFullSpeed = 1120.0;

    public static double UiSliderToLogical(double uiValue)
    {
        if (uiValue >= UiSliderFullSpeedThreshold)
        {
            return 1.0;
        }

        if (uiValue >= UiSliderCappedMax)
        {
            return 0.99;
        }

        int pct = (int)Math.Clamp(Math.Round(uiValue / 10.0, MidpointRounding.AwayFromZero), 1.0, 99.0);
        return pct / 100.0;
    }

    public static double LogicalToUiSlider(double logicalPosition)
    {
        double p = Math.Clamp(logicalPosition, 0.01, 1.0);
        if (p >= 1.0)
        {
            return UiSliderFullSpeed;
        }

        int pct = (int)Math.Clamp(Math.Round(p * 100.0, MidpointRounding.AwayFromZero), 1.0, 99.0);
        return pct * 10.0;
    }

    public static double SnapUiSliderOnRelease(double uiValue) =>
        LogicalToUiSlider(UiSliderToLogical(uiValue));

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
            // Keep capped batches <= 2^29 candidates and ~50ms slices so slider adjustments stay crisp
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
        if (record is null || GetBaselineHps(record) <= 0.0)
        {
            return false;
        }

        if (requireFull && !record.IsFullCalibration)
        {
            return false;
        }

        return string.Equals(record.DeviceKey, device.DeviceKey, StringComparison.Ordinal) &&
               string.Equals(record.DeviceName, device.Name, StringComparison.Ordinal) &&
               string.Equals(record.DriverVersion, device.DriverVersion, StringComparison.Ordinal) &&
               string.Equals(record.KernelVersion, expectedKernelVersion, StringComparison.Ordinal);
    }
}
