using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

internal sealed record DraftValidation(
    bool IsValid,
    bool HasError,
    string FormattedPreview,
    string ErrorText,
    KeyIdPattern? Pattern);

internal sealed record TelemetryDisplay(
    string RateNumber,
    string RateUnit,
    string BadgeText,
    string? BadgeTooltip,
    PowerTier DisplayTier,
    bool TierSelectorEnabled,
    string PrimaryButtonText,
    bool CanPrimaryAction,
    double ProgressPercent,
    string ProbabilityText,
    string SingleHashTooltip,
    string CheckedText,
    string CheckedExactTooltip,
    string EtaText,
    bool ActiveTargetFound,
    string? ErrorText);

internal static class DesktopDisplayMapper
{
    public static DraftValidation ValidateDraft(string? input, LocalizationService strings)
    {
        if (KeyIdPattern.TryParse(input, out KeyIdPattern? parsed, out KeyIdValidationError error) && parsed is not null)
        {
            return new DraftValidation(true, false, parsed.FormattedDisplay, string.Empty, parsed);
        }

        bool hasError = error is KeyIdValidationError.InvalidCharacters or KeyIdValidationError.TooLong;
        string errorText = error switch
        {
            KeyIdValidationError.InvalidCharacters => strings["ValidationInvalidChars"],
            KeyIdValidationError.TooLong => strings["ValidationTooLong"],
            _ => string.Empty
        };
        return new DraftValidation(false, hasError, "**** **** **** ****", errorText, null);
    }

    public static TelemetryDisplay BuildDisplay(
        MultiGpuCoordinator coordinator,
        CoordinatorTelemetry t,
        KeyIdPattern? activePattern,
        IReadOnlyList<MatchedKeyRecord> matches,
        LocalizationService strings,
        bool busy)
    {
        bool activeCompute = t.State is CoordinatorState.Running or CoordinatorState.Calibrating;
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        bool hasHardwareGpu = coordinator.Devices.Count > 0;
        bool hasEnabledGpu = coordinator.GetEnabledDevices().Count > 0;
        bool hasBaseline = t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;
        bool targetFound = t.State is not (CoordinatorState.Calibrating or CoordinatorState.Faulted)
            && activePattern is not null
            && matches.Any(m =>
                string.Equals(m.MatchedPattern, activePattern.CanonicalMask, StringComparison.OrdinalIgnoreCase) ||
                activePattern.MatchesKeyIdHex(m.KeyId));

        PowerTier displayTier = (t.State == CoordinatorState.Calibrating || !hasBaseline)
            ? PowerTier.Max
            : t.ActiveTier;

        bool tierEnabled = hasHardwareGpu && !busy && !busyCal && hasBaseline;

        string rateNum = FormattingHelpers.FormatHashRateValue(t.TotalActualHps);
        string rateUnit = FormattingHelpers.FormatHashRateUnit(t.TotalActualHps);

        var (badgeText, badgeTooltip) = ComputeBadge(displayTier, t, strings);

        double progress;
        if (t.State == CoordinatorState.Calibrating)
        {
            progress = Math.Clamp((1.0 - (t.CalibrationRemainingSeconds / coordinator.FullCalibrationDurationSeconds)) * 100.0, 0.0, 100.0);
        }
        else if (targetFound)
        {
            progress = 100.0;
        }
        else
        {
            progress = Math.Clamp(t.CumulativeProbability * 100.0, 0.0, 100.0);
        }

        string probText = targetFound
            ? strings["Found"]
            : FormattingHelpers.FormatLabeledProbability(t.CumulativeProbability, strings["ProbabilityPrefix"]);
        string singleHashTooltip = activePattern is not null
            ? Format(strings["SingleHashProbabilityFormat"], FormattingHelpers.FormatSingleHashProbabilityDetail(activePattern.FixedHexLength))
            : string.Empty;
        string checkedText = FormattingHelpers.FormatCheckedCount(t.TotalChecked);
        string checkedTooltip = FormattingHelpers.FormatCheckedCountExact(t.TotalChecked);
        string etaText = targetFound ? "\u2014" : FormattingHelpers.FormatEta(t.EstimatedMeanWait, strings.EtaFormats);

        string primaryText;
        bool canPrimary;
        switch (t.State)
        {
            case CoordinatorState.Running:
            case CoordinatorState.Paused:
                primaryText = strings["Stop"];
                canPrimary = !busy;
                break;
            case CoordinatorState.Calibrating:
                primaryText = $"{strings["Stop"]} \u00B7 {Format(strings["HudMeasuringFormat"], ((int)Math.Ceiling(Math.Max(0.0, t.CalibrationRemainingSeconds))).ToString())}";
                canPrimary = !busy;
                break;
            case CoordinatorState.Stopping:
                primaryText = strings["Stopping"];
                canPrimary = false;
                break;
            default:
                primaryText = strings["Start"];
                canPrimary = !busy && hasHardwareGpu && hasEnabledGpu && activePattern is not null;
                break;
        }

        string? errText = null;
        if (t.VaultIsFull) errText = strings["VaultFullError"];
        else if (!string.IsNullOrWhiteSpace(t.ErrorMessage))
        {
            errText = t.Failure switch
            {
                SearchFailure.SaveFailed => strings["VaultSaveFailedError"],
                SearchFailure.IncompleteResults => strings["IncompleteSearchResultsError"],
                _ => t.ErrorMessage
            };
        }
        else if (!hasHardwareGpu)
        {
            errText = strings["NoGpuError"];
        }

        return new TelemetryDisplay(
            rateNum, rateUnit, badgeText, badgeTooltip, displayTier, tierEnabled,
            primaryText, canPrimary, progress, probText, singleHashTooltip,
            checkedText, checkedTooltip, etaText, targetFound, errText);
    }

    private static (string Text, string? Tooltip) ComputeBadge(PowerTier displayTier, CoordinatorTelemetry t, LocalizationService strings)
    {
        if (t.State == CoordinatorState.Stopping) return (strings["Stopping"], null);
        if (t.State is CoordinatorState.Running or CoordinatorState.Calibrating)
        {
            if (t.State == CoordinatorState.Running && t.IsOverloadYielding && t.TotalActualHps <= 0.0)
                return (strings["YieldingBadge"], strings["YieldingSuffix"]);

            if (t.HasPowerSensor && t.TotalPowerWatts > 0.0)
            {
                double hpw = t.TotalActualHps > 0.0 ? t.TotalActualHps / t.TotalPowerWatts : 0.0;
                string eff = FormattingHelpers.FormatEfficiencyPerWatt(hpw);
                string watts = FormattingHelpers.FormatWatts(t.TotalPowerWatts);
                string tip = Format(strings["PowerTooltipFormat"], watts, eff);
                return (eff, t.IsOverloadYielding ? $"{tip} ({strings["YieldingSuffix"]})" : tip);
            }

            if (t.HasGpuUtilSensor)
            {
                int util = (int)Math.Clamp(Math.Round(t.AggregateGpuUtilPercent, MidpointRounding.AwayFromZero), 0.0, 100.0);
                string tip = Format(strings["GpuLoadTooltipFormat"], util.ToString());
                return ($"{util}%", t.IsOverloadYielding ? $"{tip} ({strings["YieldingSuffix"]})" : tip);
            }

            // Never fabricate 0 W when power sensor is absent.
            return (strings["Tier" + displayTier], null);
        }

        return t.State switch
        {
            CoordinatorState.Paused => (strings["Paused"], null),
            CoordinatorState.Faulted => (strings["Error"], null),
            _ => (strings["Idle"], null)
        };
    }

    public static (bool Visible, string Text) ComputeEmptyResultsHint(
        CoordinatorTelemetry t,
        bool hasMatches,
        bool minimizeToTrayOnClose,
        LocalizationService strings)
    {
        if (hasMatches || !t.AllEnabledFullyCalibrated)
        {
            return (false, string.Empty);
        }

        if (t.State == CoordinatorState.Running && t.HasHighIdlePowerFloor && t.ActiveTier != PowerTier.Max)
        {
            return (true, strings["HighIdlePowerHint"]);
        }

        if (minimizeToTrayOnClose && t.State is CoordinatorState.Running or CoordinatorState.Calibrating)
        {
            return (true, strings["TraySpeedupGenericHint"]);
        }

        return (false, string.Empty);
    }

    public static string Format(string template, params string[] args)
    {
        string result = template;
        for (int i = 0; i < args.Length; i++)
            result = result.Replace("{" + i + "}", args[i], StringComparison.Ordinal);
        return result;
    }
}
