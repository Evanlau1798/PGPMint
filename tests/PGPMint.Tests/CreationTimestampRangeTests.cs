using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CreationTimestampRangeTests
{
    [Fact]
    public void ParseAndValidationEnforcesSevenDayMinimumAndRejectsFutureDates()
    {
        long now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        Assert.True(CreationTimestampRange.TryParseInput("2020-01-01", out uint? min2020));
        Assert.True(CreationTimestampRange.TryParseInput("2020-01-08", out uint? max7d));
        Assert.NotNull(min2020);
        Assert.NotNull(max7d);
        Assert.Equal("2020-01-01", CreationTimestampRange.FormatUtcDate(min2020));

        // Exactly 7 days (Inclusive end -> endExclusive is +1s) passes
        CreationTimestampRange.Validate(min2020, max7d, now);

        // 6 days fails
        Assert.True(CreationTimestampRange.TryParseInput("2020-01-07", out uint? max6d));
        Assert.Throws<ArgumentException>(() => CreationTimestampRange.Validate(min2020, max6d, now));

        // Inverted fails
        Assert.Throws<ArgumentException>(() => CreationTimestampRange.Validate(max7d, min2020, now));

        // Future fails
        Assert.True(CreationTimestampRange.TryParseInput("2027-01-01", out uint? future));
        Assert.Throws<ArgumentException>(() => CreationTimestampRange.Validate(min2020, future, now));

        // Raw Unix & invalid strings
        Assert.False(CreationTimestampRange.TryParseInput("100", out _));
        Assert.False(CreationTimestampRange.TryParseInput("not-a-date", out _));
        Assert.True(CreationTimestampRange.TryParseInput("", out uint? empty));
        Assert.Null(empty);
    }

    [Fact]
    public async Task CoordinatorWorkersRespectCustomTimestampRangeAndPersistSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PGPMint-Range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var backend = new CapturingRangeBackend();
            var stateStore = new AppStateStore(dir);
            var vault = new DpapiVaultStore(dir);

            uint customMin = 1_600_000_000u;
            uint customMax = 1_602_000_000u; // ~23 days

            using (var coordinator = new MultiGpuCoordinator(backend, stateStore, vault))
            {
                coordinator.Initialize();
                coordinator.SetCreationTimestampRange(customMin, customMax);
                Assert.Equal(customMin, coordinator.MinCreationTimestamp);
                Assert.Equal(customMax, coordinator.MaxCreationTimestamp);

                await coordinator.StartSearchAsync(KeyIdPattern.Parse("DEADBEEF"));
                await WaitUntilAsync(() => backend.ObservedRanges.Count >= 1, TimeSpan.FromSeconds(5));
                await coordinator.StopAsync();

                foreach ((uint start, uint count) in backend.ObservedRanges)
                {
                    Assert.True(start >= customMin, $"Expected start >= {customMin}, got {start}");
                    Assert.True((ulong)start + count <= (ulong)customMax + 1UL, $"Expected end <= {customMax + 1UL}, got {(ulong)start + count}");
                }
            }

            // Reload validates persisted custom range survives restart
            using (var reopened = new MultiGpuCoordinator(backend, stateStore, vault))
            {
                reopened.Initialize();
                Assert.Equal(customMin, reopened.MinCreationTimestamp);
                Assert.Equal(customMax, reopened.MaxCreationTimestamp);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Timed out waiting for custom range batch dispatch.");
    }

    private sealed class CapturingRangeBackend : IGpuBackend
    {
        private readonly GpuDeviceInfo _info = new("range-mock", 1, "Range Mock GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public List<(uint Start, uint Count)> ObservedRanges { get; } = new();
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [_info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => new CapturingRangeDevice(_info, ObservedRanges);
    }

    private sealed class CapturingRangeDevice(GpuDeviceInfo info, List<(uint Start, uint Count)> observed) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 1;
        public long MaxCandidatesPerBatch => 256;
        public string KernelVersion => Info.KernelVersion;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024) =>
            [Array.Empty<uint>()];

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);

            lock (observed) observed.Add((startTimestamp, timestampCount));
            Thread.Sleep(5);
            return new SearchBatchResult([Array.Empty<uint>()], timestampCount, timestampCount)
            {
                ResultsComplete = true,
                HashRangeFullyProcessed = true
            };
        }

        public void Dispose() { }
    }
}
