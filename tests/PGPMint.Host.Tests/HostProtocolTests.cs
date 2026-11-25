using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using PGPMint.Host;
using Xunit;

namespace PGPMint.Host.Tests;

public sealed class HostProtocolTests
{
    [Fact]
    public async Task AutomaticUnlockAndLifecycleRoundtripEmitsEventsAndProtectsSecrets()
    {
        using var temp = new TempDirectory();
        var secrets = new InMemorySecretStore();
        var backend = new DeterministicHitBackend(matchOnFirstBatch: true);
        var options = new CoordinatorOptions
        {
            FullCalibrationDuration = TimeSpan.FromMilliseconds(200),
            CalibrationWindow = TimeSpan.FromMilliseconds(50),
            TelemetryInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var session = new HostSession(backend, temp.Path, secrets, options);
        await using var harness = new ServerHarness(session);

        JsonDocument status0 = await harness.SendAsync("1", "getStatus");
        Assert.True(status0.RootElement.GetProperty("ok").GetBoolean());
        JsonElement r0 = status0.RootElement.GetProperty("result");
        Assert.False(r0.GetProperty("isUnlocked").GetBoolean());
        Assert.True(r0.GetProperty("supportsAutomaticUnlock").GetBoolean());

        JsonDocument unlock = await harness.SendAsync("2", "unlockAutomatic");
        Assert.True(unlock.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(unlock.RootElement.GetProperty("result").GetProperty("unlocked").GetBoolean());
        Assert.True(unlock.RootElement.GetProperty("result").GetProperty("status").GetProperty("isUnlocked").GetBoolean());

        JsonDocument settings = await harness.SendAsync("3", "applySettings", new
        {
            targetInput = "A",
            language = "zh-Hant",
            autoPauseOnHit = true,
            minimizeToTrayOnClose = true,
            hasCompletedSetup = true
        });
        Assert.True(settings.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("A", settings.RootElement.GetProperty("result").GetProperty("settings").GetProperty("targetInput").GetString());

        JsonDocument start = await harness.SendAsync("4", "startSearch", new { targetInput = "A" });
        Assert.True(start.RootElement.GetProperty("ok").GetBoolean());

        JsonDocument matchEvent = await harness.WaitForEventAsync("matchFound", TimeSpan.FromSeconds(5));
        string rawEvent = matchEvent.RootElement.GetRawText();
        Assert.DoesNotContain("protectedPrivateSeed", rawEvent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateSeed", rawEvent, StringComparison.OrdinalIgnoreCase);
        string recordId = matchEvent.RootElement.GetProperty("payload").GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(recordId));

        JsonDocument stop = await harness.SendAsync("5", "stop");
        Assert.True(stop.RootElement.GetProperty("ok").GetBoolean());

        string privatePath = Path.Combine(temp.Path, "exported.secret.asc");
        string publicPath = Path.Combine(temp.Path, "exported.public.asc");
        JsonDocument exported = await harness.SendAsync("6", "exportMatch", new
        {
            recordId,
            uid = "Test User",
            email = "test@example.com",
            passphrase = "strong-export-passphrase",
            privatePath,
            publicPath
        });
        Assert.True(exported.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("BEGIN PGP PRIVATE KEY BLOCK", File.ReadAllText(privatePath));
        Assert.Contains("BEGIN PGP PUBLIC KEY BLOCK", File.ReadAllText(publicPath));

        // Refuses overwrite on existing export path
        JsonDocument overwriteAttempt = await harness.SendAsync("7", "exportMatch", new
        {
            recordId,
            uid = "Test User",
            email = "test@example.com",
            passphrase = "strong-export-passphrase",
            privatePath,
            publicPath
        });
        Assert.False(overwriteAttempt.RootElement.GetProperty("ok").GetBoolean());

        JsonDocument deleted = await harness.SendAsync("8", "deleteMatch", new { recordId });
        Assert.True(deleted.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(deleted.RootElement.GetProperty("result").GetProperty("deleted").GetBoolean());

        JsonDocument shutdown = await harness.SendAsync("9", "shutdown");
        Assert.True(shutdown.RootElement.GetProperty("ok").GetBoolean());
        await harness.WaitExitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task PasswordUnlockRejectsBadPasswordAndAllowsValidReopen()
    {
        using var temp = new TempDirectory();
        var backend = new DeterministicHitBackend(matchOnFirstBatch: false);
        await using (var session = new HostSession(backend, temp.Path))
        await using (var harness = new ServerHarness(session))
        {
            JsonDocument empty = await harness.SendAsync("1", "unlockPassword", new { password = "   " });
            Assert.False(empty.RootElement.GetProperty("ok").GetBoolean());

            JsonDocument ok = await harness.SendAsync("2", "unlockPassword", new { password = "correct-horse-battery" });
            Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("password", ok.RootElement.GetProperty("result").GetProperty("vaultMode").GetString());

            await harness.SendAsync("3", "lock");
            JsonDocument wrong = await harness.SendAsync("4", "unlockPassword", new { password = "wrong-password" });
            Assert.False(wrong.RootElement.GetProperty("ok").GetBoolean());
            await harness.SendAsync("5", "shutdown");
            await harness.WaitExitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StdinEofStopsActiveSearchAndReleasesVaultLock()
    {
        using var temp = new TempDirectory();
        var secrets = new InMemorySecretStore();
        var backend = new DeterministicHitBackend(matchOnFirstBatch: false);
        var options = new CoordinatorOptions
        {
            FullCalibrationDuration = TimeSpan.FromSeconds(5),
            TelemetryInterval = TimeSpan.FromMilliseconds(50)
        };

        await using (var session = new HostSession(backend, temp.Path, secrets, options))
        {
            await using var harness = new ServerHarness(session);
            await harness.SendAsync("1", "unlockAutomatic");
            await harness.SendAsync("2", "applySettings", new { targetInput = "DEADBEEF", hasCompletedSetup = true });
            JsonDocument start = await harness.SendAsync("3", "startSearch", new { targetInput = "DEADBEEF" });
            Assert.True(start.RootElement.GetProperty("ok").GetBoolean());

            // Simulate parent app crash by closing stdin without sending shutdown
            harness.CloseInput();
            await harness.WaitExitAsync(TimeSpan.FromSeconds(5));
        }

        // Vault lock must be completely released so a fresh session can immediately open the same directory
        using var reopened = new VaultSession(temp.Path);
        Assert.Equal(Path.GetFullPath(temp.Path), reopened.DirectoryPath);
    }

    [Fact]
    public async Task MalformedJsonAndUnknownMethodReturnStructuredErrorsWithoutCrashing()
    {
        using var temp = new TempDirectory();
        var backend = new DeterministicHitBackend(matchOnFirstBatch: false);
        await using var session = new HostSession(backend, temp.Path);
        await using var harness = new ServerHarness(session);

        await harness.SendRawAsync("{broken-json");
        JsonDocument parseErr = await harness.ReadNextResponseAsync(TimeSpan.FromSeconds(3));
        Assert.False(parseErr.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("invalid_request", parseErr.RootElement.GetProperty("errorCode").GetString());

        JsonDocument unknown = await harness.SendAsync("42", "noSuchMethod");
        Assert.False(unknown.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown_method", unknown.RootElement.GetProperty("errorCode").GetString());

        JsonDocument stillAlive = await harness.SendAsync("43", "getStatus");
        Assert.True(stillAlive.RootElement.GetProperty("ok").GetBoolean());
        await harness.SendAsync("44", "shutdown");
        await harness.WaitExitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class InMemorySecretStore : IVaultSecretStore
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);
        public string Kind => "test-keychain";
        public char[]? Read(string account) => _items.TryGetValue(account, out string? v) ? v.ToCharArray() : null;
        public void Create(string account, ReadOnlySpan<char> secret) => _items[account] = new string(secret);
        public void Delete(string account) => _items.Remove(account);
    }

    private sealed class DeterministicHitBackend(bool matchOnFirstBatch) : IGpuBackend
    {
        private static readonly GpuDeviceInfo FakeGpu = new(
            "vulkan:fake-0", 1, "Fake Test GPU", 0x10DE, 1, 0, 0, 8UL << 30, "1.0", true, "Vulkan", "vulkan-test-v1");

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [FakeGpu];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => new FakeDevice(deviceInfo, matchOnFirstBatch);

        private sealed class FakeDevice(GpuDeviceInfo info, bool matchOnFirstBatch) : IGpuSearchDevice
        {
            private bool _matched;
            public GpuDeviceInfo Info => info;
            public int KeysPerBatch => 4;
            public long MaxCandidatesPerBatch => 4096;
            public string KernelVersion => info.KernelVersion;

            public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024)
            {
                IReadOnlyList<uint>[] hits = Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), publicKeys.Count).ToArray();
                if (matchOnFirstBatch && !_matched && publicKeys.Count > 0)
                {
                    uint limit = Math.Min(timestampCount, 2048u);
                    for (uint i = 0; i < limit; i++)
                    {
                        uint ts = startTimestamp + i;
                        ulong keyId = OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[0], ts);
                        if (pattern.Matches(keyId))
                        {
                            hits[0] = [ts];
                            _matched = true;
                            break;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(15);
                }
                return hits;
            }

            public SearchBatchResult ScanBatch(
                IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern,
                int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll, CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new SearchBatchResult(Enumerable.Repeat<IReadOnlyList<uint>>(Array.Empty<uint>(), publicKeys.Count).ToArray(), 0, 0) { WasCancelled = true };
                IReadOnlyList<uint>[] hits = ScanGroup(publicKeys, startTimestamp, timestampCount, pattern, capacity);
                long count = (long)publicKeys.Count * timestampCount;
                return new SearchBatchResult(hits, count, count);
            }

            public void Dispose() { }
        }
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly BlockingCollection<string?> _inLines = new();
        private readonly BlockingCollection<string> _outLines = new();
        private readonly Task _serverTask;
        private readonly Queue<JsonDocument> _pendingResponses = new();
        private readonly Queue<JsonDocument> _pendingEvents = new();

        public ServerHarness(HostSession session)
        {
            var reader = new QueueTextReader(_inLines);
            var writer = new QueueTextWriter(_outLines);
            var server = new HostServer(session, reader, writer);
            _serverTask = Task.Run(() => server.RunAsync());
        }

        public Task SendRawAsync(string line)
        {
            _inLines.Add(line);
            return Task.CompletedTask;
        }

        public async Task<JsonDocument> SendAsync(string id, string method, object? payload = null)
        {
            string json = payload is null
                ? JsonSerializer.Serialize(new { id, method })
                : JsonSerializer.Serialize(new { id, method, @params = payload });
            _inLines.Add(json);
            while (true)
            {
                JsonDocument doc = await ReadNextResponseAsync(TimeSpan.FromSeconds(5));
                if (doc.RootElement.TryGetProperty("id", out JsonElement idProp) && idProp.GetString() == id)
                    return doc;
            }
        }

        public Task<JsonDocument> ReadNextResponseAsync(TimeSpan timeout) =>
            Task.Run(() =>
            {
                if (_pendingResponses.Count > 0) return _pendingResponses.Dequeue();
                DateTime deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (_outLines.TryTake(out string? line, TimeSpan.FromMilliseconds(50)) && line is not null)
                    {
                        var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.GetProperty("type").GetString() == "response") return doc;
                        _pendingEvents.Enqueue(doc);
                    }
                }
                throw new TimeoutException("Timed out waiting for host response.");
            });

        public Task<JsonDocument> WaitForEventAsync(string eventName, TimeSpan timeout) =>
            Task.Run(() =>
            {
                while (_pendingEvents.Count > 0)
                {
                    JsonDocument queued = _pendingEvents.Dequeue();
                    if (queued.RootElement.GetProperty("event").GetString() == eventName) return queued;
                }
                DateTime deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    if (_outLines.TryTake(out string? line, TimeSpan.FromMilliseconds(50)) && line is not null)
                    {
                        var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.GetProperty("type").GetString() == "event")
                        {
                            if (doc.RootElement.GetProperty("event").GetString() == eventName) return doc;
                        }
                        else
                        {
                            _pendingResponses.Enqueue(doc);
                        }
                    }
                }
                throw new TimeoutException($"Timed out waiting for event '{eventName}'.");
            });

        public void CloseInput() => _inLines.CompleteAdding();

        public async Task WaitExitAsync(TimeSpan timeout) => await _serverTask.WaitAsync(timeout);

        public async ValueTask DisposeAsync()
        {
            if (!_inLines.IsAddingCompleted) _inLines.CompleteAdding();
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            _inLines.Dispose();
            _outLines.Dispose();
        }
    }

    private sealed class QueueTextReader(BlockingCollection<string?> lines) : TextReader
    {
        public override string? ReadLine() => lines.TryTake(out string? line, Timeout.Infinite) ? line : null;
        public override Task<string?> ReadLineAsync() => Task.Run(ReadLine);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            new(Task.Run(() => lines.TryTake(out string? line, Timeout.Infinite, cancellationToken) ? line : null, cancellationToken));
    }

    private sealed class QueueTextWriter(BlockingCollection<string> lines) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n') { lines.Add(_buffer.ToString().TrimEnd('\r')); _buffer.Clear(); }
            else _buffer.Append(value);
        }
        public override void WriteLine(string? value) => lines.Add(value ?? string.Empty);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PGPMint-HostTest-" + Guid.NewGuid().ToString("N"));
        public TempDirectory() => PrivateFileSystem.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
