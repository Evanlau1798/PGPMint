using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PGPMint.Core.Gpu;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string directory = Environment.GetEnvironmentVariable("PGPMINT_DATA_DIR") is { Length: > 0 } customDir
                ? customDir
                : AppDataPaths.PasswordVaultDirectory;
            var session = new DesktopSession(GpuBackendFactory.CreateDefault(), directory,
                OperatingSystem.IsMacOS() ? new MacKeychainSecretStore() : null);
            var strings = new LocalizationService();
            var window = new MainWindow(session, strings);
            var platform = new PlatformServices(window, session, strings);
            window.AttachPlatform(platform);
            lifetime.MainWindow = window;
            lifetime.ShutdownRequested += async (_, e) =>
            {
                if (window.ReadyToShutdown) return;
                e.Cancel = true;
                await window.QuitAsync();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
