namespace PGPMint.Core.Search;

/// <summary>
/// Per-GPU closed-loop soft thermal throttle controller:
/// smoothly trims target throughput when an individual GPU exceeds the user's temperature
/// limit (holding for HysteresisHold before trimming), pauses only under extreme excursion,
/// and gently recovers once temperature drops below (limit - hysteresis).
/// </summary>
public sealed class ThermalGuardController
{
    public const int MinTemperatureLimitCelsius = 65;
    public const int MaxTemperatureLimitCelsius = 90;

    private const double TriggerHoldSeconds = 1.0;
    private const double RecoveryHoldSeconds = 2.0;
    private const double HysteresisCelsius = 3.0;
    private const double EmergencyOffsetCelsius = 8.0;
    private const double TrimDecayPerSecond = 0.65;
    private const double RecoverySlopePerSecond = 0.12;
    private const double MinActiveScale = 0.08;

    private double _overLimitSeconds;
    private double _underLimitSeconds;

    public double ThermalScale { get; private set; } = 1.0;
    public bool IsThrottling => ThermalScale < 0.99;

    public static int? NormalizeLimit(int? limitCelsius) =>
        limitCelsius is >= MinTemperatureLimitCelsius and <= MaxTemperatureLimitCelsius
            ? limitCelsius
            : null;

    public void Reset()
    {
        ThermalScale = 1.0;
        _overLimitSeconds = 0.0;
        _underLimitSeconds = 0.0;
    }

    public double Update(int? maxTemperatureCelsius, GpuSensorSample sample, double elapsedSeconds)
    {
        int? limit = NormalizeLimit(maxTemperatureCelsius);
        if (limit is null || !sample.HasTemperature || !double.IsFinite(sample.TemperatureCelsius) || sample.TemperatureCelsius <= 0.0)
        {
            Reset();
            return 1.0;
        }

        double dt = double.IsFinite(elapsedSeconds) && elapsedSeconds > 0.0
            ? Math.Clamp(elapsedSeconds, 0.01, 2.0)
            : 0.25;

        double temp = sample.TemperatureCelsius;
        if (temp >= limit.Value + EmergencyOffsetCelsius)
        {
            _overLimitSeconds += dt;
            _underLimitSeconds = 0.0;
            ThermalScale = 0.0;
            return 0.0;
        }

        if (temp > limit.Value)
        {
            _underLimitSeconds = 0.0;
            _overLimitSeconds += dt;
            if (_overLimitSeconds >= TriggerHoldSeconds)
            {
                double current = ThermalScale <= 0.0 ? MinActiveScale : ThermalScale;
                ThermalScale = Math.Clamp(current * Math.Pow(TrimDecayPerSecond, dt), MinActiveScale, 1.0);
            }
        }
        else if (temp <= limit.Value - HysteresisCelsius)
        {
            _overLimitSeconds = 0.0;
            if (ThermalScale < 1.0)
            {
                _underLimitSeconds += dt;
                if (_underLimitSeconds >= RecoveryHoldSeconds)
                {
                    double next = ThermalScale <= 0.0
                        ? MinActiveScale
                        : ThermalScale + (dt * RecoverySlopePerSecond);
                    ThermalScale = Math.Min(1.0, next);
                    if (ThermalScale >= 0.995)
                    {
                        ThermalScale = 1.0;
                        _underLimitSeconds = 0.0;
                    }
                }
            }
        }
        else
        {
            // Hold steady inside hysteresis deadband [limit - 3C, limit]
            _overLimitSeconds = 0.0;
            _underLimitSeconds = 0.0;
        }

        return ThermalScale;
    }
}
