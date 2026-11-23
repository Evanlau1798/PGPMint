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

    public static RateDirective FromSlider(double sliderPosition, double calibratedPeakHps)
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

        if (calibratedPeakHps <= 0.0)
        {
            return new RateDirective(RateMode.FullSpeed, FullSpeedSentinel);
        }

        long target = (long)Math.Round(p * calibratedPeakHps, MidpointRounding.AwayFromZero);
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
    public static bool IsValidForDevice(
        CalibrationRecord? record,
        GpuDeviceInfo device,
        string expectedKernelVersion,
        bool requireFull)
    {
        if (record is null || record.PeakHashesPerSecond <= 0.0)
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
