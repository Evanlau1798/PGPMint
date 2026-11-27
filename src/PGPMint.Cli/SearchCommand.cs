using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Cli;

internal static class SearchCommand
{
    internal static async Task<int> RunAsync(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> devices, CommandLine options)
    {
        var pattern = KeyIdPattern.Parse(options.Get("pattern")!);
        PowerTier? requestedTier = options.Get("tier") is { } rawTier ? CommandLine.ParseTier(rawTier) : null;
        int? maxTemp = options.Has("max-temp") ? options.Number("max-temp", 0) : null;
        bool keepRunning = options.Has("keep-running");
        int statusIntervalSeconds = options.Number("status-interval", 5);
        bool jsonMode = options.Has("json");
        string? onHitExec = options.Get("on-hit-exec") is { } rawExec
            ? HitHookRunner.ValidateExecutablePath(rawExec)
            : null;

        string directory = Program.DataDirectory(options);
        if (!options.Has("data-dir")) AppDataPaths.PreparePasswordVaultDirectory();
        using var lease = new VaultSession(directory);
        PasswordSeedProtector protector = Program.OpenVaultProtector(lease, options);

        var outGate = new object();
        var pendingHooks = new List<Task>();

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
                coordinator.SetAutoPauseOnHit(!keepRunning);
                if (maxTemp is { } limit)
                {
                    coordinator.SetMaxGpuTemperatureCelsius(limit);
                }

                coordinator.MatchFound += (_, match) =>
                {
                    string evtLine = jsonMode
                        ? JsonSerializer.Serialize(new CliMatchEvent(
                            Event: "match",
                            TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
                            KeyId: match.KeyId,
                            Fingerprint: match.Fingerprint,
                            Mask: match.MatchedPattern,
                            Device: match.DeviceName,
                            FoundAtUtc: match.FoundAtUtc.ToString("O")), CliJsonContext.Default.CliMatchEvent)
                        : $"Found {match.KeyId} ({match.Fingerprint}); encrypted and saved.";

                    lock (outGate)
                    {
                        Console.WriteLine(evtLine);
                        if (onHitExec is not null)
                        {
                            pendingHooks.Add(HitHookRunner.InvokeAsync(onHitExec, match));
                        }
                    }
                };

                await coordinator.StartSearchAsync(pattern);
                bool tierApplied = requestedTier is null;
                var timer = Stopwatch.StartNew();
                var lastStatusTick = Stopwatch.StartNew();
                int duration = options.Number("seconds", int.MaxValue);

                while (!cancelled.IsCancellationRequested && timer.Elapsed.TotalSeconds < duration)
                {
                    if (!tierApplied && coordinator.CurrentState == CoordinatorState.Running)
                    {
                        coordinator.SetPowerTier(requestedTier!.Value);
                        tierApplied = true;
                    }

                    if (coordinator.CurrentState is CoordinatorState.Faulted or CoordinatorState.Idle or CoordinatorState.Paused) break;

                    if (lastStatusTick.Elapsed.TotalSeconds >= statusIntervalSeconds)
                    {
                        lastStatusTick.Restart();
                        EmitStatus(coordinator.LatestTelemetry, jsonMode, outGate);
                    }

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

            Task[] hooksSnapshot;
            lock (outGate)
            {
                hooksSnapshot = [.. pendingHooks];
            }

            if (hooksSnapshot.Length > 0)
            {
                await Task.WhenAll(hooksSnapshot);
            }

            CoordinatorTelemetry telemetry = coordinator.LatestTelemetry;
            string summaryLine = jsonMode
                ? JsonSerializer.Serialize(new CliSummaryEvent(
                    Event: "summary",
                    TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
                    State: telemetry.State.ToString(),
                    SessionChecked: telemetry.SessionChecked,
                    TotalChecked: telemetry.TotalChecked,
                    SavedKeys: telemetry.TotalMatchesCount,
                    Error: telemetry.ErrorMessage), CliJsonContext.Default.CliSummaryEvent)
                : $"Checked {telemetry.SessionChecked}; saved keys {telemetry.TotalMatchesCount}; state {telemetry.State}.";
            lock (outGate)
            {
                Console.WriteLine(summaryLine);
            }

            if (telemetry.ErrorMessage is { } error) { Console.Error.WriteLine(error); return 1; }
            return 0;
        }
    }

    internal static void EmitStatus(CoordinatorTelemetry telemetry, bool jsonMode, object outGate)
    {
        string line;
        if (jsonMode)
        {
            double? mhpw = telemetry.HasPowerSensor && telemetry.TotalPowerWatts > 0.0
                ? telemetry.TotalActualHps / 1e6 / telemetry.TotalPowerWatts
                : null;
            double? etaSec = telemetry.EstimatedMeanWait is { } wait
                ? (wait == TimeSpan.MaxValue ? 365.25 * 86400.0 * 1_000_000.0 : wait.TotalSeconds)
                : null;
            CliStatusDeviceEvent[] devs = telemetry.Devices.Select(d => new CliStatusDeviceEvent(
                d.DeviceKey, d.Name, d.BackendName, d.IsEnabled, d.ActualHps, d.PowerWatts,
                d.TemperatureCelsius, d.IsThermalThrottled, d.IsThermalGuardActive)).ToArray();

            line = JsonSerializer.Serialize(new CliStatusEvent(
                Event: "status",
                TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
                State: telemetry.State.ToString(),
                ActiveTier: telemetry.ActiveTier.ToString(),
                ActualHps: telemetry.TotalActualHps,
                PowerWatts: telemetry.TotalPowerWatts,
                Mhpw: mhpw,
                MaxTemperatureCelsius: telemetry.HasTemperatureSensor ? telemetry.MaxTemperatureCelsius : null,
                IsThermalThrottled: telemetry.IsThermalThrottled,
                IsThermalGuardActive: telemetry.IsThermalGuardActive,
                SessionChecked: telemetry.SessionChecked,
                TotalChecked: telemetry.TotalChecked,
                TotalMatchesCount: telemetry.TotalMatchesCount,
                CumulativeProbability: telemetry.CumulativeProbability,
                EtaSeconds: etaSec,
                Devices: devs), CliJsonContext.Default.CliStatusEvent);
        }
        else
        {
            string rate = FormattingHelpers.FormatHashRateCompact(telemetry.TotalActualHps);
            string pwr = telemetry.HasPowerSensor && telemetry.TotalPowerWatts > 0.0
                ? $" | {FormattingHelpers.FormatWatts(telemetry.TotalPowerWatts)} | {FormattingHelpers.FormatEfficiencyPerWatt(telemetry.TotalActualHps / telemetry.TotalPowerWatts)}"
                : string.Empty;
            string tmp = telemetry.HasTemperatureSensor && telemetry.MaxTemperatureCelsius > 0.0
                ? $" | {FormattingHelpers.FormatTemperatureCelsius(telemetry.MaxTemperatureCelsius)}"
                : string.Empty;
            string guard = telemetry.IsThermalGuardActive ? " [ThermalGuard]" : string.Empty;
            string prob = FormattingHelpers.FormatProbability(telemetry.CumulativeProbability);
            string eta = FormattingHelpers.FormatEta(telemetry.EstimatedMeanWait, isChinese: false);
            line = string.Create(CultureInfo.InvariantCulture,
                $"[{telemetry.State}/{telemetry.ActiveTier}] {rate}{pwr}{tmp}{guard} | checked={telemetry.TotalChecked} | prob={prob} | eta={eta}");

            if (telemetry.Devices.Count > 1)
            {
                var devLines = telemetry.Devices.Where(d => d.IsEnabled).Select(d => $"  - {d.Name}: {FormattingHelpers.FormatDeviceStatusSummary(d, "ThermalGuard", "ThermalThrottle")}");
                line = line + Environment.NewLine + string.Join(Environment.NewLine, devLines);
            }
        }

        lock (outGate)
        {
            Console.WriteLine(line);
        }
    }
}

internal sealed class SelectedBackend(IGpuBackend backend, IReadOnlyList<GpuDeviceInfo> devices) : IGpuBackend, IWorkloadGpuBackend
{
    public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => devices;
    public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => backend.CreateDevice(info);
    public void SetWorkload(KeyIdPattern pattern) => (backend as IWorkloadGpuBackend)?.SetWorkload(pattern);
}
