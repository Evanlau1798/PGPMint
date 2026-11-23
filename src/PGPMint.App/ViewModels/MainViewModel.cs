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
    [ObservableProperty] private string _rateText = "\u2014";
    [ObservableProperty] private bool _isFullCalibrated;
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
    public string FormattedKeyId { get; }
    public string Fingerprint => Record.Fingerprint;
    public string FoundAtLocalText { get; }

    public MatchedKeyItemViewModel(MatchedKeyRecord record)
    {
        Record = record;
        FormattedKeyId = FormattingHelpers.FormatKeyIdSpaced(record.KeyId);
        FoundAtLocalText = record.FoundAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly MultiGpuCoordinator _coordinator;
    private readonly AppStateStore _stateStore;
    private readonly DpapiVaultStore _vaultStore;
    private readonly PowerKeepAliveService _powerKeepAlive;
    private readonly TrayIconService _trayIcon;
    private readonly DispatcherQueue _dispatcher;

    private readonly Timer _sliderDebounceTimer;
    private double _pendingSliderPosition;
    private int _hasPendingSlider;
    private bool _sliderDirty;
    private bool _suppressSliderCallback;
    private bool _suppressGpuToggleCallback;
    private bool _isInitialized;

    public LocalizationService Loc { get; }
    public ObservableCollection<GpuItemViewModel> Gpus { get; } = new();
    public ObservableCollection<MatchedKeyItemViewModel> Matches { get; } = new();

    // Setup Wizard vs Main Dashboard visibility
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDashboard))]
    [NotifyPropertyChangedFor(nameof(IsSetupStep1))]
    [NotifyPropertyChangedFor(nameof(IsSetupStep2))]
    private bool _showSetupWizard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupStep1))]
    [NotifyPropertyChangedFor(nameof(IsSetupStep2))]
    private int _setupStep = 1;

    public bool ShowDashboard => !ShowSetupWizard;
    public bool IsSetupStep1 => ShowSetupWizard && SetupStep == 1;
    public bool IsSetupStep2 => ShowSetupWizard && SetupStep == 2;

    // Active committed target (read-only on Main Dashboard)
    [ObservableProperty] private string _activeTargetSuffix = string.Empty;
    [ObservableProperty] private string _activeFormattedPreview = "**** **** **** ****";

    // Draft target edited in Setup Wizard Step 2 and SettingsWindow
    [ObservableProperty] private string _draftTargetInput = string.Empty;
    [ObservableProperty] private string _draftFormattedPreview = "**** **** **** ****";
    [ObservableProperty] private bool _isDraftValid;
    [ObservableProperty] private bool _hasDraftError;
    [ObservableProperty] private string _draftErrorText = string.Empty;
    [ObservableProperty] private string _draftEtaText = "\u2014";
    [ObservableProperty] private int _draftEtaSeverityLevel; // 0 = normal, 1 = >7d, 2 = >1y
    [ObservableProperty] private bool _canApplyDraftTarget;
    [ObservableProperty] private string _gpuSummaryText = "\u2014";

    // Live telemetry & throttle state
    [ObservableProperty] private string _actualRateNumber = "0.00";
    [ObservableProperty] private string _actualRateUnit = "GH/s";
    [ObservableProperty] private string _targetCapText = "\u2014";
    [ObservableProperty] private double _sliderValue = 700.0; // 0..1000
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
    [ObservableProperty] private bool _canRecalibrate = true;
    [ObservableProperty] private bool _canAdvanceFromStep1;
    [ObservableProperty] private bool _hasHardwareGpu;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _hasMatches;
    [ObservableProperty] private int _matchesCount;

    [ObservableProperty] private bool _autoPauseOnHit = true;
    [ObservableProperty] private bool _minimizeToTrayOnClose = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _selectedLanguage = "auto";
    [ObservableProperty] private string _transientNotice = string.Empty;

    public MainViewModel(
        MultiGpuCoordinator coordinator,
        AppStateStore stateStore,
        DpapiVaultStore vaultStore,
        LocalizationService localization,
        PowerKeepAliveService powerKeepAlive,
        TrayIconService trayIcon,
        DispatcherQueue dispatcher)
    {
        _coordinator = coordinator;
        _stateStore = stateStore;
        _vaultStore = vaultStore;
        Loc = localization;
        _powerKeepAlive = powerKeepAlive;
        _trayIcon = trayIcon;
        _dispatcher = dispatcher;

        _sliderDebounceTimer = new Timer(OnSliderDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
        _coordinator.TelemetryUpdated += OnCoordinatorTelemetryUpdated;
        _coordinator.MatchFound += OnCoordinatorMatchFound;
        Loc.PropertyChanged += (_, _) => RefreshLocalizedDisplays();
    }

    public async Task InitializeAsync(bool startedMinimized)
    {
        if (_isInitialized)
        {
            return;
        }

        AppPersistedState persisted = _stateStore.Load();
        Loc.ApplySetting(persisted.Language);
        SelectedLanguage = persisted.Language;
        AutoPauseOnHit = persisted.AutoPauseOnHit;
        MinimizeToTrayOnClose = persisted.MinimizeToTrayOnClose;
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
        SliderValue = Math.Round(Math.Clamp(persisted.SliderPosition, 0.0, 1.0) * 1000.0);
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
        SetupStep = 1;

        _isInitialized = true;

        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);

        if (!HasHardwareGpu)
        {
            ReportError(Loc.NoGpuError);
            return;
        }

        // Stage 1: automatic 5-second quick probe if enabled GPUs lack calibration
        if (_coordinator.NeedsQuickProbe())
        {
            try
            {
                await _coordinator.RunQuickProbeAsync();
            }
            catch (Exception ex)
            {
                ReportError(ex.Message);
            }
        }

        // In Setup Wizard Step 1, auto-advance to Step 2 on single-GPU setups after probe finishes
        if (ShowSetupWizard && SetupStep == 1 && HasHardwareGpu && Gpus.Count == 1 && _coordinator.CurrentState == CoordinatorState.Idle)
        {
            SetupStep = 2;
        }

        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry);

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

    public void GoToSetupStep2()
    {
        if (ShowSetupWizard && CanAdvanceFromStep1)
        {
            SetupStep = 2;
            ValidateDraftTarget();
        }
    }

    public void GoToSetupStep1()
    {
        if (ShowSetupWizard)
        {
            SetupStep = 1;
        }
    }

    public bool CompleteSetup()
    {
        if (!KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out _))
        {
            ValidateDraftTarget();
            return false;
        }

        ActiveTargetSuffix = parsed!.Suffix;
        ActiveFormattedPreview = parsed.FormattedDisplay;
        DraftTargetInput = parsed.Suffix;
        ShowSetupWizard = false;

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
        UpdateActionPermissions();
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

            double refRate = ComputeReferenceRateForEta();
            TimeSpan? eta = parsed.EstimateMeanWait(refRate);
            DraftEtaText = FormattingHelpers.FormatEta(eta, Loc.IsChinese);
            DraftEtaSeverityLevel = eta is null
                ? 0
                : eta.Value.TotalDays > 365.25
                    ? 2
                    : eta.Value.TotalDays > 7.0 ? 1 : 0;
        }
        else
        {
            IsDraftValid = false;
            HasDraftError = error is KeyIdValidationError.InvalidCharacters or KeyIdValidationError.TooLong;
            DraftErrorText = HasDraftError ? Loc.GetKeyIdValidationMessage(error) : string.Empty;
            DraftFormattedPreview = "**** **** **** ****";
            DraftEtaText = "\u2014";
            DraftEtaSeverityLevel = 0;
        }

        CanApplyDraftTarget = CanEditTarget && IsDraftValid &&
            !string.Equals(parsed?.Suffix, ActiveTargetSuffix, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSliderValueChanged(double value)
    {
        double p = Math.Clamp(value / 1000.0, 0.0, 1.0);
        UpdateTargetCapDisplay(p, _coordinator.LatestTelemetry);

        if (_suppressSliderCallback)
        {
            return;
        }

        _sliderDirty = true;
        Volatile.Write(ref _pendingSliderPosition, p);
        if (Interlocked.Exchange(ref _hasPendingSlider, 1) == 0)
        {
            _sliderDebounceTimer.Change(100, Timeout.Infinite);
        }

        ValidateDraftTarget();
    }

    public void CommitSliderRelease()
    {
        if (!_sliderDirty)
        {
            return;
        }

        _sliderDirty = false;
        double p = Math.Clamp(SliderValue / 1000.0, 0.0, 1.0);
        Interlocked.Exchange(ref _hasPendingSlider, 0);
        _sliderDebounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _coordinator.SetSliderPosition(p);
        ValidateDraftTarget();
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

                if (SliderValue <= 0.0)
                {
                    _suppressSliderCallback = true;
                    SliderValue = 700.0;
                    _suppressSliderCallback = false;
                    _coordinator.SetSliderPosition(0.7);
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
                SliderValue = Math.Round(Math.Clamp(_coordinator.SliderPosition, 0.0, 1.0) * 1000.0);
                _suppressSliderCallback = false;
                break;
        }
    }

    public async Task StopAsync()
    {
        await _coordinator.StopAsync();
    }

    public async Task Recalibrate30sAsync()
    {
        if (!HasHardwareGpu || _coordinator.CurrentState is CoordinatorState.QuickProbing or CoordinatorState.Calibrating or CoordinatorState.Stopping)
        {
            return;
        }

        KeyIdPattern.TryParse(ActiveTargetSuffix, out KeyIdPattern? currentPattern, out _);
        await _coordinator.RunManualRecalibrationAsync(currentPattern);
    }

    public async Task<bool> ToggleGpuAsync(string deviceKey, bool isEnabled)
    {
        if (_suppressGpuToggleCallback)
        {
            return _coordinator.IsDeviceEnabled(deviceKey);
        }

        await _coordinator.SetDeviceEnabledAsync(deviceKey, isEnabled);
        SyncGpuToggleStates();
        ValidateDraftTarget();
        return _coordinator.IsDeviceEnabled(deviceKey);
    }

    public void SetLanguage(string langSetting)
    {
        Loc.ApplySetting(langSetting);
        SelectedLanguage = langSetting;
        _stateStore.Update(s => s.Language = langSetting);
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

    private double ComputeReferenceRateForEta()
    {
        CoordinatorTelemetry t = _coordinator.LatestTelemetry;
        if (t.State is CoordinatorState.Running or CoordinatorState.Calibrating && t.TotalActualHps > 0.0)
        {
            return t.TotalActualHps;
        }

        if (t.TotalPeakHps <= 0.0)
        {
            return 0.0;
        }

        if (ShowSetupWizard)
        {
            return t.TotalPeakHps;
        }

        double p = Math.Clamp(SliderValue / 1000.0, 0.0, 1.0);
        return p >= 1.0 || p <= 0.0 ? t.TotalPeakHps : t.TotalPeakHps * p;
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
            string formattedId = FormattingHelpers.FormatKeyIdSpaced(record.KeyId);
            _trayIcon.ShowNotification(Loc.HitNotificationTitle, formattedId);
        });
    }

    private void ApplyTelemetry(CoordinatorTelemetry t)
    {
        State = t.State;
        bool activeCompute = t.State is CoordinatorState.Running or CoordinatorState.Calibrating or CoordinatorState.QuickProbing;
        bool busyCal = t.State is CoordinatorState.QuickProbing or CoordinatorState.Calibrating or CoordinatorState.Stopping;
        _powerKeepAlive.SetActive(activeCompute);

        ActualRateNumber = FormattingHelpers.FormatHashRateValue(t.TotalActualHps);
        ActualRateUnit = FormattingHelpers.FormatHashRateUnit(t.TotalActualHps);

        double sliderP = Math.Clamp(SliderValue / 1000.0, 0.0, 1.0);
        UpdateTargetCapDisplay(sliderP, t);

        if (t.State is CoordinatorState.QuickProbing or CoordinatorState.Calibrating)
        {
            double totalSec = t.State == CoordinatorState.QuickProbing ? 5.0 : 30.0;
            ProgressPercent = Math.Clamp((1.0 - (t.CalibrationRemainingSeconds / totalSec)) * 100.0, 0.0, 100.0);
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
        var enabledNames = new List<string>();
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
            vm.IsFullCalibrated = devSnap.IsFullCalibrated;

            if (devSnap.IsEnabled)
            {
                enabledNames.Add(devSnap.Name);
            }

            if (!devSnap.IsEnabled && activeCompute)
            {
                vm.RateText = "\u2014";
            }
            else
            {
                double shownRate = activeCompute && devSnap.IsEnabled ? devSnap.ActualHps : devSnap.PeakHps;
                vm.RateText = shownRate > 0.0 ? FormattingHelpers.FormatHashRateCompact(shownRate) : "\u2014";
            }
        }

        if (t.TotalPeakHps > 0.0)
        {
            string gpuLabel = enabledNames.Count == 1 ? enabledNames[0] : $"{enabledNames.Count} GPUs";
            GpuSummaryText = $"{gpuLabel}  |  {FormattingHelpers.FormatHashRateCompact(t.TotalPeakHps)}";
        }
        else
        {
            GpuSummaryText = enabledNames.Count > 0 ? string.Join(", ", enabledNames) : "\u2014";
        }

        UpdateActionPermissions();
        ValidateDraftTarget();
        UpdateTrayState(t);
    }

    private void UpdateTargetCapDisplay(double sliderP, CoordinatorTelemetry t)
    {
        if (t.State == CoordinatorState.QuickProbing)
        {
            TargetCapText = $"{Loc.Probing} {Math.Ceiling(t.CalibrationRemainingSeconds):F0}s";
            return;
        }

        if (t.State == CoordinatorState.Calibrating)
        {
            TargetCapText = $"{Loc.Calibrating} {Math.Ceiling(t.CalibrationRemainingSeconds):F0}s";
            return;
        }

        if (t.State == CoordinatorState.Stopping)
        {
            TargetCapText = Loc.Stopping;
            return;
        }

        if (t.State == CoordinatorState.Paused || sliderP <= 0.0)
        {
            TargetCapText = Loc.Paused;
            return;
        }

        if (sliderP >= 1.0)
        {
            TargetCapText = Loc.FullSpeed;
            return;
        }

        if (t.TotalPeakHps > 0.0)
        {
            RateDirective dir = RateControl.FromSlider(sliderP, t.TotalPeakHps);
            TargetCapText = FormattingHelpers.FormatHashRateCompact(dir.TargetHashesPerSecond);
        }
        else
        {
            TargetCapText = $"{Math.Round(sliderP * 100.0):F0}%";
        }
    }

    private void UpdateActionPermissions()
    {
        bool busyCal = State is CoordinatorState.QuickProbing or CoordinatorState.Calibrating or CoordinatorState.Stopping;
        CanEditTarget = State is CoordinatorState.Idle or CoordinatorState.Faulted;
        CanStop = State is CoordinatorState.Running or CoordinatorState.Paused or CoordinatorState.Calibrating or CoordinatorState.QuickProbing;
        CanRecalibrate = HasHardwareGpu && !busyCal;
        CanAdvanceFromStep1 = HasHardwareGpu && !busyCal;
        IsSliderEnabled = HasHardwareGpu && !busyCal;

        bool hasValidActiveTarget = KeyIdPattern.TryParse(ActiveTargetSuffix, out _, out _);

        switch (State)
        {
            case CoordinatorState.Running:
                PrimaryButtonText = Loc.Pause;
                CanPrimaryAction = true;
                break;
            case CoordinatorState.Paused:
                PrimaryButtonText = Loc.Resume;
                CanPrimaryAction = true;
                break;
            case CoordinatorState.QuickProbing:
                PrimaryButtonText = Loc.Probing;
                CanPrimaryAction = false;
                break;
            case CoordinatorState.Calibrating:
                PrimaryButtonText = Loc.Calibrating;
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

        _trayIcon.UpdateMenuLabels(Loc.Open, toggleLabel, Loc.Quit);

        string tip = t.State switch
        {
            CoordinatorState.Running => $"PGPMint - {FormattingHelpers.FormatHashRateCompact(t.TotalActualHps)}",
            CoordinatorState.Calibrating => $"PGPMint - {Loc.Calibrating} {Math.Ceiling(t.CalibrationRemainingSeconds):F0}s",
            CoordinatorState.QuickProbing => $"PGPMint - {Loc.Probing} {Math.Ceiling(t.CalibrationRemainingSeconds):F0}s",
            CoordinatorState.Paused => $"PGPMint - {Loc.Paused}",
            _ => $"PGPMint - {Loc.Idle}"
        };

        _trayIcon.UpdateTooltip(tip);
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
                Matches.Add(new MatchedKeyItemViewModel(rec));
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
