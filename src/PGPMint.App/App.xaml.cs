using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PGPMint.App.Services;
using PGPMint.App.ViewModels;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.App;

public partial class App : Application
{
    private readonly SingleInstanceGuard _singleInstance;
    private readonly bool _startMinimized;

    private AppStateStore? _stateStore;
    private DpapiVaultStore? _vaultStore;
    private MultiGpuCoordinator? _coordinator;
    private PowerKeepAliveService? _powerKeepAlive;
    private TrayIconService? _trayIcon;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private bool _isQuitting;

    public App(SingleInstanceGuard singleInstance, bool startMinimized)
    {
        _singleInstance = singleInstance;
        _startMinimized = startMinimized;
        UnhandledException += OnAppUnhandledException;
        InitializeComponent();
    }

    private static void OnAppUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTimeOffset.Now:O}] UnhandledException: {e.Message}\n{e.Exception}\n\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

        _stateStore = new AppStateStore();
        _vaultStore = new DpapiVaultStore();
        _coordinator = new MultiGpuCoordinator(new HybridGpuBackend(), _stateStore, _vaultStore);
        _powerKeepAlive = new PowerKeepAliveService();
        _trayIcon = new TrayIconService();
        var localization = new LocalizationService();

        _viewModel = new MainViewModel(
            _coordinator,
            _stateStore,
            _vaultStore,
            localization,
            _powerKeepAlive,
            _trayIcon,
            dispatcher);

        _mainWindow = new MainWindow(_viewModel, _trayIcon, RequestQuitAsync, OpenOrFocusSettingsWindow);

        _trayIcon.OpenRequested += (_, _) => dispatcher.TryEnqueue(() => _mainWindow?.ShowAndActivate());
        _trayIcon.ToggleSearchRequested += (_, _) => dispatcher.TryEnqueue(async () =>
        {
            try
            {
                if (_viewModel is not null)
                {
                    await _viewModel.PrimaryActionAsync();
                }
            }
            catch (Exception ex)
            {
                _viewModel?.ReportError(ex.Message);
            }
        });
        _trayIcon.QuitRequested += (_, _) => dispatcher.TryEnqueue(async () => await RequestQuitAsync());

        _singleInstance.ActivationRequested += (_, _) => dispatcher.TryEnqueue(() => _mainWindow?.ShowAndActivate());
        _singleInstance.StartListeningForActivations();

        if (_startMinimized)
        {
            _mainWindow.HideToTray();
        }
        else
        {
            _mainWindow.Activate();
        }

        _ = InitializeViewModelSafeAsync();
    }

    private void OpenOrFocusSettingsWindow()
    {
        if (_viewModel is null || _trayIcon is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.FocusWindow();
            return;
        }

        var win = new SettingsWindow(_viewModel, _trayIcon);
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, win))
            {
                _settingsWindow = null;
            }
        };
        _settingsWindow = win;
        win.Activate();
    }

    private async Task InitializeViewModelSafeAsync()
    {
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.InitializeAsync(_startMinimized);
            }
        }
        catch (Exception ex)
        {
            _viewModel?.ReportError(ex.Message);
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"[{DateTimeOffset.Now:O}] InitError: {ex}\n\n");
            }
            catch { }
        }
    }

    private async Task RequestQuitAsync()
    {
        if (_isQuitting)
        {
            return;
        }

        _isQuitting = true;
        try
        {
            if (_viewModel is not null)
            {
                await _viewModel.PrepareForQuitAsync();
                _viewModel.Dispose();
            }
        }
        catch
        {
            // Continue orderly shutdown
        }
        finally
        {
            _coordinator?.Dispose();
            _powerKeepAlive?.Dispose();
            try { _settingsWindow?.Close(); } catch { }
            _settingsWindow = null;
            _mainWindow?.ForceClose();
            _trayIcon?.Dispose();
            Exit();
        }
    }
}
