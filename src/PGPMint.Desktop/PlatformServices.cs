using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using PGPMint.Core.Search;

namespace PGPMint.Desktop;

public sealed class PlatformServices : IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly DesktopSession _session;
    private readonly LocalizationService _strings;
    private readonly SleepInhibitor _inhibitor = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TrayIcon _tray;
    private bool _disposed, _trayPresent;
    private DateTimeOffset _lastAttempt, _lastTrayCheck;
    private string _menuSignature = string.Empty;

    public bool Searching { get; private set; }
    public bool SleepProtected => _inhibitor.IsHeld;

    public PlatformServices(MainWindow window, DesktopSession session, LocalizationService strings)
    {
        _window = window;
        _session = session;
        _strings = strings;
        _tray = new TrayIcon { ToolTipText = "PGPMint", Icon = AppIcon.Load(), IsVisible = true };
        _tray.Clicked += (_, _) => _window.ShowWindow();
        UpdateMenu();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        _window.LanguageChanged += UpdateMenu;
    }

    internal void SyncMenuState()
    {
        if (_disposed) return;
        string sig = ComputeMenuSignature();
        if (sig == _menuSignature) return;
        UpdateMenu();
    }

    private string ComputeMenuSignature()
    {
        var c = _session.Coordinator;
        if (c is null) return $"locked|{_strings.ActiveCode}";
        CoordinatorTelemetry t = c.LatestTelemetry;
        bool tierEnabled = c.Devices.Count > 0 && t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0
            && t.State is not (CoordinatorState.Calibrating or CoordinatorState.Stopping);
        PowerTier displayTier = (t.State == CoordinatorState.Calibrating || !t.AllEnabledFullyCalibrated || t.TotalBaselineHps <= 0.0)
            ? PowerTier.Max : t.ActiveTier;
        return $"{_strings.ActiveCode}|{t.State}|{displayTier}|{tierEnabled}";
    }

    private void UpdateMenu()
    {
        _menuSignature = ComputeMenuSignature();
        NativeMenu menu = _tray.Menu ?? new NativeMenu();
        menu.Items.Clear();

        void AddAction(string text, Func<Task> action, bool enabled = true)
        {
            var item = new NativeMenuItem(text) { IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch { _window.ShowWindow(); }
            };
            menu.Items.Add(item);
        }

        AddAction(_strings["Open"], () => { _window.ShowWindow(); return Task.CompletedTask; });
        menu.Items.Add(new NativeMenuItemSeparator());

        var c = _session.Coordinator;
        CoordinatorTelemetry? t = c?.LatestTelemetry;
        bool active = t is not null && t.State is CoordinatorState.Running or CoordinatorState.Calibrating or CoordinatorState.Paused;
        bool stopping = t?.State == CoordinatorState.Stopping;
        string primaryLabel = stopping ? _strings["Stopping"] : _strings[active ? "Stop" : "Start"];
        bool canPrimary = c is not null && !stopping && (active || c.GetEnabledDevices().Count > 0);
        AddAction(primaryLabel, _window.TriggerPrimaryActionFromTrayAsync, canPrimary);

        var tierMenu = new NativeMenu();
        bool hasBaseline = t is not null && t.AllEnabledFullyCalibrated && t.TotalBaselineHps > 0.0;
        bool tierEnabled = c is not null && c.Devices.Count > 0 && hasBaseline
            && t!.State is not (CoordinatorState.Calibrating or CoordinatorState.Stopping);
        PowerTier displayTier = (t is null || t.State == CoordinatorState.Calibrating || !hasBaseline) ? PowerTier.Max : t.ActiveTier;
        foreach (PowerTier tier in Enum.GetValues<PowerTier>())
        {
            var tierItem = new NativeMenuItem(_strings["Tier" + tier])
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = tier == displayTier,
                IsEnabled = tierEnabled
            };
            tierItem.Click += (_, _) => _window.SetTierFromTray(tier);
            tierMenu.Items.Add(tierItem);
        }
        menu.Items.Add(new NativeMenuItem(_strings["PowerTier"]) { Menu = tierMenu, IsEnabled = c is not null });

        menu.Items.Add(new NativeMenuItemSeparator());
        AddAction(_strings["Quit"], _window.QuitAsync);
        _tray.Menu = menu;
    }

    public async Task SetSearchingAsync(bool searching)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (!searching)
            {
                await _inhibitor.DisposeAsync();
                _lastAttempt = default;
            }
            else if (!SleepProtected && DateTimeOffset.UtcNow - _lastAttempt > TimeSpan.FromSeconds(30))
            {
                _lastAttempt = DateTimeOffset.UtcNow;
                await _inhibitor.AcquireAsync();
            }
            Searching = searching;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> CanHideAsync()
    {
        if (_disposed || _tray.NativeMenuExporter is null) return false;
        _trayPresent = !OperatingSystem.IsLinux() || await HasLinuxTrayAsync();
        _lastTrayCheck = DateTimeOffset.UtcNow;
        return _trayPresent;
    }

    public async Task<bool> CanStayHiddenAsync()
    {
        if (DateTimeOffset.UtcNow - _lastTrayCheck < TimeSpan.FromSeconds(2)) return _trayPresent;
        return await CanHideAsync();
    }

    private static async Task<bool> HasLinuxTrayAsync()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/gdbus")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (string arg in new[]
        {
            "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher"
        })
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        bool started = false;
        try
        {
            if (!process.Start()) return false;
            started = true;
            process.BeginErrorReadLine();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            return process.ExitCode == 0 && (await output).Trim() == "(true,)";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException) { return false; }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            await _inhibitor.DisposeAsync();
            _window.LanguageChanged -= UpdateMenu;
            _tray.Dispose();
            Searching = false;
            _disposed = true;
        }
        finally { _gate.Release(); }
    }
}
