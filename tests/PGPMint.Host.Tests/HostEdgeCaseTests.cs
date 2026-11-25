using System.Text.Json;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using PGPMint.Host;
using Xunit;

namespace PGPMint.Host.Tests;

public sealed class HostEdgeCaseTests
{
    [Fact]
    public async Task MissingSystemSecretReturnsSystemVaultFailedWithoutOverwritingVault()
    {
        using var temp = new TempDir();
        var secrets = new DictionarySecretStore();
        var backend = new NoHitBackend();

        await using (var s1 = new HostSession(backend, temp.Path, secrets))
        {
            Assert.True(s1.UnlockAutomatic().Unlocked);
        }

        byte[] headerBefore = File.ReadAllBytes(Path.Combine(temp.Path, "vault.key.json"));
        secrets.Clear();

        await using var s2 = new HostSession(backend, temp.Path, secrets);
        var serverIn = new StringReader("{\"id\":\"1\",\"method\":\"unlockAutomatic\"}\n{\"id\":\"2\",\"method\":\"shutdown\"}\n");
        var serverOut = new StringWriter();
        var server = new HostServer(s2, serverIn, serverOut);
        await server.RunAsync();

        string[] lines = serverOut.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.True(lines.Length >= 2);
        using JsonDocument resp = JsonDocument.Parse(lines[0]);
        Assert.False(resp.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("system_vault_failed", resp.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(headerBefore, File.ReadAllBytes(Path.Combine(temp.Path, "vault.key.json")));
    }

    [Fact]
    public async Task RunningSearchRejectsTargetChangeAllowsPauseResumeAndClearMatches()
    {
        using var temp = new TempDir();
        var secrets = new DictionarySecretStore();
        var backend = new NoHitBackend();
        var options = new CoordinatorOptions
        {
            FullCalibrationDuration = TimeSpan.FromMilliseconds(120),
            CalibrationWindow = TimeSpan.FromMilliseconds(40),
            TelemetryInterval = TimeSpan.FromMilliseconds(40)
        };

        await using var session = new HostSession(backend, temp.Path, secrets, options);
        Assert.True(session.UnlockAutomatic().Unlocked);
        session.ApplySettings(new ApplySettingsParams(TargetInput: "CAFE", HasCompletedSetup: true));
        await session.StartSearchAsync("CAFE");

        // Wait until calibration completes and state reaches Running
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && session.Coordinator!.CurrentState != CoordinatorState.Running)
            await Task.Delay(25);
        Assert.Equal(CoordinatorState.Running, session.Coordinator!.CurrentState);

        // Changing target while running must fail
        Assert.Throws<InvalidOperationException>(() =>
            session.ApplySettings(new ApplySettingsParams(TargetInput: "BEEF")));

        // Setting power tier after calibration succeeds
        HostStatusSnapshot eco = session.SetPowerTier(PowerTier.Eco);
        Assert.Equal("Eco", eco.Settings.PowerTier);

        HostStatusSnapshot paused = session.Pause();
        Assert.Equal("Paused", paused.Telemetry!.State);

        HostStatusSnapshot resumed = session.Resume();
        Assert.Equal("Running", resumed.Telemetry!.State);

        HostStatusSnapshot stopped = await session.StopAsync();
        Assert.Equal("Idle", stopped.Telemetry!.State);

        ClearMatchesResult cleared = session.ClearMatches();
        Assert.Equal(0, cleared.Removed);
    }

    [Fact]
    public async Task ExportRejectsEmptyPassphraseOrDuplicatePaths()
    {
        using var temp = new TempDir();
        var secrets = new DictionarySecretStore();
        var backend = new NoHitBackend();
        await using var session = new HostSession(backend, temp.Path, secrets);
        Assert.True(session.UnlockAutomatic().Unlocked);

        string samePath = Path.Combine(temp.Path, "same.asc");
        Assert.Throws<ArgumentException>(() => session.ExportMatch(new ExportMatchParams("id1", "Alice", "", "", samePath, Path.Combine(temp.Path, "pub.asc"))));
        Assert.Throws<ArgumentException>(() => session.ExportMatch(new ExportMatchParams("id1", "", "", "pass", samePath, Path.Combine(temp.Path, "pub.asc"))));
        Assert.Throws<ArgumentException>(() => session.ExportMatch(new ExportMatchParams("id1", "Alice", "", "pass", samePath, samePath)));
    }

    private sealed class DictionarySecretStore : IVaultSecretStore
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
        public string Kind => "test-keychain";
        public char[]? Read(string account) => _map.TryGetValue(account, out string? v) ? v.ToCharArray() : null;
        public void Create(string account, ReadOnlySpan<char> secret) => _map[account] = new string(secret);
        public void Delete(string account) => _map.Remove(account);
        public void Clear() => _map.Clear();
    }

    private sealed class NoHitBackend : IGpuBackend
    {
        private static readonly GpuDeviceInfo FakeGpu = new(
            "vulkan:nohit-0", 1, "NoHit GPU", 0x10DE, 1, 0, 0, 8UL << 30, "1.0", true, "Vulkan", "vulkan-test-v1");

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [FakeGpu];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new Device(info);

        private sealed class Device(GpuDeviceInfo info) : IGpuSearchDevice
        {
            public GpuDeviceInfo Info => info;
            public int KeysPerBatch => 4;
            public long MaxCandidatesPerBatch => 4096;
            public string KernelVersion => info.KernelVersion;
            public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024)
            {
                Thread.Sleep(15);
                return Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), publicKeys.Count).ToArray();
            }
            public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new SearchBatchResult(Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), publicKeys.Count).ToArray(), 0, 0) { WasCancelled = true };
                Thread.Sleep(15);
                long count = (long)publicKeys.Count * timestampCount;
                return new SearchBatchResult(Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), publicKeys.Count).ToArray(), count, count);
            }
            public void Dispose() { }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PGPMint-HostEdge-" + Guid.NewGuid().ToString("N"));
        public TempDir() => PrivateFileSystem.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
