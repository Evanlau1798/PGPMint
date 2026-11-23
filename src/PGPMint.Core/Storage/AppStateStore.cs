using System.Text.Json;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

public sealed class AppPersistedState
{
    public bool HasCompletedSetup { get; set; }
    public string TargetInput { get; set; } = string.Empty;
    public string LastPatternMask { get; set; } = string.Empty;
    public ulong TotalCheckedCandidates { get; set; }
    public double SliderPosition { get; set; } = 1.0;
    public List<string> EnabledDeviceKeys { get; set; } = new();
    public Dictionary<string, CalibrationRecord> Calibrations { get; set; } = new(StringComparer.Ordinal);
    public string Language { get; set; } = "auto";
    public bool AutoPauseOnHit { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowMiniHudInTray { get; set; } = true;
    public int? MiniHudX { get; set; }
    public int? MiniHudY { get; set; }
    public double TrayFullSpeedHps { get; set; }
    public bool StartWithWindows { get; set; }
    public bool WasRunningOnExit { get; set; }
}

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    public string StateFilePath { get; }

    public AppStateStore(string? dataDirectory = null)
    {
        string dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PGPMint");
        StateFilePath = Path.Combine(dir, "state.json");
    }

    public AppPersistedState Load()
    {
        lock (_gate)
        {
            string raw = AtomicFileStore.ReadTextWithRetry(StateFilePath, "{}");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new AppPersistedState();
            }

            try
            {
                AppPersistedState? parsed = JsonSerializer.Deserialize<AppPersistedState>(raw, JsonOptions);
                if (parsed is null)
                {
                    return new AppPersistedState();
                }

                parsed.EnabledDeviceKeys ??= new List<string>();
                parsed.Calibrations ??= new Dictionary<string, CalibrationRecord>(StringComparer.Ordinal);
                parsed.LastPatternMask ??= string.Empty;
                parsed.SliderPosition = Math.Clamp(parsed.SliderPosition, 0.0, 1.0);
                return parsed;
            }
            catch (JsonException)
            {
                return new AppPersistedState();
            }
        }
    }

    public void Save(AppPersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            string json = JsonSerializer.Serialize(state, JsonOptions);
            AtomicFileStore.WriteTextAtomic(StateFilePath, json);
        }
    }

    public void Update(Action<AppPersistedState> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        lock (_gate)
        {
            AppPersistedState current = Load();
            mutator(current);
            string json = JsonSerializer.Serialize(current, JsonOptions);
            AtomicFileStore.WriteTextAtomic(StateFilePath, json);
        }
    }
}
