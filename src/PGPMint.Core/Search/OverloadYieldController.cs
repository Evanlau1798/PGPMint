namespace PGPMint.Core.Search;

/// <summary>
/// Smoothly adapts PGPMint's GPU rate in Eco / Balanced / Efficiency tiers when another process
/// shares the GPU: trims rate when total GPU power or engine utilization exceeds the tier ceiling,
/// holds steady inside a deadband, and gently fills spare headroom (even while another app remains
/// active) instead of latching at zero. Max tier never yields.
/// </summary>
public sealed class OverloadYieldController
{
    private const double PowerCooldownGraceSeconds = 3.0;
    private const double OtherAppSwitchGraceSeconds = 1.0;
    private const double OverloadTriggerHoldSeconds = 0.50;
    private const double CalmRecoveryHoldSeconds = 2.0;
    private const double SharedRecoveryHoldSeconds = 1.5;
    private const double SharedWakeHoldSeconds = 3.0;
    private const double FullCalmRecoverySlopePerSecond = 0.80;
    private const double SharedRecoverySlopePerSecond = 0.25;
    private const double DecayBasePerSecond = 0.38;
    private const double SnapToZeroThreshold = 0.08;
    private const double MinWakeYieldScale = 0.10;

    private double _powerGraceRemainingSeconds = PowerCooldownGraceSeconds;
    private double _otherAppGraceRemainingSeconds = OtherAppSwitchGraceSeconds;
    private double _overloadHoldSeconds;
    private double _headroomSeconds;

    public double YieldScale { get; private set; } = 1.0;
    public bool IsYielding => YieldScale < 0.99;

    public void Reset()
    {
        YieldScale = 1.0;
        _powerGraceRemainingSeconds = PowerCooldownGraceSeconds;
        _otherAppGraceRemainingSeconds = OtherAppSwitchGraceSeconds;
        _overloadHoldSeconds = 0.0;
        _headroomSeconds = 0.0;
    }

    public double Update(
        PowerTier tier,
        EfficiencyProfile? profile,
        GpuSensorSample sample,
        double elapsedSeconds)
    {
        if (tier == PowerTier.Max)
        {
            Reset();
            return 1.0;
        }

        double dt = double.IsFinite(elapsedSeconds) && elapsedSeconds > 0.0
            ? Math.Clamp(elapsedSeconds, 0.01, 2.0)
            : 0.25;

        bool inPowerCooldownGrace = _powerGraceRemainingSeconds > 0.0;
        if (inPowerCooldownGrace)
        {
            _powerGraceRemainingSeconds = Math.Max(0.0, _powerGraceRemainingSeconds - dt);
        }

        bool inOtherAppGrace = _otherAppGraceRemainingSeconds > 0.0;
        if (inOtherAppGrace)
        {
            _otherAppGraceRemainingSeconds = Math.Max(0.0, _otherAppGraceRemainingSeconds - dt);
        }

        bool isOverloaded;
        bool hasHeadroom;
        bool isFullyCalm;

        if (sample.HasPowerWatts && profile is { HasPowerSensor: true, FullLoadWatts: > 0.0 })
        {
            TierOperatingPoint? tierPoint = tier switch
            {
                PowerTier.Eco => profile.Eco,
                PowerTier.Balanced => profile.Balanced,
                PowerTier.Efficiency => profile.Efficiency,
                _ => null
            };
            double defaultRatio = tier switch
            {
                PowerTier.Eco => 0.25,
                PowerTier.Balanced => 0.45,
                _ => 0.70
            };
            double tierWatts = tierPoint is { EstimatedWatts: > 0.0 }
                ? tierPoint.EstimatedWatts
                : profile.FullLoadWatts * defaultRatio;

            bool isUpperMid = tier == PowerTier.Efficiency;
            double maxCeilingRatio = isUpperMid ? 0.88 : 0.80;
            double headroomScale = isUpperMid ? 1.15 : 1.25;
            double headroomMinWatts = isUpperMid ? 20.0 : 25.0;
            double powerCeiling = Math.Min(
                profile.FullLoadWatts * maxCeilingRatio,
                Math.Max(tierWatts * headroomScale, tierWatts + headroomMinWatts));
            double hardPowerCeiling = isUpperMid
                ? Math.Max(powerCeiling + 20.0, profile.FullLoadWatts * 0.90)
                : Math.Max(powerCeiling + 20.0, profile.FullLoadWatts * 0.70);
            double marginAboveTier = Math.Max(0.0, powerCeiling - tierWatts);
            double powerDeadbandWatts = Math.Min(12.0, Math.Min(powerCeiling * 0.15, Math.Max(2.0, marginAboveTier * 0.5)));
            double powerRecoveryThreshold = Math.Max(0.0, Math.Min(powerCeiling * 0.92, powerCeiling - powerDeadbandWatts));
            if (tierWatts < powerCeiling)
            {
                // Ensure the shared-GPU recovery threshold never sits below the tier's own calibrated power
                powerRecoveryThreshold = Math.Max(powerRecoveryThreshold, tierWatts + Math.Min(2.0, marginAboveTier * 0.25));
            }

            double baseUtilCeiling = tier switch
            {
                PowerTier.Eco => 60.0,
                PowerTier.Balanced => 75.0,
                PowerTier.Efficiency => 85.0,
                _ => 100.0
            };
            double calibratedUtil = tierPoint is { EstimatedGpuUtil: > 0.0 } ? tierPoint.EstimatedGpuUtil : 0.0;
            double utilCeiling = calibratedUtil > 0.0
                ? Math.Min(92.0, Math.Max(baseUtilCeiling, calibratedUtil + 10.0))
                : baseUtilCeiling;
            double utilRecoveryThreshold = utilCeiling - 10.0;

            if (sample.HasGpuUtil)
            {
                bool otherAppOverload = !inOtherAppGrace &&
                                        sample.OtherProcessGpuUtil >= 20.0 &&
                                        (sample.PowerWatts > powerCeiling || sample.TotalGpuUtil >= utilCeiling);
                bool hardPowerOverload = !inPowerCooldownGrace && sample.PowerWatts > hardPowerCeiling;
                isOverloaded = otherAppOverload || hardPowerOverload;
                // When no other app is competing (< 15%), allow full recovery up to powerCeiling
                // (still well below hardPowerCeiling) so warm GPUs and <=90W GPUs never stall below 1.0.
                isFullyCalm = sample.OtherProcessGpuUtil < 15.0 && sample.PowerWatts < powerCeiling;
                hasHeadroom = isFullyCalm ||
                              (sample.PowerWatts < powerRecoveryThreshold && sample.TotalGpuUtil < utilRecoveryThreshold);
            }
            else
            {
                // Power-only fallback (no D3DKMT util): keep conservative shared-slope recovery so
                // rate settles into the deadband without oscillating if another app is active.
                isOverloaded = !inPowerCooldownGrace && sample.PowerWatts > powerCeiling;
                isFullyCalm = false;
                hasHeadroom = sample.PowerWatts < powerRecoveryThreshold;
            }
        }
        else if (sample.HasGpuUtil)
        {
            TierOperatingPoint? tierPoint = tier switch
            {
                PowerTier.Eco => profile?.Eco,
                PowerTier.Balanced => profile?.Balanced,
                PowerTier.Efficiency => profile?.Efficiency,
                _ => null
            };
            double baseUtilCeiling = tier switch
            {
                PowerTier.Eco => 45.0,
                PowerTier.Balanced => 65.0,
                PowerTier.Efficiency => 82.0,
                _ => 100.0
            };
            double calibratedUtil = tierPoint is { EstimatedGpuUtil: > 0.0 } ? tierPoint.EstimatedGpuUtil : 0.0;
            double utilCeiling = calibratedUtil > 0.0
                ? Math.Min(92.0, Math.Max(baseUtilCeiling, calibratedUtil + 10.0))
                : baseUtilCeiling;
            double utilRecoveryThreshold = utilCeiling - 12.0;

            isOverloaded = !inOtherAppGrace &&
                           sample.OtherProcessGpuUtil >= 20.0 &&
                           sample.TotalGpuUtil >= utilCeiling;
            // When no other app is active (< 15%), isOverloaded is always false, so allow full
            // recovery up to utilCeiling even as PGPMint's own utilization climbs to its tier target.
            isFullyCalm = sample.OtherProcessGpuUtil < 15.0 && sample.TotalGpuUtil < utilCeiling;
            hasHeadroom = isFullyCalm || sample.TotalGpuUtil < utilRecoveryThreshold;
        }
        else
        {
            Reset();
            return 1.0;
        }

        if (isOverloaded)
        {
            _headroomSeconds = 0.0;
            _overloadHoldSeconds += dt;
            if (_overloadHoldSeconds >= OverloadTriggerHoldSeconds && YieldScale > 0.0)
            {
                double next = YieldScale * Math.Pow(DecayBasePerSecond, dt);
                YieldScale = next < SnapToZeroThreshold ? 0.0 : next;
            }
        }
        else
        {
            _overloadHoldSeconds = 0.0;
            if (hasHeadroom && YieldScale < 1.0)
            {
                _headroomSeconds += dt;
                double requiredHold = isFullyCalm
                    ? CalmRecoveryHoldSeconds
                    : (YieldScale <= 0.0 ? SharedWakeHoldSeconds : SharedRecoveryHoldSeconds);
                double slope = isFullyCalm
                    ? FullCalmRecoverySlopePerSecond
                    : SharedRecoverySlopePerSecond;
                if (_headroomSeconds >= requiredHold)
                {
                    double next = YieldScale <= 0.0
                        ? Math.Max(MinWakeYieldScale, dt * slope)
                        : YieldScale + (dt * slope);
                    YieldScale = Math.Min(1.0, next);
                    if (YieldScale >= 0.995)
                    {
                        YieldScale = 1.0;
                        _headroomSeconds = 0.0;
                    }
                }
            }
            else
            {
                _headroomSeconds = 0.0;
            }
        }

        return YieldScale;
    }
}
