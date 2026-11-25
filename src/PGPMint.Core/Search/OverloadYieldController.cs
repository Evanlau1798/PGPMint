namespace PGPMint.Core.Search;

/// <summary>
/// Smoothly yields PGPMint's GPU rate in Eco / Balanced / Efficiency tiers only when another process
/// drives total GPU power or engine utilization above the tier budget, then recovers after
/// a calm hysteresis window. Max tier never yields.
/// </summary>
public sealed class OverloadYieldController
{
    private const double SettleGraceSeconds = 3.0;
    private const double OverloadTriggerHoldSeconds = 0.75;
    private const double CalmRecoveryHoldSeconds = 2.0;
    private const double RecoverySlopePerSecond = 0.80;
    private const double DecayBasePerSecond = 0.15;
    private const double SnapToZeroThreshold = 0.12;

    private double _graceRemainingSeconds = SettleGraceSeconds;
    private double _overloadHoldSeconds;
    private double _calmSeconds;

    public double YieldScale { get; private set; } = 1.0;
    public bool IsYielding => YieldScale < 0.99;

    public void Reset()
    {
        YieldScale = 1.0;
        _graceRemainingSeconds = SettleGraceSeconds;
        _overloadHoldSeconds = 0.0;
        _calmSeconds = 0.0;
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

        bool inPowerCooldownGrace = _graceRemainingSeconds > 0.0;
        if (inPowerCooldownGrace)
        {
            _graceRemainingSeconds = Math.Max(0.0, _graceRemainingSeconds - dt);
        }

        bool isOverloaded;
        bool isCalm;

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

            if (sample.HasGpuUtil)
            {
                bool otherAppOverload = sample.OtherProcessGpuUtil >= 20.0 &&
                                        (sample.PowerWatts > powerCeiling || (isUpperMid && sample.TotalGpuUtil >= 85.0));
                bool hardPowerOverload = !inPowerCooldownGrace && sample.PowerWatts > hardPowerCeiling;
                isOverloaded = otherAppOverload || hardPowerOverload;
                isCalm = sample.OtherProcessGpuUtil < 15.0 && sample.PowerWatts < powerCeiling * 0.94;
            }
            else
            {
                isOverloaded = !inPowerCooldownGrace && sample.PowerWatts > powerCeiling;
                isCalm = sample.PowerWatts < powerCeiling * 0.92;
            }
        }
        else if (sample.HasGpuUtil)
        {
            double utilCeiling = tier switch
            {
                PowerTier.Eco => 40.0,
                PowerTier.Balanced => 62.0,
                PowerTier.Efficiency => 78.0,
                _ => 100.0
            };
            isOverloaded = sample.OtherProcessGpuUtil >= 25.0 && sample.TotalGpuUtil > utilCeiling;
            isCalm = sample.OtherProcessGpuUtil < 15.0 && sample.TotalGpuUtil < utilCeiling - 10.0;
        }
        else
        {
            Reset();
            return 1.0;
        }

        if (isOverloaded)
        {
            _calmSeconds = 0.0;
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
            if (isCalm && YieldScale < 1.0)
            {
                _calmSeconds += dt;
                if (_calmSeconds >= CalmRecoveryHoldSeconds)
                {
                    YieldScale = Math.Min(1.0, YieldScale + (dt * RecoverySlopePerSecond));
                    if (YieldScale >= 0.995)
                    {
                        YieldScale = 1.0;
                        _calmSeconds = 0.0;
                    }
                }
            }
            else if (YieldScale >= 1.0)
            {
                _calmSeconds = 0.0;
            }
        }

        return YieldScale;
    }
}
