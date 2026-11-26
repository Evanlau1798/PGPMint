using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly DesktopSession _session;
    private readonly LocalizationService _strings;
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, CheckBox> _setupGpuChecks = [];
    private PlatformServices? _platform;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _noticeTimer;
    private bool _syncing, _busy, _ticking, _quitting, _allowClose, _closePending, _automaticUnlockAttempted;
    private long _lastRefresh;
    private string _renderedMatchKey = string.Empty, _errorText = string.Empty;

    public event Action? LanguageChanged;
    internal int RenderCount { get; private set; }
    internal TimeSpan RefreshInterval => _timer.Interval;
    internal bool ReadyToShutdown => _allowClose;

    [Obsolete("Designer only", true)]
    public MainWindow() : this(null!, null!) { }

    public MainWindow(DesktopSession session, LocalizationService strings)
    {
        _session = session;
        _strings = strings;
        InitializeComponent();
        if (session is null || strings is null) return;
        Icon = AppIcon.Load();
        SettingsIcon.Data = Icons.Settings;

        _syncing = true;
        SyncLanguageCombos();
        _syncing = false;
        UnlockLanguageCombo.SelectionChanged += OnLanguageComboChanged;
        SetupLanguageCombo.SelectionChanged += OnLanguageComboChanged;
        SetupTargetBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing) ValidateSetupDraft();
        };

        ApplyTierTooltips();
        SyncStaticTexts();
        SyncViewVisibility();

        _timer.Tick += async (_, _) => await TickAsync();
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        Closed += (_, _) => _timer.Stop();
    }

    internal void AttachPlatform(PlatformServices platform)
    {
        _platform = platform;
        _platform.SyncMenuState();
    }

    private string L(string key) => _strings[key];

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        _timer.Start();
        if (!_automaticUnlockAttempted && _session.SupportsAutomaticUnlock)
        {
            _automaticUnlockAttempted = true;
            await RunAsync(async () => await TryAutomaticUnlockAsync());
        }
        else ForceRefresh();
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_quitting || _closePending) return;
        _closePending = true;
        try
        {
            if (_session.Coordinator is { } coordinator)
            {
                bool keepInTray = _session.StateStore.Load().MinimizeToTrayOnClose;
                if (keepInTray && _platform is not null && await _platform.CanHideAsync())
                {
                    Hide();
                    return;
                }
                bool active = coordinator.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating or CoordinatorState.Paused;
                if (!active || await ConfirmAsync(this, L("StopExit"), L("NoTray"), _strings))
                    await QuitAsync();
            }
            else await QuitAsync();
        }
        catch { SetError(L("OperationFailed")); ShowWindow(); }
        finally { _closePending = false; }
    }

    private void SyncViewVisibility()
    {
        bool unlocked = _session.Coordinator is not null;
        AppPersistedState? state = unlocked ? _session.StateStore.Load() : null;
        bool setupDone = state is not null && (state.HasCompletedSetup || KeyIdPattern.TryParse(state.TargetInput, out _, out _));

        UnlockPanel.IsVisible = !unlocked;
        UnlockLanguageCombo.IsVisible = !unlocked;
        SetupWizardPanel.IsVisible = unlocked && !setupDone;
        DashboardPanel.IsVisible = unlocked && setupDone;
        SettingsButton.IsVisible = unlocked && setupDone;

        if (SetupWizardPanel.IsVisible)
        {
            _syncing = true;
            SetupTargetBox.Text = state!.TargetInput;
            _syncing = false;
            SyncLanguageCombos();
            RebuildSetupGpus();
            ValidateSetupDraft();
        }
        else if (!unlocked)
        {
            SyncLanguageCombos();
        }
    }

    private void SyncLanguageCombos()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        try
        {
            IReadOnlyList<LanguageOption> options = _strings.LanguageOptions;
            LanguageOption? selected = options.FirstOrDefault(o => string.Equals(o.Code, _strings.Language, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            UnlockLanguageCombo.ItemsSource = options;
            UnlockLanguageCombo.SelectedItem = selected;
            SetupLanguageCombo.ItemsSource = options;
            SetupLanguageCombo.SelectedItem = selected;
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void SyncStaticTexts()
    {
        SyncLanguageCombos();
        AutomationProperties.SetName(UnlockLanguageCombo, L("SettingLanguage"));
        AutomationProperties.SetName(SetupLanguageCombo, L("SettingLanguage"));
        ToolTip.SetTip(SettingsButton, L("Settings"));
        AutomationProperties.SetName(SettingsButton, L("Settings"));

        UnlockTitleText.Text = L("Unlock");
        UnlockHintText.Text = L(_session.SupportsAutomaticUnlock ? "SystemVaultHint" : "VaultHint");
        UnlockActionButton.Content = L("Unlock");
        UnlockDataDirText.Text = $"{L("DataDirectory")}: {_session.DirectoryPath}";

        SetupTitleText.Text = L("SetupTitle");
        SetupTargetBox.PlaceholderText = L("TargetPlaceholder");
        AutomationProperties.SetName(SetupTargetBox, L("SectionTarget"));
        SetupRuleHintText.Text = L("SuffixRuleHint");
        SetupGpusHeading.Text = L("SectionGpus");
        SetupNoGpuText.Text = L("NoGpuError");
        SetupLanguageLabel.Text = L("SectionLanguage");
        SetupStartButton.Content = L("Finish");

        TierEcoButton.Content = L("TierEco");
        TierBalancedButton.Content = L("TierBalanced");
        TierEfficiencyButton.Content = L("TierEfficiency");
        TierMaxButton.Content = L("TierMax");
        NoMatchesText.Text = L("NoResults");
        ClearAllButton.Content = L("ClearAll");
        TrayHintText.Text = L("TraySpeedupGenericHint");
    }

    private void ApplyTierTooltips()
    {
        SetTierTip(TierEcoWrap, TierEcoButton, "TierEcoTooltip");
        SetTierTip(TierBalancedWrap, TierBalancedButton, "TierBalancedTooltip");
        SetTierTip(TierEfficiencyWrap, TierEfficiencyButton, "TierEfficiencyTooltip");
        SetTierTip(TierMaxWrap, TierMaxButton, "TierMaxTooltip");
    }

    private void SetTierTip(Border wrap, Button btn, string key) { string tip = L(key); ToolTip.SetTip(wrap, tip); ToolTip.SetTip(btn, tip); }

    private void RebuildSetupGpus()
    {
        _setupGpuChecks.Clear();
        SetupGpusList.Children.Clear();
        if (_session.Coordinator is not { } coordinator) return;
        SetupNoGpuText.IsVisible = coordinator.Devices.Count == 0;
        bool multi = coordinator.Devices.Count > 1;
        foreach (GpuDeviceInfo gpu in coordinator.Devices)
        {
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
            var check = new CheckBox { Content = gpu.Name, IsChecked = coordinator.IsDeviceEnabled(gpu.DeviceKey), IsEnabled = multi };
            check.IsCheckedChanged += async (_, _) =>
            {
                if (_syncing || _session.Coordinator is null) return;
                try { await _session.Coordinator.SetDeviceEnabledAsync(gpu.DeviceKey, check.IsChecked == true); }
                catch { SetError(L("OperationFailed")); }
            };
            Grid.SetColumn(check, 0);
            var pill = new Border { Classes = { "pill" }, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = gpu.BackendName, FontSize = 11 };
            label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            pill.Child = label;
            Grid.SetColumn(pill, 1);
            row.Children.Add(check);
            row.Children.Add(pill);
            SetupGpusList.Children.Add(row);
            _setupGpuChecks[gpu.DeviceKey] = check;
        }
    }

    private void ValidateSetupDraft()
    {
        DraftValidation v = DesktopDisplayMapper.ValidateDraft(SetupTargetBox.Text, _strings);
        SetupPreviewText.Text = v.FormattedPreview;
        SetupValidationText.Text = v.ErrorText;
        SetupValidationText.IsVisible = v.HasError;
        SetupStartButton.IsEnabled = !_busy && v.IsValid;
    }

    internal void Refresh()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (_lastRefresh != 0 && Stopwatch.GetElapsedTime(_lastRefresh) < _timer.Interval) return;
        _lastRefresh = Stopwatch.GetTimestamp();
        RenderCount++;
        RenderCurrentState();
    }

    private void ForceRefresh() { _lastRefresh = 0; Refresh(); }

    private void RenderCurrentState()
    {
        ErrorBannerText.Text = _errorText;
        ErrorBanner.IsVisible = !string.IsNullOrEmpty(_errorText);
        if (_session.Coordinator is not { } coordinator)
        {
            StatusDot.Fill = Brush.Parse("#808080");
            return;
        }

        CoordinatorTelemetry t = coordinator.LatestTelemetry;
        StatusDot.Fill = Brush.Parse(t.State switch
        {
            CoordinatorState.Running or CoordinatorState.Calibrating => "#16A34A",
            CoordinatorState.Faulted => "#DC2626",
            CoordinatorState.Stopping or CoordinatorState.Paused => "#D97706",
            _ => "#808080"
        });

        AppPersistedState state = _session.StateStore.Load();
        KeyIdPattern.TryParse(state.TargetInput, out KeyIdPattern? activePattern, out _);
        IReadOnlyList<MatchedKeyRecord> matches = _session.Matches();
        TelemetryDisplay d = DesktopDisplayMapper.BuildDisplay(coordinator, t, activePattern, matches, _strings, _busy);

        ActivePreviewText.Text = activePattern?.FormattedDisplay ?? "**** **** **** ****";
        RateNumberText.Text = d.RateNumber;
        RateUnitText.Text = d.RateUnit;
        EfficiencyBadgeText.Text = d.BadgeText;
        ToolTip.SetTip(EfficiencyBadgeBorder, d.BadgeTooltip);

        UpdateTierButton(TierEcoButton, PowerTier.Eco, d.DisplayTier, d.TierSelectorEnabled);
        UpdateTierButton(TierBalancedButton, PowerTier.Balanced, d.DisplayTier, d.TierSelectorEnabled);
        UpdateTierButton(TierEfficiencyButton, PowerTier.Efficiency, d.DisplayTier, d.TierSelectorEnabled);
        UpdateTierButton(TierMaxButton, PowerTier.Max, d.DisplayTier, d.TierSelectorEnabled);

        PrimaryActionButton.Content = d.PrimaryButtonText;
        PrimaryActionButton.IsEnabled = d.CanPrimaryAction;
        SearchProgressBar.Value = d.ProgressPercent;
        ProbabilityText.Text = d.ProbabilityText;
        ToolTip.SetTip(ProbabilityText, string.IsNullOrEmpty(d.SingleHashTooltip) ? null : d.SingleHashTooltip);
        CheckedCountText.Text = d.CheckedText;
        ToolTip.SetTip(CheckedCountText, $"{d.CheckedExactTooltip} \u00B7 {L("Verified")}: {t.SessionWork.VerifiedHits:N0}");
        EtaText.Text = d.EtaText;

        if (d.ErrorText is not null) { ErrorBannerText.Text = d.ErrorText; ErrorBanner.IsVisible = true; }
        TrayHintText.IsVisible = matches.Count == 0 && state.MinimizeToTrayOnClose &&
            t.State is CoordinatorState.Running or CoordinatorState.Calibrating && t.AllEnabledFullyCalibrated;
        RenderMatches(matches);
        _settingsWindow?.RefreshDynamicState();
    }

    private static void UpdateTierButton(Button button, PowerTier tier, PowerTier activeTier, bool enabled)
    {
        bool selected = tier == activeTier;
        button.Classes.Set("tier-active", selected);
        button.Classes.Set("tier-ghost", !selected);
        button.IsEnabled = enabled;
    }

    private void SetError(string? message)
    {
        _errorText = message ?? string.Empty;
        ErrorBannerText.Text = _errorText;
        ErrorBanner.IsVisible = !string.IsNullOrEmpty(_errorText);
    }

    private void FlashNotice(string text)
    {
        TransientNoticeText.Text = text;
        TransientNoticeText.IsVisible = true;
        _noticeTimer?.Stop();
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            TransientNoticeText.IsVisible = false;
            TransientNoticeText.Text = string.Empty;
            ForceRefresh();
        };
        _noticeTimer.Start();
    }

    private async Task TickAsync()
    {
        if (_ticking || _quitting) return;
        _ticking = true;
        try
        {
            if (_platform is not null)
            {
                bool active = _session.Coordinator?.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating;
                await _platform.SetSearchingAsync(active);
                if (_quitting) return;
                if (!IsVisible && !await _platform.CanStayHiddenAsync()) ShowWindow();
                _platform.SyncMenuState();
            }
            if (!_busy) Refresh();
        }
        catch { if (IsVisible) SetError(L("OperationFailed")); }
        finally { _ticking = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_quitting || !await _actions.WaitAsync(0)) return;
        _busy = true;
        ForceRefresh();
        try { SetError(null); await action(); }
        catch (ArgumentException) { SetError(L("ExportRequired") + " / " + L("SuffixRuleHint")); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { SetError(L("BackendError")); }
        catch (Exception) when (_session.Coordinator is null && _session.SupportsAutomaticUnlock
            && (AutomaticVault.IsAutomatic(_session.DirectoryPath) || !File.Exists(Path.Combine(_session.DirectoryPath, "vault.key.json"))))
        { SetError(L("SystemVaultFailed")); }
        catch (Exception) { SetError(L(_session.Coordinator is null ? "UnlockFailed" : "OperationFailed")); }
        finally { _busy = false; _actions.Release(); ForceRefresh(); }
    }

    public void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); ForceRefresh(); }

    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _noticeTimer?.Stop();
        _settingsWindow?.Close();
        foreach (Window owned in OwnedWindows.ToArray()) owned.Close();
        await _actions.WaitAsync();
        try
        {
            await _session.DisposeAsync();
            if (_platform is not null) await _platform.DisposeAsync();
            _timer.Stop();
            _allowClose = true;
            Close();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown();
        }
        catch { _quitting = false; SetError(L("OperationFailed")); ShowWindow(); }
        finally { _actions.Release(); }
    }
}
