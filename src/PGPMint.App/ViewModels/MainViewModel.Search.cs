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

    partial void OnSliderValueChanged(double value)
    {
        double snappedUi = RateControl.SnapUiSliderOnRelease(value);
        if (!_suppressSliderCallback && Math.Abs(snappedUi - value) > 0.5)
        {
            _suppressSliderCallback = true;
            SliderValue = snappedUi;
            _suppressSliderCallback = false;
            value = snappedUi;
        }

        double logical = RateControl.UiSliderToLogical(value);
        UpdateTargetCapDisplay(logical, _coordinator.LatestTelemetry);

        if (_suppressSliderCallback ||
            _coordinator.CurrentState == CoordinatorState.Calibrating ||
            !_coordinator.LatestTelemetry.AllEnabledFullyCalibrated)
        {
            return;
        }

        _sliderDirty = true;
        Volatile.Write(ref _pendingSliderPosition, logical);
        if (Interlocked.Exchange(ref _hasPendingSlider, 1) == 0)
        {
            _sliderDebounceTimer.Change(100, Timeout.Infinite);
        }
    }

    public void CommitSliderRelease()
    {
        if (_coordinator.CurrentState == CoordinatorState.Calibrating ||
            !_coordinator.LatestTelemetry.AllEnabledFullyCalibrated)
        {
            return;
        }

        double snappedUi = RateControl.SnapUiSliderOnRelease(SliderValue);
        if (Math.Abs(snappedUi - SliderValue) > 0.5)
        {
            _suppressSliderCallback = true;
            SliderValue = snappedUi;
            _suppressSliderCallback = false;
        }

        if (!_sliderDirty)
        {
            return;
        }

        _sliderDirty = false;
        double logical = RateControl.UiSliderToLogical(snappedUi);
        Interlocked.Exchange(ref _hasPendingSlider, 0);
        _sliderDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _coordinator.SetSliderPosition(logical);
    }

    private void OnSliderDebounceTick(object? state)
    {
        if (Interlocked.Exchange(ref _hasPendingSlider, 0) == 1)
        {
            double p = Volatile.Read(ref _pendingSliderPosition);
            _coordinator.SetSliderPosition(p);
        }
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
        SyncSmoothProgressTimer();
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
}
