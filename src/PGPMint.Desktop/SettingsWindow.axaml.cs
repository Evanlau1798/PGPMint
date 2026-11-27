using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed partial class SettingsWindow : Window
{
    private sealed record MaxGpuTempOption(int LimitValue, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private readonly DesktopSession _session;
    private readonly LocalizationService _strings;
    private readonly MainWindow _ownerWindow;
    private readonly Dictionary<string, (CheckBox Enabled, TextBlock Rate)> _gpuRows = [];
    private bool _syncing;

    [Obsolete("Designer only", true)]
    public SettingsWindow() : this(null!, null!, null!) { }

    public SettingsWindow(MainWindow ownerWindow, DesktopSession session, LocalizationService strings)
    {
        _ownerWindow = ownerWindow;
        _session = session;
        _strings = strings;
        InitializeComponent();
        if (ownerWindow is null || session is null || strings is null) return;
        Icon = AppIcon.Load();
        LanguageCombo.SelectionChanged += OnLanguageSelectionChanged;
        MaxGpuTempCombo.SelectionChanged += OnMaxGpuTempSelectionChanged;
        TargetInputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !_syncing && _session.Coordinator is not null)
                ValidateDraft(_session.Coordinator.CurrentState is CoordinatorState.Idle or CoordinatorState.Faulted);
        };
        ApplyLocalizationAndState();
    }

    internal void ApplyLocalizationAndState()
    {
        _syncing = true;
        try
        {
            Title = $"PGPMint - {_strings["Settings"]}";
            LanguageHeading.Text = _strings["SectionLanguage"];
            TargetHeading.Text = _strings["SectionTarget"];
            ApplyTargetButton.Content = _strings["Apply"];
            TargetInputBox.PlaceholderText = _strings["TargetPlaceholder"];
            AutomationProperties.SetName(TargetInputBox, _strings["SectionTarget"]);
            TargetRuleHintText.Text = _strings["SuffixRuleHint"];
            StopToEditText.Text = _strings["StopToEditHint"];

            PreferencesHeading.Text = _strings["SectionPreferences"];
            AutoPauseCheckBox.Content = _strings["SettingAutoPauseOnHit"];
            KeepInTrayCheckBox.Content = _strings["SettingMinimizeToTray"];
            MaxGpuTempLabel.Text = _strings["SettingMaxGpuTemp"];
            NoTrayHintText.Text = _strings["NoTrayHint"];

            AppPersistedState state = _session.StateStore.Load();
            MaxGpuTempOption[] tempOptions =
            [
                new(0, _strings["ThermalLimitOff"]),
                new(70, FormattingHelpers.FormatTemperatureCelsius(70)),
                new(75, FormattingHelpers.FormatTemperatureCelsius(75)),
                new(80, FormattingHelpers.FormatTemperatureCelsius(80)),
                new(85, FormattingHelpers.FormatTemperatureCelsius(85)),
                new(90, FormattingHelpers.FormatTemperatureCelsius(90))
            ];
            int currentLimit = _session.Coordinator?.MaxGpuTemperatureCelsius ?? state.MaxGpuTemperatureCelsius ?? 0;
            MaxGpuTempCombo.ItemsSource = tempOptions;
            MaxGpuTempCombo.SelectedItem = tempOptions.FirstOrDefault(o => o.LimitValue == currentLimit) ?? tempOptions[0];
            AutomationProperties.SetName(MaxGpuTempCombo, _strings["SettingMaxGpuTemp"]);

            GpusHeading.Text = _strings["SectionGpus"];
            RecalibrateButton.Content = _strings["Recalibrate"];
            NoGpuText.Text = _strings["NoGpuError"];

            DataDirHeading.Text = _strings["DataDirectory"];
            DataDirValueText.Text = _session.DirectoryPath;
            LockVaultButton.Content = _strings["Lock"];
            QuitAppButton.Content = _strings["Quit"];

            IReadOnlyList<LanguageOption> options = _strings.LanguageOptions;
            LanguageCombo.ItemsSource = options;
            LanguageCombo.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Code, _strings.Language, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            AutomationProperties.SetName(LanguageCombo, _strings["SettingLanguage"]);

            TargetInputBox.Text = state.TargetInput;
            AutoPauseCheckBox.IsChecked = state.AutoPauseOnHit;
            KeepInTrayCheckBox.IsChecked = state.MinimizeToTrayOnClose;

            RebuildGpuRows();
            RefreshDynamicState();
        }
        finally
        {
            _syncing = false;
        }
    }

    internal void RefreshDynamicState()
    {
        if (_session.Coordinator is not { } coordinator)
        {
            Close();
            return;
        }

        CoordinatorTelemetry t = coordinator.LatestTelemetry;
        bool editable = t.State is CoordinatorState.Idle or CoordinatorState.Faulted;
        bool busyCal = t.State is CoordinatorState.Calibrating or CoordinatorState.Stopping;
        bool multiGpu = coordinator.Devices.Count > 1;

        TargetInputBox.IsEnabled = editable;
        StopToEditText.IsVisible = !editable;
        ValidateDraft(editable);

        AppPersistedState state = _session.StateStore.Load();
        bool hasValidTarget = KeyIdPattern.TryParse(state.TargetInput, out _, out _);
        RecalibrateButton.IsEnabled = editable && coordinator.GetEnabledDevices().Count > 0 && hasValidTarget;

        foreach (GpuTelemetrySnapshot dev in t.Devices)
        {
            if (!_gpuRows.TryGetValue(dev.DeviceKey, out var row)) continue;
            _syncing = true;
            row.Enabled.IsChecked = dev.IsEnabled;
            row.Enabled.IsEnabled = multiGpu && !busyCal;
            _syncing = false;
            string summary = (t.State is CoordinatorState.Running or CoordinatorState.Calibrating && dev.IsEnabled)
                ? FormattingHelpers.FormatDeviceStatusSummary(dev, _strings["ThermalGuardActiveSuffix"], _strings["ThermalThrottleWarningSuffix"])
                : (dev.Error ?? string.Empty);
            row.Rate.Text = string.IsNullOrEmpty(summary) && dev.BaselineHps > 0
                ? $"0.00 GH/s / {FormattingHelpers.FormatHashRateCompact(dev.BaselineHps)}"
                : summary;
            row.Rate.IsVisible = !string.IsNullOrEmpty(row.Rate.Text);
        }
    }

    private void RebuildGpuRows()
    {
        _gpuRows.Clear();
        GpusListPanel.Children.Clear();
        if (_session.Coordinator is not { } coordinator) return;

        NoGpuText.IsVisible = coordinator.Devices.Count == 0;
        GpuDiagnosticsText.Text = string.Join('\n', _session.GpuDiagnostics);
        GpuDiagnosticsText.IsVisible = _session.GpuDiagnostics.Count > 0;

        bool multiGpu = coordinator.Devices.Count > 1;
        foreach (GpuDeviceInfo gpu in coordinator.Devices)
        {
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 8 };
            var stack = new StackPanel { Spacing = 2 };
            var check = new CheckBox
            {
                Content = gpu.Name,
                IsChecked = coordinator.IsDeviceEnabled(gpu.DeviceKey),
                IsEnabled = multiGpu
            };
            check.IsCheckedChanged += async (_, _) =>
            {
                if (_syncing || _session.Coordinator is null) return;
                try
                {
                    await _session.Coordinator.SetDeviceEnabledAsync(gpu.DeviceKey, check.IsChecked == true);
                    _ownerWindow.Refresh();
                    RefreshDynamicState();
                }
                catch { ShowError(_strings["OperationFailed"]); }
            };
            var rate = new TextBlock
            {
                FontSize = 11,
                IsVisible = false,
                Margin = new Thickness(28, 0, 0, 0)
            };
            rate.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            stack.Children.Add(check);
            stack.Children.Add(rate);
            Grid.SetColumn(stack, 0);

            var badge = new Border { Classes = { "pill" }, VerticalAlignment = VerticalAlignment.Center };
            var badgeText = new TextBlock { Text = gpu.BackendName, FontSize = 11 };
            badgeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            badge.Child = badgeText;
            Grid.SetColumn(badge, 1);

            grid.Children.Add(stack);
            grid.Children.Add(badge);
            GpusListPanel.Children.Add(grid);
            _gpuRows[gpu.DeviceKey] = (check, rate);
        }
    }

    private void ValidateDraft(bool editable)
    {
        DraftValidation v = DesktopDisplayMapper.ValidateDraft(TargetInputBox.Text, _strings);
        DraftPreviewText.Text = v.FormattedPreview;
        TargetValidationText.Text = v.ErrorText;
        TargetValidationText.IsVisible = v.HasError;
        string active = _session.StateStore.Load().TargetInput ?? string.Empty;
        ApplyTargetButton.IsEnabled = editable && v.IsValid && v.Pattern is not null &&
            !string.Equals(v.Pattern.Suffix, active, StringComparison.OrdinalIgnoreCase);
    }

    private void OnApplyTargetClick(object? sender, RoutedEventArgs e)
    {
        if (_session.Coordinator is not { } coordinator) return;
        try
        {
            ClearError();
            KeyIdPattern parsed = KeyIdPattern.Parse(TargetInputBox.Text ?? string.Empty);
            coordinator.SetTargetPattern(parsed);
            _session.StateStore.Update(s =>
            {
                s.TargetInput = parsed.Suffix;
                s.HasCompletedSetup = true;
            });
            TargetInputBox.Text = parsed.Suffix;
            _ownerWindow.OnSettingsChangedFromDialog(flashApplied: true);
            RefreshDynamicState();
        }
        catch { ShowError(_strings["OperationFailed"]); }
    }

    private void OnLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || LanguageCombo.SelectedItem is not LanguageOption option) return;
        try
        {
            ClearError();
            _ownerWindow.ApplyLanguageSelection(option.Code);
            ApplyLocalizationAndState();
        }
        catch { ShowError(_strings["OperationFailed"]); }
    }

    private void OnMaxGpuTempSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _session.Coordinator is null || MaxGpuTempCombo.SelectedItem is not MaxGpuTempOption option) return;
        try
        {
            ClearError();
            _session.Coordinator.SetMaxGpuTemperatureCelsius(option.LimitValue > 0 ? option.LimitValue : null);
            _ownerWindow.Refresh();
        }
        catch { ShowError(_strings["OperationFailed"]); }
    }

    private void OnPreferencesChanged(object? sender, RoutedEventArgs e)
    {
        if (_syncing || _session.Coordinator is null) return;
        try
        {
            ClearError();
            bool autoPause = AutoPauseCheckBox.IsChecked == true;
            bool keepInTray = KeepInTrayCheckBox.IsChecked == true;
            _session.Coordinator.SetAutoPauseOnHit(autoPause);
            _session.StateStore.Update(s =>
            {
                s.AutoPauseOnHit = autoPause;
                s.MinimizeToTrayOnClose = keepInTray;
            });
            _ownerWindow.Refresh();
        }
        catch { ShowError(_strings["OperationFailed"]); }
    }

    private async void OnRecalibrateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ClearError();
            await _ownerWindow.RecalibrateFromSettingsAsync();
            RefreshDynamicState();
        }
        catch { ShowError(_strings["OperationFailed"]); }
    }

    private async void OnLockClick(object? sender, RoutedEventArgs e)
    {
        Close();
        await _ownerWindow.LockVaultFromSettingsAsync();
    }

    private async void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        Close();
        await _ownerWindow.QuitAsync();
    }

    private void ShowError(string message)
    {
        SettingsErrorText.Text = message;
        SettingsErrorText.IsVisible = true;
    }

    private void ClearError()
    {
        SettingsErrorText.Text = string.Empty;
        SettingsErrorText.IsVisible = false;
    }
}
