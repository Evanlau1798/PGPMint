using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PGPMint.App.Services;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.App.ViewModels;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed partial class MaxGpuTempOption : ObservableObject
{
    public int LimitValue { get; }

    [ObservableProperty]
    private string _displayName;

    public MaxGpuTempOption(int limitValue, string displayName)
    {
        LimitValue = limitValue;
        _displayName = displayName;
    }
}

public sealed partial class GpuItemViewModel : ObservableObject
{
    public string DeviceKey { get; }
    public string Name { get; }
    public bool IsDiscrete { get; }

    [ObservableProperty] private string _backendName;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _canToggle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSummaryVisibility))]
    private string _statusSummary = string.Empty;

    public Visibility StatusSummaryVisibility =>
        string.IsNullOrEmpty(StatusSummary) ? Visibility.Collapsed : Visibility.Visible;

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

    private readonly DispatcherQueueTimer _smoothProgressTimer;
    private bool _suppressGpuToggleCallback;
    private bool _suppressLanguageCallback;
    private bool _mainWindowInTray;
    private long _lastHitNotifyTick = -NotificationThrottleMs;

    private double _persistedTrayFullSpeedHps;
    private double _trayFullSpeedEmaHps;
    private double _fgFullSpeedEmaHps;
    private long _lastTraySpeedPersistTick;
    private long _lastTrayHintUiUpdateTick = -1000;
    private bool _showingHighIdlePowerHint;
    private long _lastTrayTooltipTick = -1000;
    private CoordinatorState _lastTrayTooltipState = CoordinatorState.Idle;

    [ObservableProperty] private bool _isInitialized;

    // Telemetry snapshot anchors for GPU-friendly smooth progress bar interpolation
    private KeyIdPattern? _activePatternObj;
    private long _snapTimestamp;
    private ulong _snapTotalChecked;
    private double _snapActualHps;
    private double _smoothProgressCurrent;

    public LocalizationService Loc { get; }
    public ObservableCollection<GpuItemViewModel> Gpus { get; } = new();
    public ObservableCollection<MatchedKeyItemViewModel> Matches { get; } = new();
    public ObservableCollection<LanguageOption> LanguageOptions { get; } = new();
    public ObservableCollection<MaxGpuTempOption> MaxGpuTempOptions { get; } = new();

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
    private bool _wasVaultFull;
    [ObservableProperty] private string _draftErrorText = string.Empty;
    [ObservableProperty] private bool _canApplyDraftTarget;
    [ObservableProperty] private string _draftMinCreationDate = string.Empty;
    [ObservableProperty] private string _draftMaxCreationDate = string.Empty;
    [ObservableProperty] private string _timestampRangeErrorText = string.Empty;
    [ObservableProperty] private bool _hasTimestampRangeError;
    public bool CanFinishSetup => HasHardwareGpu && IsDraftValid;

    // Live telemetry & 4-tier efficiency control (Eco / Balanced / Efficiency / Max)
    [ObservableProperty] private string _actualRateNumber = "0.00";
    [ObservableProperty] private string _actualRateUnit = "GH/s";
    [ObservableProperty] private string _efficiencyBadgeText = string.Empty;
    [ObservableProperty] private string? _efficiencyBadgeTooltip;
    [ObservableProperty] private PowerTier _selectedTier = PowerTier.Max;
    [ObservableProperty] private PowerTier _displayTier = PowerTier.Max;
    [ObservableProperty] private bool _isTierSelectorEnabled;
    [ObservableProperty] private bool _canRecalibrate;
    [ObservableProperty] private string _traySpeedupHintText = string.Empty;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _probabilityText = "0.00%";
    [ObservableProperty] private string _singleHashProbabilityTooltip = string.Empty;
    [ObservableProperty] private string _checkedCountText = "0 H";
    [ObservableProperty] private string _checkedCountTooltip = "0 H";
    [ObservableProperty] private string _liveEtaText = "\u2014";

    [ObservableProperty] private CoordinatorState _state = CoordinatorState.Idle;
    [ObservableProperty] private string _primaryButtonText = string.Empty;
    [ObservableProperty] private bool _canPrimaryAction;
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
    [ObservableProperty] private bool _showTraySpeedupHint;
    [ObservableProperty] private bool _showMiniHudInTray = true;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _selectedLanguageCode = LocalizationService.AutoLanguageCode;
    [ObservableProperty] private int _selectedMaxGpuTempLimitValue;
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

        _smoothProgressTimer = _dispatcher.CreateTimer();
        _smoothProgressTimer.Interval = TimeSpan.FromMilliseconds(33);
        _smoothProgressTimer.IsRepeating = true;
        _smoothProgressTimer.Tick += OnSmoothProgressTimerTick;

        _coordinator.TelemetryUpdated += OnCoordinatorTelemetryUpdated;
        _coordinator.MatchFound += OnCoordinatorMatchFound;
        Loc.PropertyChanged += (_, _) => RefreshLocalizedDisplays();

        RebuildLanguageOptions();
        RebuildMaxGpuTempOptions();
    }

    public async Task InitializeAsync(bool startedMinimized)
    {
        if (IsInitialized)
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
        _persistedTrayFullSpeedHps = Math.Max(0.0, persisted.TrayFullSpeedHps);
        StartWithWindows = StartupRegistryService.IsEnabled();

        _coordinator.Initialize();
        SelectedMaxGpuTempLimitValue = _coordinator.MaxGpuTemperatureCelsius ?? 0;
        DraftMinCreationDate = CreationTimestampRange.FormatUtcDate(_coordinator.MinCreationTimestamp);
        DraftMaxCreationDate = CreationTimestampRange.FormatUtcDate(_coordinator.MaxCreationTimestamp);
        RebuildMaxGpuTempOptions();
        HasHardwareGpu = _coordinator.Devices.Count > 0;

        Gpus.Clear();
        bool multiGpu = _coordinator.Devices.Count > 1;
        foreach (GpuDeviceInfo dev in _coordinator.Devices)
        {
            Gpus.Add(new GpuItemViewModel(dev, _coordinator.IsDeviceEnabled(dev.DeviceKey), multiGpu));
        }

        ReloadMatchesFromVault();

        SelectedTier = _coordinator.SelectedTier;
        DisplayTier = SelectedTier;

        bool hasSavedValidTarget = KeyIdPattern.TryParse(persisted.TargetInput, out KeyIdPattern? savedPattern, out _);
        if (hasSavedValidTarget && savedPattern is not null)
        {
            _activePatternObj = savedPattern;
            ActiveTargetSuffix = savedPattern.Suffix;
            ActiveFormattedPreview = savedPattern.FormattedDisplay;
            DraftTargetInput = savedPattern.Suffix;
            _coordinator.SetTargetPattern(savedPattern);
        }
        else
        {
            _activePatternObj = null;
            ActiveTargetSuffix = string.Empty;
            ActiveFormattedPreview = "**** **** **** ****";
            DraftTargetInput = persisted.TargetInput ?? string.Empty;
        }

        ShowSetupWizard = !(persisted.HasCompletedSetup && hasSavedValidTarget);

        _miniHud.SetSetupActive(ShowSetupWizard);
        IsInitialized = true;

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
        if (!HasHardwareGpu || !KeyIdPattern.TryParse(DraftTargetInput, out KeyIdPattern? parsed, out _) || parsed is null)
        {
            ValidateDraftTarget();
            return false;
        }

        _activePatternObj = parsed;
        ActiveTargetSuffix = parsed.Suffix;
        ActiveFormattedPreview = parsed.FormattedDisplay;
        DraftTargetInput = parsed.Suffix;
        ShowSetupWizard = false;
        _miniHud.SetSetupActive(false);
        SyncSmoothProgressTimer();

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

    public async Task<bool> CompleteSetupAndStartAsync()
    {
        if (!CompleteSetup())
        {
            return false;
        }

        await PrimaryActionAsync();
        return true;
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
            if (IsInitialized)
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
        RebuildMaxGpuTempOptions();
    }

    private void RebuildMaxGpuTempOptions()
    {
        (int Value, string Label)[] desired =
        [
            (0, Loc.ThermalLimitOff),
            (70, FormattingHelpers.FormatTemperatureCelsius(70)),
            (75, FormattingHelpers.FormatTemperatureCelsius(75)),
            (80, FormattingHelpers.FormatTemperatureCelsius(80)),
            (85, FormattingHelpers.FormatTemperatureCelsius(85)),
            (90, FormattingHelpers.FormatTemperatureCelsius(90))
        ];

        for (int i = 0; i < desired.Length; i++)
        {
            if (i < MaxGpuTempOptions.Count && MaxGpuTempOptions[i].LimitValue == desired[i].Value)
            {
                MaxGpuTempOptions[i].DisplayName = desired[i].Label;
            }
            else if (i < MaxGpuTempOptions.Count)
            {
                MaxGpuTempOptions[i] = new MaxGpuTempOption(desired[i].Value, desired[i].Label);
            }
            else
            {
                MaxGpuTempOptions.Add(new MaxGpuTempOption(desired[i].Value, desired[i].Label));
            }
        }

        while (MaxGpuTempOptions.Count > desired.Length)
        {
            MaxGpuTempOptions.RemoveAt(MaxGpuTempOptions.Count - 1);
        }

        OnPropertyChanged(nameof(SelectedMaxGpuTempLimitValue));
    }

    private void RefreshLocalizedDisplays()
    {
        ValidateDraftTarget();
        ApplyTelemetry(_coordinator.LatestTelemetry, forceTrayHintTextUpdate: true);
    }

    public void Dispose()
    {
        _smoothProgressTimer.Stop();
        _coordinator.TelemetryUpdated -= OnCoordinatorTelemetryUpdated;
        _coordinator.MatchFound -= OnCoordinatorMatchFound;
    }
}
