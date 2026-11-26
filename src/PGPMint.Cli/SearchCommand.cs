using System.Diagnostics;
using System.Runtime.InteropServices;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class SearchCommand
{
    internal static async Task<int> RunAsync(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> devices, CommandLine options)
    {
        var pattern = KeyIdPattern.Parse(options.Get("pattern")!);
        string directory = Program.DataDirectory(options);
        if (!options.Has("data-dir")) AppDataPaths.PreparePasswordVaultDirectory();
        using var lease = new VaultSession(directory);
        PasswordSeedProtector protector = Program.OpenVaultProtector(lease, options);
        using (protector)
        using (var coordinator = new MultiGpuCoordinator(new SelectedBackend(backend, devices), new AppStateStore(directory), new VaultStore(directory, protector)))
        using (var cancelled = new CancellationTokenSource())
        {
            void RequestCancel()
            {
                try { cancelled.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; RequestCancel(); };
            Console.CancelKeyPress += cancel;
            PosixSignalRegistration? sigTerm = null;
            try
            {
                try
                {
                    sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                    {
                        ctx.Cancel = true;
                        RequestCancel();
                    });
                }
                catch (PlatformNotSupportedException)
                {
                }

                coordinator.Initialize();
                coordinator.MatchFound += (_, match) => Console.WriteLine($"Found {match.KeyId} ({match.Fingerprint}); encrypted and saved.");
                await coordinator.StartSearchAsync(pattern);
                var timer = Stopwatch.StartNew();
                int duration = options.Number("seconds", int.MaxValue);
                while (!cancelled.IsCancellationRequested && timer.Elapsed.TotalSeconds < duration)
                {
                    if (coordinator.CurrentState is CoordinatorState.Faulted or CoordinatorState.Idle or CoordinatorState.Paused) break;
                    try { await Task.Delay(250, cancelled.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                await coordinator.StopAsync();
                Console.CancelKeyPress -= cancel;
                sigTerm?.Dispose();
            }
            var telemetry = coordinator.LatestTelemetry;
            Console.WriteLine($"Checked {telemetry.SessionChecked}; saved keys {telemetry.TotalMatchesCount}; state {telemetry.State}.");
            if (telemetry.ErrorMessage is { } error) { Console.Error.WriteLine(error); return 1; }
            return 0;
        }
    }
}

internal sealed class SelectedBackend(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> devices) : IGpuBackend, IWorkloadGpuBackend
{
    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => devices;
    public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => backend.CreateDevice(info);
    public void SetWorkload(KeyIdPattern pattern) => (backend as IWorkloadGpuBackend)?.SetWorkload(pattern);
}
