using PGPMint.App.Services;
using PGPMint.Core.Search;

namespace PGPMint.App.ViewModels;

public sealed partial class MainViewModel
{
    public void SyncDraftFromActive()
    {
        DraftTargetInput = ActiveTargetSuffix;
        ValidateDraftTarget();
    }

    public bool ApplyDraftTargetFromSettings()
    {
        if (!CanEditTarget || !KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out _) || parsed is null)
        {
            ValidateDraftTarget();
            return false;
        }

        _activePatternObj = parsed;
        ActiveTargetSuffix = parsed.Suffix;
        ActiveFormattedPreview = parsed.FormattedDisplay;
        DraftTargetInput = parsed.Suffix;

        _stateStore.Update(s => s.TargetInput = parsed.Suffix);
        _coordinator.SetTargetPattern(parsed);
        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);
        FlashNotice(Loc.Applied);
        return true;
    }

    partial void OnDraftTargetInputChanged(string value)
    {
        ValidateDraftTarget();
    }

    private void ValidateDraftTarget()
    {
        bool ok = KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out KeyIdValidationError error);
        if (ok && parsed is not null)
        {
            IsDraftValid = true;
            HasDraftError = false;
            DraftErrorText = string.Empty;
            DraftFormattedPreview = parsed.FormattedDisplay;
        }
        else
        {
            IsDraftValid = false;
            HasDraftError = error is KeyIdValidationError.InvalidCharacters or KeyIdValidationError.TooLong;
            DraftErrorText = HasDraftError ? Loc.GetKeyIdValidationMessage(error) : string.Empty;
            DraftFormattedPreview = "**** **** **** ****";
        }

        CanApplyDraftTarget = CanEditTarget && IsDraftValid &&
            !string.Equals(parsed?.Suffix, ActiveTargetSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public void SelectPowerTier(PowerTier tier)
    {
        if (_coordinator.CurrentState is CoordinatorState.Calibrating or CoordinatorState.Stopping ||
            !_coordinator.LatestTelemetry.AllEnabledFullyCalibrated)
        {
            return;
        }

        _coordinator.SetPowerTier(tier);
        SelectedTier = _coordinator.SelectedTier;
        DisplayTier = SelectedTier;
    }

    public async Task RecalibrateAsync()
    {
        if (!CanRecalibrate)
        {
            return;
        }

        if (!_coordinator.InvalidateCalibrationsForRecalibration())
        {
            return;
        }

        _fgFullSpeedEmaHps = 0.0;
        _trayFullSpeedEmaHps = 0.0;
        _persistedTrayFullSpeedHps = 0.0;

        FlashNotice(Loc.Recalibrating);
        await PrimaryActionAsync();
    }

    public async Task PrimaryActionAsync()
    {
        if (!HasHardwareGpu || ShowSetupWizard)
        {
            return;
        }

        switch (_coordinator.CurrentState)
        {
            case CoordinatorState.Idle:
            case CoordinatorState.Faulted:
                if (!KeyIdPattern.TryParse(ActiveTargetSuffix, out KeyIdPattern? pattern, out _))
                {
                    return;
                }

                _activePatternObj = pattern;
                HasError = false;
                ErrorText = string.Empty;
                await _coordinator.StartSearchAsync(pattern!);
                break;

            case CoordinatorState.Calibrating:
            case CoordinatorState.Running:
            case CoordinatorState.Paused:
                await _coordinator.StopAsync();
                break;
        }
    }

    public async Task StopAsync()
    {
        await _coordinator.StopAsync();
    }

    public async Task<bool> ToggleGpuAsync(string deviceKey, bool isEnabled)
    {
        if (_suppressGpuToggleCallback)
        {
            return _coordinator.IsDeviceEnabled(deviceKey);
        }

        _fgFullSpeedEmaHps = 0.0;
        _trayFullSpeedEmaHps = 0.0;
        _persistedTrayFullSpeedHps = 0.0;

        await _coordinator.SetDeviceEnabledAsync(deviceKey, isEnabled);
        SyncGpuToggleStates();
        return _coordinator.IsDeviceEnabled(deviceKey);
    }

    partial void OnAutoPauseOnHitChanged(bool value)
    {
        if (IsInitialized)
        {
            _coordinator.SetAutoPauseOnHit(value);
        }
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (IsInitialized)
        {
            _stateStore.Update(s => s.MinimizeToTrayOnClose = value);
            UpdateShowTraySpeedupHint(_coordinator.LatestTelemetry);
        }
    }

    partial void OnShowMiniHudInTrayChanged(bool value)
    {
        if (IsInitialized)
        {
            _miniHud.SetEnabledByUser(value);
            _stateStore.Update(s => s.ShowMiniHudInTray = value);
            CoordinatorTelemetry latest = _coordinator.LatestTelemetry;
            UpdateTrayState(
                latest,
                ProgressPercent,
                latest.State != CoordinatorState.Calibrating && HasMatchForActivePattern());
        }
    }

    public void ToggleMiniHudInTray()
    {
        ShowMiniHudInTray = !ShowMiniHudInTray;
    }

    public void SetMainWindowInTray(bool inTray)
    {
        _mainWindowInTray = inTray;
        _miniHud.SetMainWindowInTray(inTray);
        if (!inTray && IsInitialized)
        {
            ApplyTelemetry(_coordinator.LatestTelemetry, forceTrayHintTextUpdate: true);
        }
        else
        {
            SyncSmoothProgressTimer();
        }
    }

    public void SaveMiniHudPosition(int x, int y)
    {
        _stateStore.Update(s =>
        {
            s.MiniHudX = x;
            s.MiniHudY = y;
        });
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (IsInitialized)
        {
            StartupRegistryService.SetEnabled(value);
            _stateStore.Update(s => s.StartWithWindows = value);
        }
    }

    public void SetMaxGpuTempLimit(int limitValue)
    {
        int? normalized = ThermalGuardController.NormalizeLimit(limitValue);
        int targetValue = normalized ?? 0;
        if (SelectedMaxGpuTempLimitValue == targetValue && _coordinator.MaxGpuTemperatureCelsius == normalized)
        {
            return;
        }

        SelectedMaxGpuTempLimitValue = targetValue;
        _coordinator.SetMaxGpuTemperatureCelsius(normalized);
    }
}
