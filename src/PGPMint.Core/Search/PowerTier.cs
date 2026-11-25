namespace PGPMint.Core.Search;

public enum PowerTier
{
    Eco = 0,
    Balanced = 1,
    Efficiency = 2,
    Max = 3
}

public sealed record EfficiencyCurveSample(
    double RateFraction,
    double MeasuredHps,
    double MeasuredWatts,
    double MeasuredGpuUtil);

public sealed record TierOperatingPoint(
    double RateFraction,
    double EstimatedWatts,
    double EstimatedGpuUtil,
    double MeasuredHps,
    string SelectionMode);

public sealed record EfficiencyProfile(
    double FullLoadWatts,
    double FullLoadGpuUtil,
    bool HasPowerSensor,
    TierOperatingPoint Eco,
    TierOperatingPoint Balanced,
    TierOperatingPoint Efficiency,
    IReadOnlyList<EfficiencyCurveSample> Samples)
{
    public static EfficiencyProfile CreateFallback(
        double fullSpeedHps,
        double fullLoadWatts = 0.0,
        double fullLoadGpuUtil = 0.0)
    {
        double safeHps = Math.Max(0.0, fullSpeedHps);
        double safeWatts = double.IsFinite(fullLoadWatts) && fullLoadWatts > 0.0 ? fullLoadWatts : 0.0;
        double safeUtil = double.IsFinite(fullLoadGpuUtil) ? Math.Clamp(fullLoadGpuUtil, 0.0, 100.0) : 0.0;

        return new EfficiencyProfile(
            FullLoadWatts: safeWatts,
            FullLoadGpuUtil: safeUtil,
            HasPowerSensor: safeWatts > 0.0,
            Eco: new TierOperatingPoint(
                EfficiencyCurveSelector.DefaultEcoFraction,
                safeWatts > 0.0 ? safeWatts * 0.25 : 0.0,
                safeUtil > 0.0 ? Math.Min(25.0, safeUtil * 0.25) : 0.0,
                safeHps * EfficiencyCurveSelector.DefaultEcoFraction,
                "default-fallback"),
            Balanced: new TierOperatingPoint(
                EfficiencyCurveSelector.DefaultBalancedFraction,
                safeWatts > 0.0 ? safeWatts * 0.45 : 0.0,
                safeUtil > 0.0 ? Math.Min(45.0, safeUtil * 0.45) : 0.0,
                safeHps * EfficiencyCurveSelector.DefaultBalancedFraction,
                "default-fallback"),
            Efficiency: new TierOperatingPoint(
                EfficiencyCurveSelector.DefaultEfficiencyFraction,
                safeWatts > 0.0 ? safeWatts * 0.70 : 0.0,
                safeUtil > 0.0 ? Math.Min(70.0, safeUtil * 0.70) : 0.0,
                safeHps * EfficiencyCurveSelector.DefaultEfficiencyFraction,
                "default-fallback"),
            Samples: Array.Empty<EfficiencyCurveSample>());
    }
}

public static class EfficiencyCurveSelector
{
    public const double DefaultEcoFraction = 0.10;
    public const double DefaultBalancedFraction = 0.25;
    public const double DefaultEfficiencyFraction = 0.50;
    public const double MinTierFraction = 0.02;
    public const double MaxBalancedFraction = 0.45;
    public const double MaxSubFullFraction = 0.85;

    // 7 step-down fractions spanning ~60-72% power -> P3 low-power sweet spot -> P5 quiet floor.
    public static readonly double[] DefaultStepDownFractions = [0.60, 0.48, 0.35, 0.22, 0.15, 0.08, 0.05];

    public static EfficiencyProfile Select(
        double fullSpeedHps,
        double fullLoadWatts,
        double fullLoadGpuUtil,
        IReadOnlyList<EfficiencyCurveSample>? samples)
    {
        double safeFullHps = double.IsFinite(fullSpeedHps) && fullSpeedHps > 0.0 ? fullSpeedHps : 0.0;
        double safeFullWatts = double.IsFinite(fullLoadWatts) && fullLoadWatts > 0.0 ? fullLoadWatts : 0.0;
        double safeFullUtil = double.IsFinite(fullLoadGpuUtil) ? Math.Clamp(fullLoadGpuUtil, 0.0, 100.0) : 0.0;

        List<EfficiencyCurveSample> valid = (samples ?? Array.Empty<EfficiencyCurveSample>())
            .Where(s => double.IsFinite(s.RateFraction) && s.RateFraction >= MinTierFraction && s.RateFraction < 0.90 &&
                        double.IsFinite(s.MeasuredHps) && s.MeasuredHps > 0.0)
            .OrderByDescending(s => s.RateFraction)
            .ToList();

        if (valid.Count == 0)
        {
            return EfficiencyProfile.CreateFallback(safeFullHps, safeFullWatts, safeFullUtil);
        }

        List<EfficiencyCurveSample> powerValid = valid
            .Where(s => double.IsFinite(s.MeasuredWatts) && s.MeasuredWatts > 0.0)
            .ToList();

        if (safeFullWatts > 0.0 && powerValid.Count >= 2)
        {
            return SelectByPowerCurve(safeFullHps, safeFullWatts, safeFullUtil, powerValid);
        }

        List<EfficiencyCurveSample> utilValid = valid
            .Where(s => double.IsFinite(s.MeasuredGpuUtil) && s.MeasuredGpuUtil > 0.0)
            .ToList();

        if (utilValid.Count >= 2)
        {
            return SelectByGpuUtilFallback(safeFullHps, safeFullUtil, utilValid);
        }

        EfficiencyProfile fallback = EfficiencyProfile.CreateFallback(safeFullHps, safeFullWatts, safeFullUtil);
        return fallback with { Samples = valid };
    }

    private static EfficiencyProfile SelectByPowerCurve(
        double fullSpeedHps,
        double fullLoadWatts,
        double fullLoadGpuUtil,
        List<EfficiencyCurveSample> samples)
    {
        // 1. Balanced tier (low-power P3 sweet spot): keep <= MaxBalancedFraction so upper-mid samples stay reserved for Efficiency.
        List<EfficiencyCurveSample> balPool = samples
            .Where(s => s.RateFraction <= MaxBalancedFraction)
            .ToList();
        if (balPool.Count == 0)
        {
            balPool = samples;
        }

        EfficiencyCurveSample? naturalBal = balPool
            .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.75)
            .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
            .ThenByDescending(s => s.MeasuredHps)
            .FirstOrDefault();

        EfficiencyCurveSample balSample;
        string balMode;
        if (naturalBal is not null && naturalBal.MeasuredWatts <= fullLoadWatts * 0.60)
        {
            balSample = naturalBal;
            balMode = "natural-peak";
        }
        else
        {
            EfficiencyCurveSample? bandBal = balPool
                .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.55)
                .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                .ThenByDescending(s => s.MeasuredHps)
                .FirstOrDefault();

            if (bandBal is not null)
            {
                balSample = bandBal;
                balMode = "power-band-fallback";
            }
            else
            {
                balSample = balPool
                    .Where(s => s.RateFraction >= 0.15)
                    .OrderBy(s => s.MeasuredWatts)
                    .ThenByDescending(s => s.MeasuredHps)
                    .FirstOrDefault()
                    ?? balPool.OrderBy(s => s.MeasuredWatts).ThenByDescending(s => s.MeasuredHps).First();
                balMode = "power-floor-fallback";
            }
        }

        // 2. Eco tier: highest compute on the lowest-power plateau (Rule 3), falling back to <= 30% power band (Rule 1).
        double minWatts = samples.Min(s => s.MeasuredWatts);
        double balWatts = balSample.MeasuredWatts;
        List<EfficiencyCurveSample> belowBal = samples
            .Where(s => s.RateFraction < balSample.RateFraction - 1e-6)
            .ToList();

        TierOperatingPoint ecoPoint;
        if (belowBal.Count == 0)
        {
            double synthFrac = Math.Clamp(balSample.RateFraction * 0.5, MinTierFraction, Math.Max(MinTierFraction, balSample.RateFraction - 0.01));
            ecoPoint = new TierOperatingPoint(
                synthFrac,
                Math.Min(minWatts, balWatts * 0.65),
                Math.Max(0.0, balSample.MeasuredGpuUtil * 0.5),
                fullSpeedHps * synthFrac,
                "synthetic-eco");
        }
        else
        {
            double plateauCeiling = minWatts + Math.Max(8.0, fullLoadWatts * 0.04);
            double minGapBelowBal = Math.Max(12.0, fullLoadWatts * 0.08);

            EfficiencyCurveSample? naturalEco = belowBal
                .Where(s => s.MeasuredWatts <= plateauCeiling &&
                            (s.MeasuredWatts <= balWatts * 0.80 || balWatts - s.MeasuredWatts >= minGapBelowBal))
                .OrderByDescending(s => s.MeasuredHps)
                .FirstOrDefault();

            if (naturalEco is not null && naturalEco.MeasuredWatts <= fullLoadWatts * 0.32)
            {
                ecoPoint = ToPoint(naturalEco, "natural-plateau");
            }
            else
            {
                EfficiencyCurveSample? bandEco = belowBal
                    .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.30)
                    .OrderByDescending(s => s.MeasuredHps)
                    .FirstOrDefault();

                if (bandEco is not null)
                {
                    ecoPoint = ToPoint(bandEco, "power-band-fallback");
                }
                else
                {
                    EfficiencyCurveSample floorEco = belowBal
                        .OrderBy(s => s.MeasuredWatts)
                        .ThenByDescending(s => s.MeasuredHps)
                        .First();
                    ecoPoint = ToPoint(floorEco, "power-floor-fallback");
                }
            }
        }

        // 3. Efficiency tier (card-agnostic upper-mid band: <= 72% full power, fallback <= 75%):
        // bridges Balanced and Max while keeping power clearly under the full thermal wall (~200-250W on a 350W GPU).
        List<EfficiencyCurveSample> aboveBal = samples
            .Where(s => s.RateFraction > balSample.RateFraction + 0.05 && s.RateFraction <= MaxSubFullFraction)
            .ToList();

        TierOperatingPoint effPoint;
        EfficiencyCurveSample? upperMidPeak = aboveBal
            .Where(s => s.RateFraction >= 0.35 && s.MeasuredWatts <= fullLoadWatts * 0.72)
            .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
            .ThenByDescending(s => s.MeasuredHps)
            .FirstOrDefault();

        if (upperMidPeak is not null)
        {
            effPoint = ToPoint(upperMidPeak, "upper-mid-peak");
        }
        else
        {
            EfficiencyCurveSample? bandEff = aboveBal
                .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.75)
                .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                .ThenByDescending(s => s.MeasuredHps)
                .FirstOrDefault();

            if (bandEff is not null)
            {
                effPoint = ToPoint(bandEff, "power-band-fallback");
            }
            else if (aboveBal.Count > 0)
            {
                EfficiencyCurveSample bestAbove = aboveBal
                    .OrderBy(s => Math.Abs(s.MeasuredWatts - (fullLoadWatts * 0.68)))
                    .ThenByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                    .First();
                effPoint = ToPoint(bestAbove, "upper-mid-fallback");
            }
            else
            {
                double synthFrac = Math.Clamp(Math.Max(DefaultEfficiencyFraction, (balSample.RateFraction + 1.0) * 0.5),
                    Math.Min(MaxSubFullFraction, balSample.RateFraction + 0.05), MaxSubFullFraction);
                double synthWatts = Math.Max(0.0, (balWatts + fullLoadWatts) * 0.5);
                double synthUtil = Math.Clamp((balSample.MeasuredGpuUtil + fullLoadGpuUtil) * 0.5, 0.0, 100.0);
                effPoint = new TierOperatingPoint(
                    synthFrac,
                    synthWatts,
                    synthUtil,
                    fullSpeedHps * synthFrac,
                    "synthetic-efficiency");
            }
        }

        TierOperatingPoint balPoint = ToPoint(balSample, balMode);
        return new EfficiencyProfile(
            FullLoadWatts: fullLoadWatts,
            FullLoadGpuUtil: fullLoadGpuUtil,
            HasPowerSensor: true,
            Eco: ecoPoint,
            Balanced: balPoint,
            Efficiency: effPoint,
            Samples: samples);
    }

    private static EfficiencyProfile SelectByGpuUtilFallback(
        double fullSpeedHps,
        double fullLoadGpuUtil,
        List<EfficiencyCurveSample> samples)
    {
        List<EfficiencyCurveSample> balPool = samples
            .Where(s => s.RateFraction <= MaxBalancedFraction)
            .ToList();
        if (balPool.Count == 0)
        {
            balPool = samples;
        }

        EfficiencyCurveSample balSample = balPool
            .Where(s => s.MeasuredGpuUtil <= 55.0)
            .OrderByDescending(s => s.MeasuredHps)
            .FirstOrDefault()
            ?? balPool.OrderBy(s => Math.Abs(s.MeasuredGpuUtil - 45.0)).ThenByDescending(s => s.MeasuredHps).First();

        List<EfficiencyCurveSample> belowBal = samples
            .Where(s => s.RateFraction < balSample.RateFraction - 1e-6)
            .ToList();

        TierOperatingPoint ecoPoint;
        if (belowBal.Count == 0)
        {
            double synthFrac = Math.Clamp(balSample.RateFraction * 0.5, MinTierFraction, Math.Max(MinTierFraction, balSample.RateFraction - 0.01));
            ecoPoint = new TierOperatingPoint(
                synthFrac,
                0.0,
                Math.Max(0.0, balSample.MeasuredGpuUtil * 0.5),
                fullSpeedHps * synthFrac,
                "gpu-util-fallback");
        }
        else
        {
            EfficiencyCurveSample ecoSample = belowBal
                .Where(s => s.MeasuredGpuUtil <= 28.0)
                .OrderByDescending(s => s.MeasuredHps)
                .FirstOrDefault()
                ?? belowBal.OrderBy(s => s.MeasuredGpuUtil).ThenByDescending(s => s.MeasuredHps).First();
            ecoPoint = ToPoint(ecoSample, "gpu-util-fallback");
        }

        List<EfficiencyCurveSample> aboveBal = samples
            .Where(s => s.RateFraction > balSample.RateFraction + 0.05 && s.RateFraction <= MaxSubFullFraction)
            .ToList();

        TierOperatingPoint effPoint;
        if (aboveBal.Count > 0)
        {
            EfficiencyCurveSample effSample = aboveBal
                .Where(s => s.MeasuredGpuUtil <= 75.0)
                .OrderByDescending(s => s.MeasuredHps)
                .FirstOrDefault()
                ?? aboveBal.OrderBy(s => Math.Abs(s.MeasuredGpuUtil - 68.0)).ThenByDescending(s => s.MeasuredHps).First();
            effPoint = ToPoint(effSample, "gpu-util-fallback");
        }
        else
        {
            double synthFrac = Math.Clamp(Math.Max(DefaultEfficiencyFraction, (balSample.RateFraction + 1.0) * 0.5),
                Math.Min(MaxSubFullFraction, balSample.RateFraction + 0.05), MaxSubFullFraction);
            effPoint = new TierOperatingPoint(
                synthFrac,
                0.0,
                Math.Clamp(fullLoadGpuUtil > 0.0 ? fullLoadGpuUtil * synthFrac : 65.0, 0.0, 80.0),
                fullSpeedHps * synthFrac,
                "gpu-util-fallback");
        }

        return new EfficiencyProfile(
            FullLoadWatts: 0.0,
            FullLoadGpuUtil: fullLoadGpuUtil,
            HasPowerSensor: false,
            Eco: ecoPoint,
            Balanced: ToPoint(balSample, "gpu-util-fallback"),
            Efficiency: effPoint,
            Samples: samples);
    }

    private static TierOperatingPoint ToPoint(EfficiencyCurveSample sample, string mode) =>
        new(
            RateFraction: Math.Clamp(sample.RateFraction, MinTierFraction, MaxSubFullFraction),
            EstimatedWatts: Math.Max(0.0, sample.MeasuredWatts),
            EstimatedGpuUtil: Math.Clamp(sample.MeasuredGpuUtil, 0.0, 100.0),
            MeasuredHps: Math.Max(0.0, sample.MeasuredHps),
            SelectionMode: mode);
}
