using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PGPMint.Core.Storage;
using WinRT;

namespace PGPMint.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new SingleInstanceGuard();
        if (!singleInstance.IsPrimaryInstance)
        {
            SingleInstanceGuard.SignalPrimaryInstance();
            return;
        }

        bool startMinimized = args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        ComWrappersSupport.InitializeComWrappers();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PGPMint");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"[{DateTimeOffset.Now:O}] DomainUnhandled: {e.ExceptionObject}\n\n");
            }
            catch { }
        };

        Application.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new App(singleInstance, startMinimized);
            _ = app;
        });
    }
}
