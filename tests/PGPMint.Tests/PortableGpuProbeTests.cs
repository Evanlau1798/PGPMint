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

    private sealed class CpuDevice : IGpuSearchDevice
    {
        public bool OmitHits { get; init; }
        public bool NullMetrics { get; init; }
        public int BoundedTimedCalls { get; private set; }
        public int TimedSizeCalls { get; private set; }
        public int OracleCalls { get; private set; }
        public GpuDeviceInfo Info => new("cpu-test", 0, "managed fake", 0, 0, 0, 0, 0, "test", false);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 1 << 24;
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
