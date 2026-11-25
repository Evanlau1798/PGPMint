using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed class MainWindow : Window
{
    private readonly DesktopSession _session;
    private readonly LocalizationService _strings;
    private readonly SemaphoreSlim _actions = new(1, 1);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, (CheckBox Enabled, TextBlock Status)> _gpus = [];
    private readonly List<Button> _buttons = [];
    private TextBlock _status = new(), _error = new(), _sleepStatus = new();
    private TextBox _target = new();
    private CheckBox _autoPause = new(), _keepInTray = new();
    private ComboBox _tier = new();
    private ListBox _matches = new();
    private Button _start = new(), _pause = new(), _stop = new(), _calibrate = new(), _apply = new();
    private bool _quitting, _allowClose, _busy, _ticking, _closePending;
    private long _lastRefresh;
    private string _matchIds = "";
    private PlatformServices? _platform;
    public event Action? LanguageChanged;
    internal int RenderCount { get; private set; }
    internal TimeSpan RefreshInterval => _timer.Interval;
    internal bool ReadyToShutdown => _allowClose;

    public MainWindow(DesktopSession session, LocalizationService strings)
    {
        _session = session;
        _strings = strings;
        Title = "PGPMint";
        Width = 980;
        Height = 800;
        MinWidth = 660;
        MinHeight = 540;
        BuildUi();
        _timer.Tick += async (_, _) => await TickAsync();
        Opened += (_, _) => _timer.Start();
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_quitting || _closePending) return;
            _closePending = true;
            try
            {
                if (_session.Coordinator is not null && _keepInTray.IsChecked == true && _platform is not null && await _platform.CanHideAsync())
                    Hide();
                else if (await ConfirmAsync(_strings["StopExit"], _strings["NoTray"])) await QuitAsync();
            }
            catch { _error.Text = L("OperationFailed"); ShowWindow(); }
            finally { _closePending = false; }
        };
        Closed += (_, _) => _timer.Stop();
    }

    internal void AttachPlatform(PlatformServices platform) => _platform = platform;

    private string L(string key) => _strings[key];
    private Button Button(string key, Func<Task> action)
    {
        var button = new Button { Content = L(key) };
        button.Click += async (_, _) => { if (key == "Quit") await QuitAsync(); else await RunAsync(action); };
        _buttons.Add(button);
        return button;
    }

    private void BuildUi()
    {
        _buttons.Clear();
        _gpus.Clear();
        _matchIds = "";
        _lastRefresh = 0;
        var coordinator = _session.Coordinator;
        var settings = coordinator is null ? new AppPersistedState() : _session.StateStore.Load();
        var body = new StackPanel { Margin = new(24), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = "PGPMint", FontSize = 28, FontWeight = FontWeight.SemiBold });
        var languages = new ComboBox { ItemsSource = _strings.Languages, SelectedItem = _strings.Language, MinWidth = 150 };
        AutomationProperties.SetName(languages, L("SettingLanguage"));
        languages.SelectionChanged += (_, _) =>
        {
            if (languages.SelectedItem is not string code || _busy) return;
            try
            {
                if (_session.Coordinator is not null) _session.StateStore.Update(s => s.Language = code);
                _strings.Apply(code);
                BuildUi();
                LanguageChanged?.Invoke();
            }
            catch { _error.Text = L("OperationFailed"); }
        };
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            Children = { new TextBlock { Text = L("SettingLanguage"), VerticalAlignment = VerticalAlignment.Center }, languages } });
        body.Children.Add(new TextBlock { Text = L("DataDirectory") + ": " + _session.DirectoryPath, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Button(coordinator is null ? "Unlock" : "Lock", async () =>
        {
            if (_session.Coordinator is null) await UnlockAsync();
            else { await _session.DisposeAsync(); if (_platform is not null) await _platform.SetSearchingAsync(false); BuildUi(); }
        }));
        if (coordinator is null)
        {
            body.Children.Add(new TextBlock { Text = L("VaultHint"), TextWrapping = TextWrapping.Wrap });
            _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
            body.Children.Add(_error);
            Content = new ScrollViewer { Content = body };
            return;
        }
        body.Children.Add(new TextBlock { Text = L("SectionTarget"), FontSize = 18 });
        _target = new TextBox { Text = settings.TargetInput, PlaceholderText = L("TargetPlaceholder") };
        AutomationProperties.SetName(_target, L("SectionTarget"));
        body.Children.Add(_target);
        body.Children.Add(new TextBlock { Text = L("SuffixRuleHint"), TextWrapping = TextWrapping.Wrap });
        _autoPause = new CheckBox { Content = L("SettingAutoPauseOnHit"), IsChecked = settings.AutoPauseOnHit };
        _keepInTray = new CheckBox { Content = L("SettingMinimizeToTray"), IsChecked = settings.MinimizeToTrayOnClose };
        body.Children.Add(_autoPause);
        body.Children.Add(_keepInTray);
        body.Children.Add(new TextBlock { Text = L("NoTrayHint"), TextWrapping = TextWrapping.Wrap });
        _apply = Button("Apply", () => { ApplySettings(); return Task.CompletedTask; });
        body.Children.Add(_apply);
        body.Children.Add(new TextBlock { Text = L("PowerTier") });
        _tier = new ComboBox { ItemsSource = Enum.GetValues<PowerTier>().Select(t => L("Tier" + t)).ToArray(), SelectedIndex = (int)coordinator.SelectedTier };
        AutomationProperties.SetName(_tier, L("PowerTier"));
        _tier.SelectionChanged += (_, _) =>
        {
            try { if (_tier.SelectedIndex >= 0 && !_busy) coordinator.SetPowerTier((PowerTier)_tier.SelectedIndex); }
            catch { _error.Text = L("OperationFailed"); }
        };
        body.Children.Add(_tier);
        body.Children.Add(new TextBlock { Text = L("SectionGpus"), FontSize = 18 });
        foreach (var gpu in coordinator.Devices)
        {
            var enabled = new CheckBox { Content = gpu.Name + " · " + gpu.BackendName, IsChecked = coordinator.IsDeviceEnabled(gpu.DeviceKey) };
            enabled.IsCheckedChanged += async (_, _) => await RunAsync(async () =>
            {
                await coordinator.SetDeviceEnabledAsync(gpu.DeviceKey, enabled.IsChecked == true);
                enabled.IsChecked = coordinator.IsDeviceEnabled(gpu.DeviceKey);
            });
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _gpus[gpu.DeviceKey] = (enabled, status);
            body.Children.Add(enabled);
            body.Children.Add(status);
        }
        if (coordinator.Devices.Count == 0) body.Children.Add(new TextBlock { Text = L("NoGpuError"), Foreground = Brushes.OrangeRed });
        if (_session.GpuDiagnostics.Count > 0) body.Children.Add(new TextBlock { Text = string.Join('\n', _session.GpuDiagnostics), TextWrapping = TextWrapping.Wrap });
        _start = Button("Start", async () => { ApplySettings(); await coordinator.StartSearchAsync(ParseTarget()); });
        _pause = Button("Pause", () => { if (coordinator.CurrentState == CoordinatorState.Paused) coordinator.Resume(); else coordinator.Pause(); return Task.CompletedTask; });
        _stop = Button("Stop", coordinator.StopAsync);
        _calibrate = Button("Recalibrate", async () =>
        {
            ApplySettings();
            if (coordinator.InvalidateCalibrationsForRecalibration()) await coordinator.StartSearchAsync(ParseTarget());
        });
        body.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { _start, _pause, _stop, _calibrate } });
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _sleepStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed };
        body.Children.Add(_status);
        body.Children.Add(_sleepStatus);
        body.Children.Add(_error);
        body.Children.Add(new TextBlock { Text = L("Saved"), FontSize = 18 });
        _matches = new ListBox { MinHeight = 100, MaxHeight = 240 };
        AutomationProperties.SetName(_matches, L("Saved"));
        body.Children.Add(_matches);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children =
        {
            Button("ExportTitle", ExportAsync),
            Button("Delete", async () =>
            {
                if (_matches.SelectedItem is not ListBoxItem { Tag: MatchedKeyRecord record }) throw new InvalidOperationException();
                if (await ConfirmAsync(L("DeleteConfirmTitle"), record.Fingerprint)) coordinator.DeleteVaultMatch(record.Id);
            }),
            Button("Quit", QuitAsync)
        }});
        Content = new ScrollViewer { Content = body };
        Refresh();
    }

    private KeyIdPattern ParseTarget()
    {
        if (string.IsNullOrWhiteSpace(_target.Text)) throw new ArgumentException(L("TargetRequired"));
        return KeyIdPattern.Parse(_target.Text);
    }

    private void ApplySettings()
    {
        var coordinator = _session.Coordinator!;
        coordinator.SetTargetPattern(ParseTarget());
        coordinator.SetAutoPauseOnHit(_autoPause.IsChecked == true);
        _session.StateStore.Update(s => { s.TargetInput = _target.Text!; s.Language = _strings.Language;
            s.MinimizeToTrayOnClose = _keepInTray.IsChecked == true; s.HasCompletedSetup = true; });
    }

    private async Task UnlockAsync()
    {
        var input = await new SecretDialog(_strings).ShowDialog<SecretInput?>(this);
        if (input is null) return;
        try { await Task.Run(() => _session.Unlock(input.Password)); }
        finally { Array.Clear(input.Password); }
        _strings.Apply(_session.StateStore.Load().Language);
        BuildUi();
        LanguageChanged?.Invoke();
    }

    private async Task ExportAsync()
    {
        if (_matches.SelectedItem is not ListBoxItem { Tag: MatchedKeyRecord record }) { _error.Text = L("SelectionRequired"); return; }
        var input = await new SecretDialog(_strings, export: true).ShowDialog<SecretInput?>(this);
        if (input is null) return;
        try
        {
            string? secret = await SavePathAsync("ExportPrivate", record.KeyId + ".secret.asc");
            if (secret is null) return;
            string? pub = await SavePathAsync("ExportPublic", record.KeyId + ".public.asc");
            if (pub is null) return;
            await Task.Run(() => _session.Export(record, input.Uid, input.Email, new string(input.Password), secret, pub));
            _error.Text = L("ExportSuccess");
        }
        finally { Array.Clear(input.Password); }
    }

    private async Task<string?> SavePathAsync(string title, string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L(title), SuggestedFileName = suggested, DefaultExtension = "asc", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("OpenPGP ASCII armor") { Patterns = ["*.asc"] }]
        });
        if (file is null) return null;
        return file.TryGetLocalPath() ?? throw new IOException(L("LocalFileRequired"));
    }

    internal void Refresh()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (_lastRefresh != 0 && Stopwatch.GetElapsedTime(_lastRefresh) < _timer.Interval) return;
        _lastRefresh = Stopwatch.GetTimestamp();
        RenderCount++;
        if (_session.Coordinator is not { } coordinator) return;
        var t = coordinator.LatestTelemetry;
        bool editable = t.State is CoordinatorState.Idle or CoordinatorState.Faulted;
        _target.IsEnabled = _apply.IsEnabled = editable && !_busy;
        _tier.IsEnabled = !_busy && !coordinator.NeedsFullCalibration() && t.State is not (CoordinatorState.Calibrating or CoordinatorState.Stopping);
        _tier.SelectedIndex = (int)t.ActiveTier;
        _start.IsEnabled = _calibrate.IsEnabled = !_busy && editable && coordinator.GetEnabledDevices().Count > 0;
        _pause.IsEnabled = !_busy && t.State is CoordinatorState.Running or CoordinatorState.Paused;
        _pause.Content = L(t.State == CoordinatorState.Paused ? "Resume" : "Pause");
        _stop.IsEnabled = !_busy && !editable;
        _status.Text = $"{t.State} · {t.TotalActualHps:N0} H/s\n{L("Checked")}: {t.SessionChecked:N0} / {t.TotalChecked:N0} · {L("Verified")}: {t.SessionWork.VerifiedHits:N0} · {L("Saved")}: {t.TotalMatchesCount}\n{L("Calibration")}: {Math.Ceiling(t.CalibrationRemainingSeconds)}";
        if (t.ErrorMessage is not null) _error.Text = t.Failure switch
        {
            SearchFailure.SaveFailed => L("VaultSaveFailedError"), SearchFailure.IncompleteResults => L("IncompleteSearchResultsError"),
            _ => t.VaultIsFull ? L("VaultFullError") : t.ErrorMessage
        };
        _sleepStatus.Text = _platform?.Searching == true ? L(_platform.SleepProtected ? "SleepProtected" : "SleepUnavailable") : "";
        foreach (var gpu in t.Devices)
            if (_gpus.TryGetValue(gpu.DeviceKey, out var row))
            {
                row.Enabled.IsEnabled = !_busy && t.State is not (CoordinatorState.Calibrating or CoordinatorState.Stopping);
                row.Status.Text = $"{gpu.ActualHps:N0} H/s · {gpu.TargetHps:N0} target · {gpu.Error}";
            }
        var matches = _session.Matches();
        string ids = string.Join(',', matches.Select(m => m.Id));
        if (ids != _matchIds)
        {
            string? selected = (_matches.SelectedItem as ListBoxItem)?.Tag is MatchedKeyRecord r ? r.Id : null;
            _matches.ItemsSource = matches.Select(m => new ListBoxItem { Content = $"{m.KeyId} · {m.Fingerprint} · {m.FoundAtUtc:g}", Tag = m }).ToArray();
            _matches.SelectedItem = _matches.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((MatchedKeyRecord)i.Tag!).Id == selected);
            _matchIds = ids;
        }
    }

    private async Task TickAsync()
    {
        if (_ticking || _quitting) return;
        _ticking = true;
        try
        {
            if (_platform is not null)
            {
                await _platform.SetSearchingAsync(_session.Coordinator?.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating);
                if (_quitting) return;
                if (!IsVisible && !await _platform.CanStayHiddenAsync()) ShowWindow();
            }
            if (!_busy) Refresh();
        }
        catch { if (IsVisible) _error.Text = L("OperationFailed"); }
        finally { _ticking = false; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_quitting || !await _actions.WaitAsync(0)) return;
        _busy = true;
        foreach (var button in _buttons) button.IsEnabled = false;
        try { _error.Text = ""; await action(); }
        catch (ArgumentException) { _error.Text = L("ExportRequired") + " / " + L("SuffixRuleHint"); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { _error.Text = L("BackendError"); }
        catch (Exception) { _error.Text = L(_session.Coordinator is null ? "UnlockFailed" : "OperationFailed"); }
        finally
        {
            _busy = false;
            _actions.Release();
            foreach (var button in _buttons) button.IsEnabled = true;
            Refresh();
        }
    }

    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, LocalizationService strings)
    {
        var dialog = new Window { Title = title, Width = 450, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = strings["Apply"] };
        var no = new Button { Content = strings["Cancel"], IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel { Margin = new(24), Spacing = 16, Children =
        {
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } }
        }};
        return await dialog.ShowDialog<bool>(owner);
    }

    private Task<bool> ConfirmAsync(string title, string message) => ConfirmAsync(this, title, message, _strings);
    public void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); Refresh(); }

    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        foreach (var owned in OwnedWindows.ToArray()) owned.Close();
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
        catch { _quitting = false; _error.Text = L("OperationFailed"); ShowWindow(); }
        finally { _actions.Release(); }
    }
}
