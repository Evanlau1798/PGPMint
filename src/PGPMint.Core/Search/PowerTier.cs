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
    public bool IsHighIdlePowerFloor { get; init; }

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
    public const double DefaultEfficiencyFraction = 0.68;
    public const double MinTierFraction = 0.02;
    public const double MaxBalancedFraction = 0.45;
    public const double MaxEfficiencyFraction = 0.78;
    public const double MaxSubFullFraction = 0.85;
    public const double RampStartFraction = 0.92;
    public const double RampEndFraction = 0.05;
    public const double RampConcavityExponent = 1.65;
    public const double HighIdleFloorMinWattRatio = 0.20;
    public const double HighIdleFloorMaxLowLoadEffRatio = 0.85;
    public const double HighIdleEcoMaxWattRatio = 0.32;

    public static bool HasHighIdlePowerFloor(CalibrationRecord? record)
    {
        if (record?.EfficiencyProfile is not { HasPowerSensor: true } profile)
        {
            return false;
        }

        if (profile.IsHighIdlePowerFloor)
        {
            return true;
        }

        return DetectHighIdlePowerFloor(
            CalibrationValidator.GetBaselineHps(record),
            profile.FullLoadWatts,
            profile.Samples);
    }

    internal static bool DetectHighIdlePowerFloor(
        double fullSpeedHps,
        double fullLoadWatts,
        IReadOnlyList<EfficiencyCurveSample>? rawSamples)
    {
        if (!double.IsFinite(fullSpeedHps) || fullSpeedHps <= 0.0 ||
            !double.IsFinite(fullLoadWatts) || fullLoadWatts <= 0.0)
        {
            return false;
        }

        List<EfficiencyCurveSample> valid = (rawSamples ?? Array.Empty<EfficiencyCurveSample>())
            .Where(s => double.IsFinite(s.RateFraction) && s.RateFraction >= MinTierFraction && s.RateFraction <= 0.95 &&
                        double.IsFinite(s.MeasuredHps) && s.MeasuredHps > 0.0 &&
                        double.IsFinite(s.MeasuredWatts) && s.MeasuredWatts > 0.0)
            .OrderByDescending(s => s.RateFraction)
            .ToList();

        if (valid.Count < 2)
        {
            return false;
        }

        List<EfficiencyCurveSample> samples = SmoothDenseCurve(valid, fullSpeedHps);
        if (samples.Min(s => s.RateFraction) > 0.20)
        {
            return false;
        }

        double minWatts = samples.Min(s => s.MeasuredWatts);
        if (minWatts < fullLoadWatts * HighIdleFloorMinWattRatio || fullLoadWatts <= minWatts * 1.25)
        {
            return false;
        }

        List<EfficiencyCurveSample> lowMid = samples
            .Where(s => s.RateFraction <= MaxBalancedFraction)
            .ToList();
        if (lowMid.Count == 0)
        {
            return false;
        }

        double fullMhpw = fullSpeedHps / fullLoadWatts;
        double bestLowMidMhpw = lowMid.Max(s => s.MeasuredHps / s.MeasuredWatts);
        return bestLowMidMhpw < fullMhpw * HighIdleFloorMaxLowLoadEffRatio;
    }

    /// <summary>
    /// Smooth concave ramp from 92% down to 5%: traverses upper P0 swiftly while spending
    /// more than half of the sweep duration below 35% so low-power P-states settle cleanly.
    /// </summary>
    public static double RampFractionAtProgress(double progress)
    {
        double u = double.IsFinite(progress) ? Math.Clamp(progress, 0.0, 1.0) : 1.0;
        return RampEndFraction + ((RampStartFraction - RampEndFraction) * Math.Pow(1.0 - u, RampConcavityExponent));
    }

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
            .Where(s => double.IsFinite(s.RateFraction) && s.RateFraction >= MinTierFraction && s.RateFraction <= 0.95 &&
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

    private static List<EfficiencyCurveSample> SmoothDenseCurve(List<EfficiencyCurveSample> sortedDesc, double fullSpeedHps)
    {
        if (sortedDesc.Count < 12)
        {
            return sortedDesc;
        }

        static EfficiencyCurveSample ClampEndpointHps(EfficiencyCurveSample ep, double fullHps)
        {
            if (fullHps > 0.0 && ep.RateFraction > 0.0)
            {
                double maxPlausible = fullHps * ep.RateFraction * 1.15;
                if (ep.MeasuredHps > maxPlausible)
                {
                    return ep with { MeasuredHps = maxPlausible };
                }
            }
            return ep;
        }

        var smoothed = new List<EfficiencyCurveSample>(sortedDesc.Count) { ClampEndpointHps(sortedDesc[0], fullSpeedHps) };
        for (int i = 1; i < sortedDesc.Count - 1; i++)
        {
            EfficiencyCurveSample prev = sortedDesc[i - 1];
            EfficiencyCurveSample cur = sortedDesc[i];
            EfficiencyCurveSample next = sortedDesc[i + 1];
            double medHps = Median3(prev.MeasuredHps, cur.MeasuredHps, next.MeasuredHps);
            if (fullSpeedHps > 0.0 && cur.RateFraction > 0.0)
            {
                medHps = Math.Min(medHps, fullSpeedHps * cur.RateFraction * 1.15);
            }
            smoothed.Add(cur with
            {
                MeasuredHps = medHps,
                MeasuredWatts = Median3(prev.MeasuredWatts, cur.MeasuredWatts, next.MeasuredWatts),
                MeasuredGpuUtil = Median3(prev.MeasuredGpuUtil, cur.MeasuredGpuUtil, next.MeasuredGpuUtil)
            });
        }

        smoothed.Add(ClampEndpointHps(sortedDesc[^1], fullSpeedHps));
        return smoothed;
    }

    private static double Median3(double a, double b, double c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return b;
    }

    private static EfficiencyProfile SelectByPowerCurve(
        double fullSpeedHps,
        double fullLoadWatts,
        double fullLoadGpuUtil,
        List<EfficiencyCurveSample> rawSamples)
    {
        List<EfficiencyCurveSample> samples = SmoothDenseCurve(rawSamples, fullSpeedHps);
        bool isHighIdlePowerFloor = DetectHighIdlePowerFloor(fullSpeedHps, fullLoadWatts, rawSamples);

        // 1. Balanced tier (low-to-mid power peak efficiency sweet spot, <= MaxBalancedFraction).
        List<EfficiencyCurveSample> balPool = samples
            .Where(s => s.RateFraction <= MaxBalancedFraction)
            .ToList();
        if (balPool.Count == 0)
        {
            balPool = samples;
        }

        List<EfficiencyCurveSample> naturalBalCandidates = balPool
            .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.75)
            .ToList();

        EfficiencyCurveSample? naturalBal = null;
        if (naturalBalCandidates.Count > 0)
        {
            double bestEff = naturalBalCandidates.Max(s => s.MeasuredHps / s.MeasuredWatts);
            EfficiencyCurveSample peak = naturalBalCandidates
                .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                .ThenByDescending(s => s.MeasuredHps)
                .First();

            if (peak.MeasuredWatts <= fullLoadWatts * 0.60)
            {
                // Within 3% of the peak MH/W (and still <= 60% power), prefer higher throughput so
                // Balanced lands on the upper knee of the low-power P-state instead of hugging Eco.
                naturalBal = naturalBalCandidates
                    .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.60 &&
                                (s.MeasuredHps / s.MeasuredWatts) >= bestEff * 0.97)
                    .OrderByDescending(s => s.MeasuredHps)
                    .First();
            }
            else
            {
                naturalBal = peak;
            }
        }

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

        // 2. Eco tier: highest compute on the lowest-power plateau clearly below Balanced.
        double minWatts = samples.Min(s => s.MeasuredWatts);
        double balWatts = balSample.MeasuredWatts;
        List<EfficiencyCurveSample> belowBal = samples
            .Where(s => s.RateFraction < balSample.RateFraction - 0.02)
            .ToList();
        if (belowBal.Count == 0)
        {
            belowBal = samples
                .Where(s => s.RateFraction < balSample.RateFraction - 1e-6)
                .ToList();
        }

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

            EfficiencyCurveSample? highIdleEco = isHighIdlePowerFloor
                ? belowBal
                    .Where(s => s.MeasuredWatts <= fullLoadWatts * HighIdleEcoMaxWattRatio &&
                                (s.MeasuredWatts <= balWatts * 0.80 || balWatts - s.MeasuredWatts >= minGapBelowBal))
                    .OrderByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                    .ThenByDescending(s => s.MeasuredHps)
                    .FirstOrDefault()
                : null;

            EfficiencyCurveSample? naturalEco = !isHighIdlePowerFloor
                ? belowBal
                    .Where(s => s.MeasuredWatts <= plateauCeiling &&
                                (s.MeasuredWatts <= balWatts * 0.80 || balWatts - s.MeasuredWatts >= minGapBelowBal))
                    .OrderByDescending(s => s.MeasuredHps)
                    .FirstOrDefault()
                : null;

            if (highIdleEco is not null)
            {
                ecoPoint = ToPoint(highIdleEco, "high-idle-efficiency");
            }
            else if (naturalEco is not null && naturalEco.MeasuredWatts <= fullLoadWatts * 0.32)
            {
                ecoPoint = ToPoint(naturalEco, "natural-plateau");
            }
            else
            {
                EfficiencyCurveSample? bandEco = belowBal
                    .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.30 && s.MeasuredWatts < balWatts)
                    .OrderByDescending(s => s.MeasuredHps)
                    .FirstOrDefault()
                    ?? belowBal
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

        // 3. Efficiency tier (full-curve upper-mid high-efficiency zone: [max(0.55, bal+0.15) .. 0.78] under <= 88% full power):
        // avoids the lower-P0 efficiency valley while staying well separated from both Balanced and Max.
        double effMinFraction = Math.Max(0.55, balSample.RateFraction + 0.15);
        List<EfficiencyCurveSample> aboveBal = samples
            .Where(s => s.RateFraction > balSample.RateFraction + 0.05 && s.RateFraction <= MaxEfficiencyFraction)
            .ToList();

        TierOperatingPoint effPoint;
        EfficiencyCurveSample? upperMidPeak = aboveBal
            .Where(s => s.RateFraction >= effMinFraction && s.MeasuredWatts <= fullLoadWatts * 0.88)
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
                .Where(s => s.MeasuredWatts <= fullLoadWatts * 0.88)
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
                    .OrderBy(s => Math.Abs(s.MeasuredWatts - (fullLoadWatts * 0.80)))
                    .ThenByDescending(s => s.MeasuredHps / s.MeasuredWatts)
                    .First();
                effPoint = ToPoint(bestAbove, "upper-mid-fallback");
            }
            else
            {
                double synthFrac = Math.Clamp(Math.Max(DefaultEfficiencyFraction, (balSample.RateFraction + 1.0) * 0.5),
                    Math.Min(MaxEfficiencyFraction, balSample.RateFraction + 0.05), MaxEfficiencyFraction);
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
        if (aboveBal.Count == 0 && balPoint.RateFraction >= MaxSubFullFraction - 0.03)
        {
            double adjustedBalFrac = Math.Clamp(MaxSubFullFraction - 0.05, MinTierFraction + 0.02, MaxSubFullFraction - 0.05);
            balPoint = balPoint with
            {
                RateFraction = adjustedBalFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * adjustedBalFrac : balPoint.MeasuredHps
            };
        }

        if (ecoPoint.RateFraction >= balPoint.RateFraction)
        {
            double adjustedEcoFrac = Math.Clamp(balPoint.RateFraction * 0.5, MinTierFraction, Math.Max(MinTierFraction, balPoint.RateFraction - 0.01));
            ecoPoint = ecoPoint with
            {
                RateFraction = adjustedEcoFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * adjustedEcoFrac : ecoPoint.MeasuredHps,
                SelectionMode = "synthetic-eco"
            };
        }

        if (effPoint.RateFraction <= balPoint.RateFraction)
        {
            double synthFrac = Math.Clamp(
                Math.Max(DefaultEfficiencyFraction, (balPoint.RateFraction + 1.0) * 0.5),
                Math.Min(MaxSubFullFraction, balPoint.RateFraction + 0.04),
                MaxSubFullFraction);
            effPoint = effPoint with
            {
                RateFraction = synthFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * synthFrac : effPoint.MeasuredHps,
                SelectionMode = "synthetic-efficiency"
            };
        }

        return new EfficiencyProfile(
            FullLoadWatts: fullLoadWatts,
            FullLoadGpuUtil: fullLoadGpuUtil,
            HasPowerSensor: true,
            Eco: ecoPoint,
            Balanced: balPoint,
            Efficiency: effPoint,
            Samples: rawSamples)
        {
            IsHighIdlePowerFloor = isHighIdlePowerFloor
        };
    }

    private static EfficiencyProfile SelectByGpuUtilFallback(
        double fullSpeedHps,
        double fullLoadGpuUtil,
        List<EfficiencyCurveSample> rawSamples)
    {
        List<EfficiencyCurveSample> samples = SmoothDenseCurve(rawSamples, fullSpeedHps);

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
            .Where(s => s.RateFraction < balSample.RateFraction - 0.02)
            .ToList();
        if (belowBal.Count == 0)
        {
            belowBal = samples
                .Where(s => s.RateFraction < balSample.RateFraction - 1e-6)
                .ToList();
        }

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

        double effMinFraction = Math.Max(0.55, balSample.RateFraction + 0.15);
        List<EfficiencyCurveSample> aboveBal = samples
            .Where(s => s.RateFraction > balSample.RateFraction + 0.05 && s.RateFraction <= MaxEfficiencyFraction)
            .ToList();

        TierOperatingPoint effPoint;
        if (aboveBal.Count > 0)
        {
            EfficiencyCurveSample effSample = aboveBal
                .Where(s => s.RateFraction >= effMinFraction && s.MeasuredGpuUtil <= 80.0)
                .OrderByDescending(s => s.MeasuredHps)
                .FirstOrDefault()
                ?? aboveBal
                    .Where(s => s.MeasuredGpuUtil <= 80.0)
                    .OrderByDescending(s => s.MeasuredHps)
                    .FirstOrDefault()
                ?? aboveBal.OrderBy(s => Math.Abs(s.MeasuredGpuUtil - 72.0)).ThenByDescending(s => s.MeasuredHps).First();
            effPoint = ToPoint(effSample, "gpu-util-fallback");
        }
        else
        {
            double synthFrac = Math.Clamp(Math.Max(DefaultEfficiencyFraction, (balSample.RateFraction + 1.0) * 0.5),
                Math.Min(MaxEfficiencyFraction, balSample.RateFraction + 0.05), MaxEfficiencyFraction);
            effPoint = new TierOperatingPoint(
                synthFrac,
                0.0,
                Math.Clamp(fullLoadGpuUtil > 0.0 ? fullLoadGpuUtil * synthFrac : 70.0, 0.0, 80.0),
                fullSpeedHps * synthFrac,
                "gpu-util-fallback");
        }

        TierOperatingPoint balPoint = ToPoint(balSample, "gpu-util-fallback");
        if (aboveBal.Count == 0 && balPoint.RateFraction >= MaxSubFullFraction - 0.03)
        {
            double adjustedBalFrac = Math.Clamp(MaxSubFullFraction - 0.05, MinTierFraction + 0.02, MaxSubFullFraction - 0.05);
            balPoint = balPoint with
            {
                RateFraction = adjustedBalFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * adjustedBalFrac : balPoint.MeasuredHps
            };
        }

        if (ecoPoint.RateFraction >= balPoint.RateFraction)
        {
            double adjustedEcoFrac = Math.Clamp(balPoint.RateFraction * 0.5, MinTierFraction, Math.Max(MinTierFraction, balPoint.RateFraction - 0.01));
            ecoPoint = ecoPoint with
            {
                RateFraction = adjustedEcoFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * adjustedEcoFrac : ecoPoint.MeasuredHps
            };
        }

        if (effPoint.RateFraction <= balPoint.RateFraction)
        {
            double synthFrac = Math.Clamp(
                Math.Max(DefaultEfficiencyFraction, (balPoint.RateFraction + 1.0) * 0.5),
                Math.Min(MaxSubFullFraction, balPoint.RateFraction + 0.04),
                MaxSubFullFraction);
            effPoint = effPoint with
            {
                RateFraction = synthFrac,
                MeasuredHps = fullSpeedHps > 0.0 ? fullSpeedHps * synthFrac : effPoint.MeasuredHps
            };
        }

        return new EfficiencyProfile(
            FullLoadWatts: 0.0,
            FullLoadGpuUtil: fullLoadGpuUtil,
            HasPowerSensor: false,
            Eco: ecoPoint,
            Balanced: balPoint,
            Efficiency: effPoint,
            Samples: rawSamples);
    }

    private static TierOperatingPoint ToPoint(EfficiencyCurveSample sample, string mode) =>
        new(
            RateFraction: Math.Clamp(sample.RateFraction, MinTierFraction, MaxSubFullFraction),
            EstimatedWatts: Math.Max(0.0, sample.MeasuredWatts),
            EstimatedGpuUtil: Math.Clamp(sample.MeasuredGpuUtil, 0.0, 100.0),
            MeasuredHps: Math.Max(0.0, sample.MeasuredHps),
            SelectionMode: mode);
}
