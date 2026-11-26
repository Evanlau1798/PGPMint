using System.Diagnostics;
using Microsoft.UI.Dispatching;
using PGPMint.App.Services;
using PGPMint.Core.Search;

namespace PGPMint.App.ViewModels;

public sealed partial class MainViewModel
{
    private void ApplyTelemetry(CoordinatorTelemetry t, bool forceTrayHintTextUpdate = false)
    {
        State = t.State;
        bool activeCompute = t.State is CoordinatorState.Running or CoordinatorState.Calibrating;
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        _powerKeepAlive.SetActive(activeCompute);

        bool hasMeasuredBaseline = t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;
        bool activeTargetFound = t.State is not (CoordinatorState.Calibrating or CoordinatorState.Faulted)
            && HasMatchForActivePattern();

        SelectedTier = t.ActiveTier;
        DisplayTier = (t.State == CoordinatorState.Calibrating || !hasMeasuredBaseline)
            ? PowerTier.Max
            : t.ActiveTier;

        ActualRateNumber = FormattingHelpers.FormatHashRateValue(t.TotalActualHps);
        ActualRateUnit = FormattingHelpers.FormatHashRateUnit(t.TotalActualHps);

        UpdateEfficiencyBadge(DisplayTier, t);
        UpdateTraySpeedupHint(t, forceTrayHintTextUpdate);

        double rawProgressPercent;
        if (t.State == CoordinatorState.Calibrating)
        {
            double calTotal = _coordinator.FullCalibrationDurationSeconds;
            rawProgressPercent = Math.Clamp((1.0 - (t.CalibrationRemainingSeconds / calTotal)) * 100.0, 0.0, 100.0);
        }
        else if (activeTargetFound)
        {
            rawProgressPercent = 100.0;
        }
        else
        {
            rawProgressPercent = Math.Clamp(t.CumulativeProbability * 100.0, 0.0, 100.0);
        }

        _snapTimestamp = Stopwatch.GetTimestamp();
        _snapTotalChecked = t.TotalChecked;
        _snapActualHps = t.TotalActualHps;

        if (t.State != CoordinatorState.Running || _mainWindowInTray ||
            activeTargetFound ||
            rawProgressPercent < _smoothProgressCurrent - 0.5)
        {
            _smoothProgressCurrent = t.State == CoordinatorState.Calibrating ? 0.0 : rawProgressPercent;
            if (!_mainWindowInTray ||
                Math.Abs(_smoothProgressCurrent - ProgressPercent) >= 0.04 ||
                _smoothProgressCurrent is 0.0 or 100.0)
            {
                ProgressPercent = _smoothProgressCurrent;
            }
        }
        else if (!_smoothProgressTimer.IsRunning)
        {
            _smoothProgressCurrent = rawProgressPercent;
            ProgressPercent = rawProgressPercent;
        }

        SyncSmoothProgressTimer();

        ProbabilityText = activeTargetFound
            ? Loc.Found
            : FormattingHelpers.FormatLabeledProbability(t.CumulativeProbability, Loc.ProbabilityPrefix);
        SingleHashProbabilityTooltip = _activePatternObj is not null
            ? Loc.FormatSingleHashProbability(FormattingHelpers.FormatSingleHashProbabilityDetail(_activePatternObj.FixedHexLength))
            : string.Empty;
        CheckedCountText = FormattingHelpers.FormatCheckedCount(t.TotalChecked);
        CheckedCountTooltip = FormattingHelpers.FormatCheckedCountExact(t.TotalChecked);
        LiveEtaText = activeTargetFound
            ? "\u2014"
            : FormattingHelpers.FormatEta(t.EstimatedMeanWait, Loc.EtaFormats);

        bool vaultFullCleared = _wasVaultFull && !t.VaultIsFull;
        _wasVaultFull = t.VaultIsFull;
        if (t.VaultIsFull || !string.IsNullOrWhiteSpace(t.ErrorMessage))
        {
            HasError = true;
            ErrorText = t.VaultIsFull ? Loc.VaultFullError : t.Failure switch
            {
                SearchFailure.SaveFailed => Loc.VaultSaveFailedError,
                SearchFailure.IncompleteResults => Loc.IncompleteSearchResultsError,
                _ => t.ErrorMessage!
            };
        }
        else if (vaultFullCleared || (HasHardwareGpu && t.State != CoordinatorState.Faulted && activeCompute))
        {
            HasError = false;
            ErrorText = string.Empty;
        }

        bool multiGpu = Gpus.Count > 1;
        foreach (GpuTelemetrySnapshot devSnap in t.Devices)
        {
            GpuItemViewModel? vm = Gpus.FirstOrDefault(g => g.DeviceKey == devSnap.DeviceKey);
            if (vm is null)
            {
                continue;
            }

            _suppressGpuToggleCallback = true;
            vm.IsEnabled = devSnap.IsEnabled;
            _suppressGpuToggleCallback = false;

            vm.BackendName = devSnap.BackendName;
            vm.CanToggle = multiGpu && !busyCal;
        }

        UpdateActionPermissions(t);
        ValidateDraftTarget();
        UpdateTrayState(t, rawProgressPercent, activeTargetFound, forceTrayHintTextUpdate);
    }

    private void SyncSmoothProgressTimer()
    {
        // During calibration the button countdown provides feedback without animating the progress bar.
        bool shouldRun = State == CoordinatorState.Running && !_mainWindowInTray &&
                         !ShowSetupWizard && !HasMatchForActivePattern();
        if (shouldRun && !_smoothProgressTimer.IsRunning)
        {
            _smoothProgressTimer.Start();
        }
        else if (!shouldRun && _smoothProgressTimer.IsRunning)
        {
            _smoothProgressTimer.Stop();
        }
    }

    private void OnSmoothProgressTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_mainWindowInTray || ShowSetupWizard || State != CoordinatorState.Running)
        {
            _smoothProgressTimer.Stop();
            return;
        }

        if (_activePatternObj is null)
        {
            return;
        }

        if (HasMatchForActivePattern())
        {
            _smoothProgressCurrent = 100.0;
            if (ProgressPercent < 100.0)
            {
                ProgressPercent = 100.0;
            }
            _smoothProgressTimer.Stop();
            return;
        }

        double elapsed = Math.Min(1.0, Stopwatch.GetElapsedTime(_snapTimestamp).TotalSeconds);
        ulong extra = _snapActualHps > 0.0 ? (ulong)Math.Max(0.0, _snapActualHps * elapsed) : 0UL;
        double prob = _activePatternObj.ComputeCumulativeProbability(_snapTotalChecked + extra);
        double targetPct = Math.Clamp(prob * 100.0, 0.0, 100.0);

        if (targetPct < _smoothProgressCurrent - 0.5)
        {
            _smoothProgressCurrent = targetPct;
        }
        else if (targetPct > _smoothProgressCurrent)
        {
            _smoothProgressCurrent += (targetPct - _smoothProgressCurrent) * 0.35;
        }

        // Only notify XAML ProgressBar when the delta is visually perceptible (>= 0.04%),
        // avoiding unnecessary WinUI D3D11 UI composition passes on slow-moving long suffixes.
        if (Math.Abs(_smoothProgressCurrent - ProgressPercent) >= 0.04)
        {
            ProgressPercent = _smoothProgressCurrent;
        }
    }

    private void UpdateEfficiencyBadge(PowerTier displayTier, CoordinatorTelemetry t)
    {
        if (t.State == CoordinatorState.Stopping)
        {
            EfficiencyBadgeText = Loc.Stopping;
            EfficiencyBadgeTooltip = null;
            return;
        }

        if (t.State is CoordinatorState.Running or CoordinatorState.Calibrating)
        {
            if (t.State == CoordinatorState.Running && t.IsOverloadYielding && t.TotalActualHps <= 0.0)
            {
                EfficiencyBadgeText = Loc.YieldingBadge;
                if (t.HasPowerSensor && t.TotalPowerWatts > 0.0)
                {
                    string wattsText = FormattingHelpers.FormatWatts(t.TotalPowerWatts);
                    EfficiencyBadgeTooltip = Loc.FormatPowerTooltip(wattsText, Loc.YieldingSuffix);
                }
                else if (t.HasGpuUtilSensor)
                {
                    int utilPct = (int)Math.Clamp(Math.Round(t.AggregateGpuUtilPercent, MidpointRounding.AwayFromZero), 0.0, 100.0);
                    EfficiencyBadgeTooltip = $"{Loc.FormatGpuLoadTooltip(utilPct)} ({Loc.YieldingSuffix})";
                }
                else
                {
                    EfficiencyBadgeTooltip = Loc.YieldingSuffix;
                }
                return;
            }

            if (t.HasPowerSensor && t.TotalPowerWatts > 0.0)
            {
                double hpw = t.TotalActualHps > 0.0 ? t.TotalActualHps / t.TotalPowerWatts : 0.0;
                string effText = FormattingHelpers.FormatEfficiencyPerWatt(hpw);
                EfficiencyBadgeText = effText;
                string wattsText = FormattingHelpers.FormatWatts(t.TotalPowerWatts);
                string tip = Loc.FormatPowerTooltip(wattsText, effText);
                EfficiencyBadgeTooltip = t.IsOverloadYielding ? $"{tip} ({Loc.YieldingSuffix})" : tip;
                return;
            }

            if (t.HasGpuUtilSensor)
            {
                int utilPct = (int)Math.Clamp(Math.Round(t.AggregateGpuUtilPercent, MidpointRounding.AwayFromZero), 0.0, 100.0);
                EfficiencyBadgeText = $"{utilPct}%";
                string tip = Loc.FormatGpuLoadTooltip(utilPct);
                EfficiencyBadgeTooltip = t.IsOverloadYielding ? $"{tip} ({Loc.YieldingSuffix})" : tip;
                return;
            }

            EfficiencyBadgeText = Loc.GetTierLabel(displayTier);
            EfficiencyBadgeTooltip = null;
            return;
        }

        EfficiencyBadgeText = t.State switch
        {
            CoordinatorState.Paused => Loc.Paused,
            CoordinatorState.Faulted => Loc.Error,
            _ => Loc.Idle
        };
        EfficiencyBadgeTooltip = null;
    }

    private bool ShouldShowHighIdlePowerHint(CoordinatorTelemetry t) =>
        !HasMatches &&
        t.State == CoordinatorState.Running &&
        t.AllEnabledFullyCalibrated &&
        t.TotalBaselineHps > 0.0 &&
        t.HasHighIdlePowerFloor &&
        t.ActiveTier != PowerTier.Max;

    private void UpdateShowTraySpeedupHint(CoordinatorTelemetry t)
    {
        bool hasMeasuredBaseline = t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;
        bool showHighIdle = ShouldShowHighIdlePowerHint(t);
        bool showTray = MinimizeToTrayOnClose &&
                        !HasMatches &&
                        t.State == CoordinatorState.Running &&
                        hasMeasuredBaseline;
        ShowTraySpeedupHint = showHighIdle || showTray;
    }

    private void UpdateTraySpeedupHint(CoordinatorTelemetry t, bool forceUiUpdate = false)
    {
        UpdateShowTraySpeedupHint(t);

        bool activeFullSpeed = t.IsFullSpeed &&
                               !t.IsGpuContended &&
                               t.State == CoordinatorState.Running &&
                               t.TotalActualHps > 0.0;

        if (activeFullSpeed)
        {
            if (_mainWindowInTray)
            {
                _trayFullSpeedEmaHps = _trayFullSpeedEmaHps <= 0.0
                    ? t.TotalActualHps
                    : (_trayFullSpeedEmaHps * 0.6) + (t.TotalActualHps * 0.4);
                _persistedTrayFullSpeedHps = _trayFullSpeedEmaHps;

                long nowTick = Environment.TickCount64;
                if (nowTick - _lastTraySpeedPersistTick >= 15_000)
                {
                    _lastTraySpeedPersistTick = nowTick;
                    double toSave = _persistedTrayFullSpeedHps;
                    _stateStore.Update(s => s.TrayFullSpeedHps = toSave);
                }
            }
            else
            {
                _fgFullSpeedEmaHps = _fgFullSpeedEmaHps <= 0.0
                    ? t.TotalActualHps
                    : (_fgFullSpeedEmaHps * 0.6) + (t.TotalActualHps * 0.4);
            }
        }
        else if (t.State == CoordinatorState.Idle)
        {
            _fgFullSpeedEmaHps = 0.0;
        }

        if (t.TotalBaselineHps > 0.0 && _persistedTrayFullSpeedHps > t.TotalBaselineHps * 1.35)
        {
            // Reset stale tray sample if GPU selection changed significantly
            _persistedTrayFullSpeedHps = 0.0;
            _trayFullSpeedEmaHps = 0.0;
        }

        bool showHighIdle = ShouldShowHighIdlePowerHint(t);
        if (showHighIdle != _showingHighIdlePowerHint)
        {
            _showingHighIdlePowerHint = showHighIdle;
            forceUiUpdate = true;
        }

        long nowHintTick = Environment.TickCount64;
        if (!forceUiUpdate &&
            !string.IsNullOrEmpty(TraySpeedupHintText) &&
            nowHintTick - _lastTrayHintUiUpdateTick < 1000)
        {
            return;
        }

        _lastTrayHintUiUpdateTick = nowHintTick;

        if (showHighIdle)
        {
            TraySpeedupHintText = Loc.HighIdlePowerHint;
            return;
        }

        double refTrayHps = Math.Max(_persistedTrayFullSpeedHps, t.TotalBaselineHps);
        double fgHps = _fgFullSpeedEmaHps > 0.0 ? _fgFullSpeedEmaHps : (activeFullSpeed && !_mainWindowInTray ? t.TotalActualHps : 0.0);

        if (fgHps > 0.0 && refTrayHps > fgHps)
        {
            int gainPct = (int)Math.Round(((refTrayHps - fgHps) / fgHps) * 100.0, MidpointRounding.AwayFromZero);
            if (gainPct is >= 1 and <= 40)
            {
                TraySpeedupHintText = Loc.FormatTraySpeedupMeasured(gainPct);
                return;
            }
        }

        TraySpeedupHintText = Loc.TraySpeedupGenericHint;
    }

    private void UpdateActionPermissions(CoordinatorTelemetry t)
    {
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        bool hasMeasuredBaseline = t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;
        CanEditTarget = t.State is CoordinatorState.Idle or CoordinatorState.Faulted;
        IsTierSelectorEnabled = HasHardwareGpu && !busyCal && hasMeasuredBaseline;

        bool hasValidActiveTarget = KeyIdPattern.TryParse(ActiveTargetSuffix, out _, out _);
        CanRecalibrate = HasHardwareGpu && CanEditTarget && hasValidActiveTarget;

        switch (t.State)
        {
            case CoordinatorState.Running:
            case CoordinatorState.Paused:
                PrimaryButtonText = Loc.Stop;
                CanPrimaryAction = true;
                break;
            case CoordinatorState.Calibrating:
                PrimaryButtonText = CalibrationStopText(t.CalibrationRemainingSeconds);
                CanPrimaryAction = true;
                break;
            case CoordinatorState.Stopping:
                PrimaryButtonText = Loc.Stopping;
                CanPrimaryAction = false;
                break;
            default:
                PrimaryButtonText = Loc.Start;
                CanPrimaryAction = HasHardwareGpu && hasValidActiveTarget;
                break;
        }
    }

    private string CalibrationStopText(double remainingSeconds) =>
        $"{Loc.Stop} · {Loc.FormatHudMeasuring((int)Math.Ceiling(remainingSeconds))}";

    private void UpdateTrayState(
        CoordinatorTelemetry t,
        double rawProgressPercent,
        bool activeTargetFound,
        bool forceTooltipUpdate = false)
    {
        string toggleLabel = t.State is CoordinatorState.Calibrating or CoordinatorState.Running or CoordinatorState.Paused
            ? Loc.Stop
            : Loc.Start;

        string rateCompact = FormattingHelpers.FormatHashRateCompact(t.TotalActualHps);
        string runningRateLabel = (t.IsOverloadYielding && t.TotalActualHps <= 0.0)
            ? Loc.YieldingBadge
            : rateCompact;
        string probCompact = FormattingHelpers.FormatProbability(t.CumulativeProbability);
        bool hasRate = t.TotalActualHps > 0.0;
        int remainSec = (int)Math.Ceiling(t.CalibrationRemainingSeconds);
        string hudMeasuring = Loc.FormatHudMeasuring(remainSec);

        string summaryLine1 = t.State switch
        {
            CoordinatorState.Running => $"{runningRateLabel}  |  {ProbabilityText}  |  {LiveEtaText}",
            CoordinatorState.Calibrating => hasRate
                ? $"{hudMeasuring}  |  {rateCompact}"
                : hudMeasuring,
            CoordinatorState.Stopping => Loc.Stopping,
            CoordinatorState.Faulted => Loc.Error,
            _ => (t.TotalChecked > 0 || activeTargetFound)
                ? $"{Loc.Idle}  |  {ProbabilityText}"
                : Loc.Idle
        };

        string summaryLine2 = string.Empty;
        if (!string.IsNullOrEmpty(ActiveTargetSuffix))
        {
            summaryLine2 = t.TotalChecked > 0
                ? $"{ActiveFormattedPreview}  |  {CheckedCountText}"
                : ActiveFormattedPreview;
        }

        bool canToggle = !ShowSetupWizard && CanPrimaryAction;
        bool canSelectTier = !ShowSetupWizard && IsTierSelectorEnabled;
        _trayIcon.UpdateMenuState(
            Loc.Open,
            toggleLabel,
            canToggle,
            Loc.TierEco,
            Loc.TierBalanced,
            Loc.TierEfficiency,
            Loc.TierMax,
            DisplayTier,
            canSelectTier,
            Loc.TrayMiniHud,
            ShowMiniHudInTray,
            Loc.Quit,
            summaryLine1,
            summaryLine2);

        long nowTooltipTick = Environment.TickCount64;
        bool throttleTooltip = t.State is CoordinatorState.Running or CoordinatorState.Calibrating;
        if (forceTooltipUpdate ||
            !throttleTooltip ||
            t.State != _lastTrayTooltipState ||
            nowTooltipTick - _lastTrayTooltipTick >= 1000)
        {
            _lastTrayTooltipState = t.State;
            _lastTrayTooltipTick = nowTooltipTick;

            string tip = t.State switch
            {
                CoordinatorState.Running => string.IsNullOrEmpty(ActiveTargetSuffix)
                    ? $"PGPMint  {runningRateLabel}\n{ProbabilityText} | {LiveEtaText} | {CheckedCountText}"
                    : $"PGPMint  {runningRateLabel}\n{ProbabilityText} | {LiveEtaText} | {CheckedCountText}\n{ActiveFormattedPreview}",
                CoordinatorState.Calibrating => hasRate
                    ? $"PGPMint  {hudMeasuring}\n{rateCompact}"
                    : $"PGPMint  {hudMeasuring}",
                CoordinatorState.Faulted => $"PGPMint - {Loc.Error}",
                _ => (t.TotalChecked > 0 || activeTargetFound)
                    ? $"PGPMint - {Loc.Idle}\n{ProbabilityText} | {CheckedCountText}"
                    : (string.IsNullOrEmpty(ActiveTargetSuffix)
                        ? $"PGPMint - {Loc.Idle}"
                        : $"PGPMint - {Loc.Idle}\n{ActiveFormattedPreview}")
            };

            _trayIcon.UpdateTooltip(tip);
        }

        string hudPrimary = t.State switch
        {
            CoordinatorState.Running => runningRateLabel,
            CoordinatorState.Calibrating => hasRate ? rateCompact : hudMeasuring,
            CoordinatorState.Stopping => Loc.Stopping,
            CoordinatorState.Faulted => Loc.Error,
            _ => Loc.Idle
        };

        string hudProb = t.State == CoordinatorState.Calibrating
            ? $"{Math.Round(rawProgressPercent):F0}%"
            : (activeTargetFound ? Loc.Found : probCompact);

        string hudEta = t.State switch
        {
            CoordinatorState.Calibrating => hudMeasuring,
            CoordinatorState.Running => activeTargetFound ? "\u2014" : LiveEtaText,
            _ => "\u2014"
        };

        _miniHud.UpdateSnapshot(new MiniHudSnapshot(
            t.State,
            hudPrimary,
            hudProb,
            hudEta,
            Math.Clamp(rawProgressPercent / 100.0, 0.0, 1.0)));
    }

    private void SyncGpuToggleStates()
    {
        _suppressGpuToggleCallback = true;
        foreach (GpuItemViewModel vm in Gpus)
        {
            vm.IsEnabled = _coordinator.IsDeviceEnabled(vm.DeviceKey);
        }
        _suppressGpuToggleCallback = false;
    }
}
