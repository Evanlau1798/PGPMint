using System.Security.Cryptography;
using System.Text.Json;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Host;

public sealed class HostServer(HostSession session, TextReader input, TextWriter output)
{
    private readonly object _writeLock = new();
    private bool _shutdownRequested;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        session.TelemetryEmitted += OnTelemetry;
        session.MatchFoundEmitted += OnMatchFound;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
            {
                string? line;
                try
                {
                    line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    // Parent stdin closed (EOF): immediately stop search and release vault.
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                await HandleLineAsync(line).ConfigureAwait(false);
            }
        }
        finally
        {
            session.TelemetryEmitted -= OnTelemetry;
            session.MatchFoundEmitted -= OnMatchFound;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleLineAsync(string line)
    {
        HostRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, HostJsonContext.Default.HostRequest);
            if (request is null || string.IsNullOrWhiteSpace(request.Method))
            {
                WriteResponse(new HostResponse("response", request?.Id, false, ErrorCode: "invalid_request", Error: "Missing method name."));
                return;
            }
        }
        catch (JsonException ex)
        {
            WriteResponse(new HostResponse("response", null, false, ErrorCode: "invalid_request", Error: ex.Message));
            return;
        }

        try
        {
            object? result = await DispatchAsync(request).ConfigureAwait(false);
            WriteResponse(new HostResponse("response", request.Id, true, Result: result));
        }
        catch (UnknownMethodException ex)
        {
            WriteResponse(new HostResponse("response", request.Id, false, ErrorCode: "unknown_method", Error: ex.Message));
        }
        catch (Exception ex)
        {
            string code = ClassifyError(ex, request.Method);
            WriteResponse(new HostResponse("response", request.Id, false, ErrorCode: code, Error: ex.Message));
        }
    }

    private async Task<object?> DispatchAsync(HostRequest request)
    {
        switch (request.Method)
        {
            case "getStatus":
                return session.GetStatus();

            case "unlockAutomatic":
                return session.UnlockAutomatic();

            case "unlockPassword":
            {
                UnlockPasswordParams p = DeserializeParams(request.Params, HostJsonContext.Default.UnlockPasswordParams);
                char[] chars = (p.Password ?? string.Empty).ToCharArray();
                try { return session.UnlockPassword(chars); }
                finally { Array.Clear(chars); }
            }

            case "lock":
                return await session.LockAsync().ConfigureAwait(false);

            case "applySettings":
            {
                ApplySettingsParams p = DeserializeParams(request.Params, HostJsonContext.Default.ApplySettingsParams);
                return session.ApplySettings(p);
            }

            case "setPowerTier":
            {
                SetPowerTierParams p = DeserializeParams(request.Params, HostJsonContext.Default.SetPowerTierParams);
                if (!Enum.TryParse<PowerTier>(p.Tier, ignoreCase: true, out PowerTier tier) || !Enum.IsDefined(tier))
                    throw new ArgumentException("Invalid power tier.");
                return session.SetPowerTier(tier);
            }

            case "setDeviceEnabled":
            {
                SetDeviceEnabledParams p = DeserializeParams(request.Params, HostJsonContext.Default.SetDeviceEnabledParams);
                return await session.SetDeviceEnabledAsync(p.DeviceKey ?? string.Empty, p.Enabled).ConfigureAwait(false);
            }

            case "startSearch":
            {
                TargetParams? p = request.Params.HasValue
                    ? DeserializeParams(request.Params, HostJsonContext.Default.TargetParams)
                    : null;
                return await session.StartSearchAsync(p?.TargetInput).ConfigureAwait(false);
            }

            case "pause":
                return session.Pause();

            case "resume":
                return session.Resume();

            case "stop":
                return await session.StopAsync().ConfigureAwait(false);

            case "recalibrate":
            {
                TargetParams? p = request.Params.HasValue
                    ? DeserializeParams(request.Params, HostJsonContext.Default.TargetParams)
                    : null;
                return await session.RecalibrateAsync(p?.TargetInput).ConfigureAwait(false);
            }

            case "exportMatch":
            {
                ExportMatchParams p = DeserializeParams(request.Params, HostJsonContext.Default.ExportMatchParams);
                return session.ExportMatch(p);
            }

            case "deleteMatch":
            {
                RecordIdParams p = DeserializeParams(request.Params, HostJsonContext.Default.RecordIdParams);
                return session.DeleteMatch(p.RecordId ?? string.Empty);
            }

            case "clearMatches":
                return session.ClearMatches();

            case "inspectSyncBundle":
            {
                InspectSyncBundleParams p = DeserializeParams(request.Params, HostJsonContext.Default.InspectSyncBundleParams);
                return session.InspectSyncBundle(p.BundlePath ?? string.Empty);
            }

            case "exportSyncBundle":
            {
                ExportSyncBundleParams p = DeserializeParams(request.Params, HostJsonContext.Default.ExportSyncBundleParams);
                return session.ExportSyncBundle(p);
            }

            case "importSyncBundle":
            {
                ImportSyncBundleParams p = DeserializeParams(request.Params, HostJsonContext.Default.ImportSyncBundleParams);
                return session.ImportSyncBundle(p);
            }

            case "shutdown":
            {
                _shutdownRequested = true;
                await session.StopAsync().ConfigureAwait(false);
                return session.GetStatus();
            }

            default:
                throw new UnknownMethodException($"Unknown host method '{request.Method}'.");
        }
    }

    private static T DeserializeParams<T>(JsonElement? element, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Null)
            throw new ArgumentException("Missing request parameters.");
        return element.Value.Deserialize(typeInfo)
            ?? throw new ArgumentException("Invalid request parameters.");
    }

    private string ClassifyError(Exception ex, string method)
    {
        if (method == "unlockAutomatic" && session.Coordinator is null && session.SupportsAutomaticUnlock &&
            (AutomaticVault.IsAutomatic(session.DirectoryPath) || !File.Exists(Path.Combine(session.DirectoryPath, "vault.key.json"))))
        {
            return "system_vault_failed";
        }

        return ex switch
        {
            ArgumentException => "invalid_argument",
            CryptographicException => "invalid_password",
            DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException => "backend_error",
            InvalidDataException => "invalid_data",
            NotSupportedException => "not_supported",
            IOException => "io_error",
            InvalidOperationException => "invalid_state",
            _ => "internal_error"
        };
    }

    private void OnTelemetry(HostTelemetryDto telemetry) =>
        WriteEvent(new HostEvent("event", "telemetry", telemetry));

    private void OnMatchFound(HostMatchDto match) =>
        WriteEvent(new HostEvent("event", "matchFound", match));

    private void WriteResponse(HostResponse response)
    {
        string json = JsonSerializer.Serialize(response, HostJsonContext.Default.HostResponse);
        WriteLine(json);
    }

    private void WriteEvent(HostEvent evt)
    {
        try
        {
            string json = JsonSerializer.Serialize(evt, HostJsonContext.Default.HostEvent);
            WriteLine(json);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Parent disconnected stdout before stdin EOF completed shutdown.
        }
    }

    private void WriteLine(string json)
    {
        lock (_writeLock)
        {
            output.WriteLine(json);
            output.Flush();
        }
    }

    private sealed class UnknownMethodException(string message) : Exception(message);
}
