using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    private bool _disposed;
    private DateTimeOffset _lastAttempt, _lastTrayCheck;
    private bool _trayPresent;
    public bool Searching { get; private set; }
    public bool SleepProtected => _inhibitor.IsHeld;

    public PlatformServices(MainWindow window, DesktopSession session, LocalizationService strings)
    {
        _window = window;
        _session = session;
        _strings = strings;
        _tray = new TrayIcon { ToolTipText = "PGPMint", Icon = CreateIcon(), IsVisible = true };
        _tray.Clicked += (_, _) => _window.ShowWindow();
        UpdateMenu();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        _window.LanguageChanged += UpdateMenu;
    }

    private void UpdateMenu()
    {
        var menu = _tray.Menu ?? new NativeMenu();
        menu.Items.Clear();
        void Item(string key, Func<Task> action)
        {
            var item = new NativeMenuItem(_strings[key]);
            item.Click += async (_, _) =>
            {
                try { await action(); }
                catch { _window.ShowWindow(); }
            };
            menu.Items.Add(item);
        }
        Item("Open", () => { _window.ShowWindow(); return Task.CompletedTask; });
        Item("Hide", async () => { if (await CanHideAsync()) _window.Hide(); else _window.ShowWindow(); });
        Item("Pause", () => { _session.Coordinator?.Pause(); return Task.CompletedTask; });
        Item("Resume", () => { _session.Coordinator?.Resume(); return Task.CompletedTask; });
        Item("Stop", async () => { if (_session.Coordinator is { } coordinator) await coordinator.StopAsync(); });
        Item("Quit", _window.QuitAsync);
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
        using var process = new Process { StartInfo = new ProcessStartInfo("/usr/bin/gdbus")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (string arg in new[] { "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher" }) process.StartInfo.ArgumentList.Add(arg);
        bool started = false;
        try
        {
            if (!process.Start()) return false;
            started = true;
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync();
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

    private static WindowIcon CreateIcon()
    {
        using var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var pixels = bitmap.Lock())
        {
            int[] data = new int[32 * 32];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    bool key = ((x - 11) * (x - 11) + (y - 11) * (y - 11) < 36) ||
                        (x >= 10 && x <= 14 && y >= 11 && y <= 27) || (y >= 22 && y <= 25 && x >= 11 && x <= 21);
                    data[y * 32 + x] = key ? unchecked((int)0xFFFFFFFF) : unchecked((int)0xFF147D64);
                }
            for (int y = 0; y < 32; y++) Marshal.Copy(data, y * 32, pixels.Address + y * pixels.RowBytes, 32);
        }
        return new WindowIcon(bitmap);
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
