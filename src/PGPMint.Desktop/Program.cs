using Avalonia;

namespace PGPMint.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main() => BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
