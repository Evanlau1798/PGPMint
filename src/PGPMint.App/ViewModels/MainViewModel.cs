using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PGPMint.App.Services;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.App.ViewModels;

public sealed partial class GpuItemViewModel : ObservableObject
{
    public string DeviceKey { get; }
    public string Name { get; }
    public bool IsDiscrete { get; }

    [ObservableProperty] private string _backendName;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _canToggle;

    public GpuItemViewModel(GpuDeviceInfo info, bool isEnabled, bool canToggle)
    {
        DeviceKey = info.DeviceKey;
        Name = info.Name;
        _backendName = info.BackendName;
        IsDiscrete = info.IsDiscrete;
        _isEnabled = isEnabled;
        _canToggle = canToggle;
    }
}

public sealed class MatchedKeyItemViewModel
{
    public MatchedKeyRecord Record { get; }
    public LocalizationService Loc { get; }
    public string FormattedKeyId { get; }
    public string Fingerprint => Record.Fingerprint;
    public string FoundAtLocalText { get; }

    public MatchedKeyItemViewModel(MatchedKeyRecord record, LocalizationService loc)
    {
        Record = record;
        Loc = loc;
        FormattedKeyId = FormattingHelpers.FormatKeyIdSpaced(record.KeyId);
        FoundAtLocalText = record.FoundAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const long NotificationThrottleMs = 5000;

    private readonly MultiGpuCoordinator _coordinator;
    private readonly AppStateStore _stateStore;
    private readonly DpapiVaultStore _vaultStore;
    private readonly PowerKeepAliveService _powerKeepAlive;
    private readonly TrayIconService _trayIcon;
    private readonly MiniHudService _miniHud;
    private readonly DispatcherQueue _dispatcher;

    private readonly Timer _sliderDebounceTimer;
    private double _pendingSliderPosition;
    private int _hasPendingSlider;
    private bool _sliderDirty;
    private bool _suppressSliderCallback;
    private bool _suppressGpuToggleCallback;
    private bool _suppressLanguageCallback;
    private bool _isInitialized;
    private bool _wasCalibrating;
    private long _lastHitNotifyTick = -NotificationThrottleMs;

    public LocalizationService Loc { get; }
    public ObservableCollection<GpuItemViewModel> Gpus { get; } = new();
    public ObservableCollection<MatchedKeyItemViewModel> Matches { get; } = new();
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();

    // Setup Wizard vs Main Dashboard visibility
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    private bool _showSetupWizard;

    public bool ShowDashboard => !ShowSetupWizard;

    // Active committed target (read-only on Main Dashboard)
    [ObservableProperty] private string _activeTargetSuffix = string.Empty;
    [ObservableProperty] private string _activeFormattedPreview = "**** **** **** ****";

    // Draft target edited in Setup Wizard and SettingsWindow
    [ObservableProperty] private string _draftTargetInput = string.Empty;
    [ObservableProperty] private string _draftFormattedPreview = "**** **** **** ****";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinishSetup))]
    private bool _isDraftValid;
    [ObservableProperty] private bool _hasDraftError;
    [ObservableProperty] private string _draftErrorText = string.Empty;
    [ObservableProperty] private bool _canApplyDraftTarget;
    public bool CanFinishSetup => HasHardwareGpu && IsDraftValid;

    // Live telemetry & two-stage throttle state (UI slider 0..1080)
    [ObservableProperty] private string _actualRateNumber = "0.00";
    [ObservableProperty] private string _actualRateUnit = "GH/s";
    [ObservableProperty] private string _targetCapText = "100%";
    [ObservableProperty] private double _sliderValue = RateControl.UiSliderFullSpeed;
    [ObservableProperty] private bool _isSliderEnabled = true;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _probabilityText = "0.000%";
    [ObservableProperty] private string _checkedCountText = "0 H";
    [ObservableProperty] private string _liveEtaText = "\u2014";

    [ObservableProperty] private CoordinatorState _state = CoordinatorState.Idle;
    [ObservableProperty] private string _primaryButtonText = string.Empty;
    [ObservableProperty] private bool _canPrimaryAction;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canEditTarget = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinishSetup))]
    private bool _hasHardwareGpu;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _hasMatches;
    [ObservableProperty] private int _matchesCount;

    [ObservableProperty] private bool _autoPauseOnHit = true;
    [ObservableProperty] private bool _minimizeToTrayOnClose = true;
    [ObservableProperty] private bool _showMiniHudInTray = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _selectedLanguageCode = LocalizationService.AutoLanguageCode;
    [ObservableProperty] private string _transientNotice = string.Empty;

    public MainViewModel(
        MultiGpuCoordinator coordinator,
        AppStateStore stateStore,
        DpapiVaultStore vaultStore,
        LocalizationService localization,
        PowerKeepAliveService powerKeepAlive,
        TrayIconService trayIcon,
        MiniHudService miniHud,
        DispatcherQueue dispatcher)
    {
        _coordinator = coordinator;
        _stateStore = stateStore;
        _vaultStore = vaultStore;
        Loc = localization;
        _powerKeepAlive = powerKeepAlive;
        _trayIcon = trayIcon;
        _miniHud = miniHud;
        _dispatcher = dispatcher;

        _sliderDebounceTimer = new Timer(OnSliderDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
        _coordinator.TelemetryUpdated += OnCoordinatorTelemetryUpdated;
        _coordinator.MatchFound += OnCoordinatorMatchFound;
        Loc.PropertyChanged += (_, _) => RefreshLocalizedDisplays();

        RebuildLanguageOptions();
    }

    public async Task InitializeAsync(bool startedMinimized)
    {
        if (_isInitialized)
        {
            return;
        }

        AppPersistedState persisted = _stateStore.Load();
        _suppressLanguageCallback = true;
        Loc.ApplySetting(persisted.Language);
        SelectedLanguageCode = Loc.SettingValue;
        RebuildLanguageOptions();
        _suppressLanguageCallback = false;

        AutoPauseOnHit = persisted.AutoPauseOnHit;
        MinimizeToTrayOnClose = persisted.MinimizeToTrayOnClose;
        ShowMiniHudInTray = persisted.ShowMiniHudInTray;
        StartWithWindows = StartupRegistryService.IsEnabled();

        _coordinator.Initialize();
        HasHardwareGpu = _coordinator.Devices.Count > 0;

        Gpus.Clear();
        bool multiGpu = _coordinator.Devices.Count > 1;
        foreach (GpuDeviceInfo dev in _coordinator.Devices)
        {
            Gpus.Add(new GpuItemViewModel(dev, _coordinator.IsDeviceEnabled(dev.DeviceKey), multiGpu));
        }

        ReloadMatchesFromVault();

        _suppressSliderCallback = true;
        SliderValue = RateControl.LogicalToUiSlider(persisted.SliderPosition);
        _suppressSliderCallback = false;

        bool hasSavedValidTarget = KeyIdPattern.TryParse(persisted.TargetInput, out KeyIdPattern? savedPattern, out _);
        if (hasSavedValidTarget && savedPattern is not null)
        {
            ActiveTargetSuffix = savedPattern.Suffix;
            ActiveFormattedPreview = savedPattern.FormattedDisplay;
            DraftTargetInput = savedPattern.Suffix;
            _coordinator.SetTargetPattern(savedPattern);
        }
        else
        {
            ActiveTargetSuffix = string.Empty;
            ActiveFormattedPreview = "**** **** **** ****";
            DraftTargetInput = persisted.TargetInput ?? string.Empty;
        }

        ShowSetupWizard = !(persisted.HasCompletedSetup && hasSavedValidTarget);

        _miniHud.SetSetupActive(ShowSetupWizard);
        _isInitialized = true;

        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);
        _miniHud.Configure(persisted.ShowMiniHudInTray, persisted.MiniHudX, persisted.MiniHudY);

        if (!HasHardwareGpu)
        {
            ReportError(Loc.NoGpuError);
            return;
        }

        // Auto-resume if launched silently with --minimized after setup was already completed
        if (!ShowSetupWizard && startedMinimized && persisted.WasRunningOnExit &&
            KeyIdPattern.TryParse(ActiveTargetSuffix, out KeyIdPattern? resumePattern, out _))
        {
            await _coordinator.StartSearchAsync(resumePattern!);
        }
    }

    public void ReportError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        HasError = true;
        ErrorText = message;
    }

    public bool CompleteSetup()
    {
        if (!HasHardwareGpu || !KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out _))
        {
            ValidateDraftTarget();
            return false;
        }

        ActiveTargetSuffix = parsed!.Suffix;
        ActiveFormattedPreview = parsed.FormattedDisplay;
        DraftTargetInput = parsed.Suffix;
        ShowSetupWizard = false;
        _miniHud.SetSetupActive(false);

        _stateStore.Update(s =>
        {
            s.HasCompletedSetup = true;
            s.TargetInput = parsed.Suffix;
        });

        _coordinator.SetTargetPattern(parsed);
        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);
        return true;
    }

    public void SyncDraftFromActive()
    {
        DraftTargetInput = ActiveTargetSuffix;
        ValidateDraftTarget();
    }

    public bool ApplyDraftTargetFromSettings()
    {
        if (!CanEditTarget || !KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out _))
        {
            ValidateDraftTarget();
            return false;
        }

        ActiveTargetSuffix = parsed!.Suffix;
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
        double logical = RateControl.UiSliderToLogical(value);
        UpdateTargetCapDisplay(logical, _coordinator.LatestTelemetry);

        if (_suppressSliderCallback || _coordinator.CurrentState == CoordinatorState.Calibrating)
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
        if (_coordinator.CurrentState == CoordinatorState.Calibrating)
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

                if (RateControl.UiSliderToLogical(SliderValue) <= 0.0)
                {
                    _suppressSliderCallback = true;
                    SliderValue = RateControl.UiSliderFullSpeed;
                    _suppressSliderCallback = false;
                    _coordinator.SetSliderPosition(1.0);
                }

                HasError = false;
                ErrorText = string.Empty;
                await _coordinator.StartSearchAsync(pattern!);
                break;

            case CoordinatorState.Running:
                _coordinator.Pause();
                break;

            case CoordinatorState.Paused:
                _coordinator.Resume();
                _suppressSliderCallback = true;
                SliderValue = RateControl.LogicalToUiSlider(_coordinator.SliderPosition);
                _suppressSliderCallback = false;
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

        await _coordinator.SetDeviceEnabledAsync(deviceKey, isEnabled);
        SyncGpuToggleStates();
        return _coordinator.IsDeviceEnabled(deviceKey);
    }

    public void SetLanguage(string langSetting)
    {
        if (_suppressLanguageCallback || string.IsNullOrWhiteSpace(langSetting))
        {
            return;
        }

        _suppressLanguageCallback = true;
        try
        {
            Loc.ApplySetting(langSetting);
            SelectedLanguageCode = Loc.SettingValue;
            RebuildLanguageOptions();
            if (_isInitialized)
            {
                _stateStore.Update(s => s.Language = Loc.SettingValue);
            }
        }
        finally
        {
            _suppressLanguageCallback = false;
        }
    }

    private void RebuildLanguageOptions()
    {
        IReadOnlyList<LanguageOption> latest = Loc.GetLanguageOptions();
        for (int i = 0; i < latest.Count; i++)
        {
            if (i < LanguageOptions.Count)
            {
                if (string.Equals(LanguageOptions[i].Code, latest[i].Code, StringComparison.Ordinal))
                {
                    // Update DisplayName in place without replacing collection item so ComboBox keeps selection intact.
                    LanguageOptions[i].DisplayName = latest[i].DisplayName;
                }
                else
                {
                    LanguageOptions[i] = latest[i];
                }
            }
            else
            {
                LanguageOptions.Add(latest[i]);
            }
        }

        while (LanguageOptions.Count > latest.Count)
        {
            LanguageOptions.RemoveAt(LanguageOptions.Count - 1);
        }

        OnPropertyChanged(nameof(SelectedLanguageCode));
    }

    partial void OnAutoPauseOnHitChanged(bool value)
    {
        if (_isInitialized)
        {
            _coordinator.SetAutoPauseOnHit(value);
        }
    }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        if (_isInitialized)
        {
            _stateStore.Update(s => s.MinimizeToTrayOnClose = value);
        }
    }

    partial void OnShowMiniHudInTrayChanged(bool value)
    {
        if (_isInitialized)
        {
            _miniHud.SetEnabledByUser(value);
            _stateStore.Update(s => s.ShowMiniHudInTray = value);
            UpdateTrayState(_coordinator.LatestTelemetry);
        }
    }

    public void ToggleMiniHudInTray()
    {
        ShowMiniHudInTray = !ShowMiniHudInTray;
    }

    public void SetMainWindowInTray(bool inTray)
    {
        _miniHud.SetMainWindowInTray(inTray);
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
        if (_isInitialized)
        {
            StartupRegistryService.SetEnabled(value);
            _stateStore.Update(s => s.StartWithWindows = value);
        }
    }

    public OpenPgpExportResult ExportMatchKey(MatchedKeyRecord record, string userName, string? userEmail, string? passphrase)
    {
        byte[] seed = _vaultStore.DecryptPrivateSeed(record);
        byte[] publicKey = Convert.FromHexString(record.PublicKeyHex);
        try
        {
            return OpenPgpArmorExporter.ExportKeyPair(new OpenPgpExportRequest(
                PrivateSeed: seed,
                PublicKey: publicKey,
                CreationTimestamp: record.CreationTimestamp,
                UserName: userName,
                UserEmail: userEmail,
                Passphrase: passphrase));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public void DeleteMatch(string recordId)
    {
        try
        {
            if (_vaultStore.DeleteMatch(recordId))
            {
                ReloadMatchesFromVault();
                _coordinator.NotifyVaultCountChanged(MatchesCount);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    public void ClearAllMatches()
    {
        try
        {
            if (_vaultStore.ClearAllMatches() > 0)
            {
                ReloadMatchesFromVault();
                _coordinator.NotifyVaultCountChanged(0);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    public void FlashNotice(string text)
    {
        TransientNotice = text;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2600);
            _dispatcher.TryEnqueue(() =>
            {
                if (TransientNotice == text)
                {
                    TransientNotice = string.Empty;
                }
            });
        });
    }

    public async Task PrepareForQuitAsync()
    {
        bool wasRunning = _coordinator.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating;
        _stateStore.Update(s => s.WasRunningOnExit = wasRunning);
        _powerKeepAlive.SetActive(false);
        await StopAsync();
    }

    private void OnCoordinatorTelemetryUpdated(object? sender, CoordinatorTelemetry telemetry)
    {
        _dispatcher.TryEnqueue(() => ApplyTelemetry(telemetry));
    }

    private void OnCoordinatorMatchFound(object? sender, MatchedKeyRecord record)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ReloadMatchesFromVault();

            long nowTick = Environment.TickCount64;
            if (AutoPauseOnHit || nowTick - _lastHitNotifyTick >= NotificationThrottleMs)
            {
                _lastHitNotifyTick = nowTick;
                string formattedId = FormattingHelpers.FormatKeyIdSpaced(record.KeyId);
                _trayIcon.ShowNotification(Loc.HitNotificationTitle, formattedId);
            }
        });
    }

    private void ApplyTelemetry(CoordinatorTelemetry t)
    {
        State = t.State;
        bool activeCompute = t.State is CoordinatorState.Running or CoordinatorState.Calibrating;
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        _powerKeepAlive.SetActive(activeCompute);

        // During initial 30s measurement, hold slider visually at 100% (locked).
        // Once measurement finishes, restore the persisted/coordinator logical slider position.
        if (t.State == CoordinatorState.Calibrating)
        {
            _wasCalibrating = true;
            if (Math.Abs(SliderValue - RateControl.UiSliderFullSpeed) > 0.5)
            {
                _suppressSliderCallback = true;
                SliderValue = RateControl.UiSliderFullSpeed;
                _suppressSliderCallback = false;
            }
        }
        else if (_wasCalibrating)
        {
            _wasCalibrating = false;
            double restoredUi = RateControl.LogicalToUiSlider(_coordinator.SliderPosition);
            _suppressSliderCallback = true;
            SliderValue = restoredUi;
            _suppressSliderCallback = false;
        }

        ActualRateNumber = FormattingHelpers.FormatHashRateValue(t.TotalActualHps);
        ActualRateUnit = FormattingHelpers.FormatHashRateUnit(t.TotalActualHps);

        double logicalSlider = t.State == CoordinatorState.Calibrating
            ? 1.0
            : RateControl.UiSliderToLogical(SliderValue);
        UpdateTargetCapDisplay(logicalSlider, t);

        if (t.State == CoordinatorState.Calibrating)
        {
            double calTotal = _coordinator.FullCalibrationDurationSeconds;
            ProgressPercent = Math.Clamp((1.0 - (t.CalibrationRemainingSeconds / calTotal)) * 100.0, 0.0, 100.0);
        }
        else
        {
            ProgressPercent = t.CumulativeProbability * 100.0;
        }

        ProbabilityText = FormattingHelpers.FormatProbability(t.CumulativeProbability);
        CheckedCountText = FormattingHelpers.FormatCheckedCount(t.TotalChecked);
        LiveEtaText = FormattingHelpers.FormatEta(t.EstimatedMeanWait, Loc.IsChinese);

        if (!string.IsNullOrWhiteSpace(t.ErrorMessage))
        {
            HasError = true;
            ErrorText = t.ErrorMessage;
        }
        else if (HasHardwareGpu && t.State != CoordinatorState.Faulted && activeCompute)
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
        UpdateTrayState(t);
    }

    private void UpdateTargetCapDisplay(double logicalSlider, CoordinatorTelemetry t)
    {
        if (t.State == CoordinatorState.Calibrating)
        {
            TargetCapText = "100%";
            return;
        }

        if (t.State == CoordinatorState.Stopping)
        {
            TargetCapText = Loc.Stopping;
            return;
        }

        if (logicalSlider <= 0.0)
        {
            TargetCapText = "0%";
            return;
        }

        bool hasMeasuredBaseline = t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;

        if (logicalSlider >= 1.0)
        {
            TargetCapText = hasMeasuredBaseline
                ? $"{Loc.FullSpeed} · {FormattingHelpers.FormatHashRateCompact(t.TotalBaselineHps)}"
                : "100%";
            return;
        }

        int pct = (int)Math.Clamp(Math.Round(logicalSlider * 100.0, MidpointRounding.AwayFromZero), 1.0, 99.0);
        if (hasMeasuredBaseline)
        {
            RateDirective dir = RateControl.FromSlider(logicalSlider, t.TotalBaselineHps);
            TargetCapText = $"{pct}% · {FormattingHelpers.FormatHashRateCompact(dir.TargetHashesPerSecond)}";
        }
        else
        {
            TargetCapText = $"{pct}%";
        }
    }

    private void UpdateActionPermissions(CoordinatorTelemetry t)
    {
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        CanEditTarget = t.State is CoordinatorState.Idle or CoordinatorState.Faulted;
        CanStop = t.State is CoordinatorState.Running or CoordinatorState.Paused or CoordinatorState.Calibrating;
        IsSliderEnabled = HasHardwareGpu && !busyCal;

        bool hasValidActiveTarget = KeyIdPattern.TryParse(ActiveTargetSuffix, out _, out _);

        switch (t.State)
        {
            case CoordinatorState.Running:
                PrimaryButtonText = Loc.Pause;
                CanPrimaryAction = true;
                break;
            case CoordinatorState.Paused:
                PrimaryButtonText = Loc.Resume;
                CanPrimaryAction = true;
                break;
            case CoordinatorState.Calibrating:
                PrimaryButtonText = Loc.FormatMeasuringSpeed((int)Math.Ceiling(t.CalibrationRemainingSeconds));
                CanPrimaryAction = false;
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

    private void UpdateTrayState(CoordinatorTelemetry t)
    {
        string toggleLabel = t.State switch
        {
            CoordinatorState.Running => Loc.Pause,
            CoordinatorState.Paused => Loc.Resume,
            _ => Loc.Start
        };

        string rateCompact = FormattingHelpers.FormatHashRateCompact(t.TotalActualHps);
        bool hasRate = t.TotalActualHps > 0.0;
        int remainSec = (int)Math.Ceiling(t.CalibrationRemainingSeconds);
        string hudMeasuring = Loc.FormatHudMeasuring(remainSec);

        string summaryLine1 = t.State switch
        {
            CoordinatorState.Running => $"{rateCompact}  |  {ProbabilityText}  |  {LiveEtaText}",
            CoordinatorState.Calibrating => hasRate
                ? $"{hudMeasuring}  |  {rateCompact}"
                : hudMeasuring,
            CoordinatorState.Paused => t.TotalChecked > 0
                ? $"{Loc.Paused}  |  {ProbabilityText}"
                : Loc.Paused,
            CoordinatorState.Stopping => Loc.Stopping,
            CoordinatorState.Faulted => Loc.Error,
            _ => Loc.Idle
        };

        string summaryLine2 = string.Empty;
        if (!string.IsNullOrEmpty(ActiveTargetSuffix))
        {
            summaryLine2 = t.TotalChecked > 0
                ? $"{ActiveFormattedPreview}  |  {CheckedCountText}"
                : ActiveFormattedPreview;
        }

        bool canToggle = !ShowSetupWizard && CanPrimaryAction;
        _trayIcon.UpdateMenuState(
            Loc.Open,
            toggleLabel,
            canToggle,
            Loc.TrayMiniHud,
            ShowMiniHudInTray,
            Loc.Quit,
            summaryLine1,
            summaryLine2);

        string tip = t.State switch
        {
            CoordinatorState.Running => string.IsNullOrEmpty(ActiveTargetSuffix)
                ? $"PGPMint  {rateCompact}\n{ProbabilityText} | {LiveEtaText} | {CheckedCountText}"
                : $"PGPMint  {rateCompact}\n{ProbabilityText} | {LiveEtaText} | {CheckedCountText}\n{ActiveFormattedPreview}",
            CoordinatorState.Calibrating => hasRate
                ? $"PGPMint  {hudMeasuring}\n{rateCompact}"
                : $"PGPMint  {hudMeasuring}",
            CoordinatorState.Paused => t.TotalChecked > 0
                ? $"PGPMint  {Loc.Paused}\n{ProbabilityText} | {CheckedCountText}"
                : $"PGPMint - {Loc.Paused}",
            CoordinatorState.Faulted => $"PGPMint - {Loc.Error}",
            _ => string.IsNullOrEmpty(ActiveTargetSuffix)
                ? $"PGPMint - {Loc.Idle}"
                : $"PGPMint - {Loc.Idle}\n{ActiveFormattedPreview}"
        };

        _trayIcon.UpdateTooltip(tip);

        string hudPrimary = t.State switch
        {
            CoordinatorState.Running => rateCompact,
            CoordinatorState.Calibrating => hasRate ? rateCompact : hudMeasuring,
            CoordinatorState.Paused => Loc.Paused,
            CoordinatorState.Stopping => Loc.Stopping,
            CoordinatorState.Faulted => Loc.Error,
            _ => Loc.Idle
        };

        string hudProb = t.State == CoordinatorState.Calibrating
            ? $"{Math.Round(ProgressPercent):F0}%"
            : ProbabilityText;

        string hudEta = t.State switch
        {
            CoordinatorState.Calibrating => hudMeasuring,
            CoordinatorState.Running => LiveEtaText,
            _ => "\u2014"
        };

        _miniHud.UpdateSnapshot(new MiniHudSnapshot(
            t.State,
            hudPrimary,
            hudProb,
            hudEta,
            Math.Clamp(ProgressPercent / 100.0, 0.0, 1.0)));
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

    private void ReloadMatchesFromVault()
    {
        try
        {
            IReadOnlyList<MatchedKeyRecord> records = _vaultStore.LoadMatches();
            Matches.Clear();
            foreach (MatchedKeyRecord rec in records)
            {
                Matches.Add(new MatchedKeyItemViewModel(rec, Loc));
            }

            MatchesCount = Matches.Count;
            HasMatches = MatchesCount > 0;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private void RefreshLocalizedDisplays()
    {
        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);
    }

    public void Dispose()
    {
        _coordinator.TelemetryUpdated -= OnCoordinatorTelemetryUpdated;
        _coordinator.MatchFound -= OnCoordinatorMatchFound;
        _sliderDebounceTimer.Dispose();
    }
}
