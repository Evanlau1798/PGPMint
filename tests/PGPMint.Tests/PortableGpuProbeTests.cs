using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuProbeTests
{
    [Fact]
    public void ProbeVerifiesFullCpuHitSetBefore16WarmupsAnd31TimedDispatches()
    {
        var device = new CpuDevice();
        Assert.True(PortableGpuProbe.Measure(device, KeyIdPattern.Parse("ABCDEF123")) > 0);
        Assert.Equal(47, device.TimedSizeCalls);
        Assert.Equal(47, device.BoundedTimedCalls);
        Assert.True(device.OracleCalls >= 3);
    }

    [Fact]
    public void ProbeRejectsOmittedHitsBeforeMeasuring()
    {
        var device = new CpuDevice { OmitHits = true };
        Assert.Throws<InvalidOperationException>(() => PortableGpuProbe.Measure(device, KeyIdPattern.Parse("0")));
        Assert.Equal(0, device.TimedSizeCalls);
    }

    [Fact]
    public void ProbeRejectsMissingEvaluationMetrics()
    {
        var device = new CpuDevice { NullMetrics = true };
        Assert.Throws<InvalidOperationException>(() => PortableGpuProbe.Measure(device, KeyIdPattern.Parse("0")));
        Assert.Equal(0, device.TimedSizeCalls);
    }

    [Fact]
    public void ProbeScalesUpToComputeBoundCeilingOnFastDevice()
    {
        var device = new CpuDevice { MaxCandidatesOverride = 1L << 30 };
        double rate = PortableGpuProbe.Measure(device, KeyIdPattern.Parse("ABCDEF123"));
        Assert.True(rate > 0);
        Assert.Equal(47, device.TimedSizeCalls);
        Assert.Equal(65_536u, device.DispatchedCounts[0]);
        Assert.All(device.DispatchedCounts.Skip(16), count => Assert.Equal(1u << 25, count));
    }

    [Fact]
    public void ProbeStopsScalingWhenDispatchReachesTwoMilliseconds()
    {
        var device = new CpuDevice { MaxCandidatesOverride = 1L << 30, DelayMs = 3 };
        double rate = PortableGpuProbe.Measure(device, KeyIdPattern.Parse("ABCDEF123"));
        Assert.True(rate > 0);
        Assert.Equal(47, device.TimedSizeCalls);
        Assert.All(device.DispatchedCounts, count => Assert.Equal(65_536u, count));
    }

    [Fact]
    public void ProbeKeepsShortPatternsAtInitialBatchSize()
    {
        var device = new CpuDevice { MaxCandidatesOverride = 1L << 30 };
        double rate = PortableGpuProbe.Measure(device, KeyIdPattern.Parse("ABCD"));
        Assert.True(rate > 0);
        Assert.Equal(47, device.TimedSizeCalls);
        Assert.All(device.DispatchedCounts, count => Assert.Equal(65_536u, count));
    }

    private sealed class CpuDevice : IGpuSearchDevice
    {
        public bool OmitHits { get; init; }
        public bool NullMetrics { get; init; }
        public int DelayMs { get; init; }
        public long? MaxCandidatesOverride { get; init; }
        public int BoundedTimedCalls { get; private set; }
        public int TimedSizeCalls { get; private set; }
        public int OracleCalls { get; private set; }
        public List<uint> DispatchedCounts { get; } = [];
        public GpuDeviceInfo Info => new("cpu-test", 0, "managed fake", 0, 0, 0, 0, 0, "test", false);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => MaxCandidatesOverride ?? (1 << 24);
        public string KernelVersion => "test";
        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> keys, uint first, uint count,
            KeyIdPattern pattern, int capacity = 1024) => ScanBatch(keys, first, count, pattern, capacity).Hits;
        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> keys, uint first, uint count,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return SearchBatchResult.CancelledBeforeDispatch(4);
            var hits = SearchBatchResult.EmptyHits(4);
            if (count > 128)
            {
                TimedSizeCalls++;
                DispatchedCounts.Add(count);
                if (DelayMs > 0) Thread.Sleep(DelayMs);
                if (capacity == 1 && resultMode == SearchResultMode.FindOne) BoundedTimedCalls++;
            }
            else
            {
                OracleCalls++;
                if (!OmitHits)
                    for (int lane = 0; lane < 4; lane++)
                        hits[lane] = Enumerable.Range(0, (int)count).Select(i => first + (uint)i)
                            .Where(ts => pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).ToArray();
            }
            return new(hits, (long)count * 4, NullMetrics ? null : (long)count * 4);
        }
        public void Dispose() { }
    }
}
