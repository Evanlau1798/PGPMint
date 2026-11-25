using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class MetalDeviceTests
{
    private static readonly GpuDeviceInfo Info = new("metal:0000000000000001", 1,
        "Test Metal", 0, 0, 0, 0, 0, "test", false, "Metal");

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void DeviceReportsActualPipelineWorkgroupConfiguration(int workgroupSize)
    {
        using var session = new CpuSession { WorkgroupSize = workgroupSize };
        using var device = new MetalDevice(Info, session);
        Assert.Equal(workgroupSize, device.Info.KernelConfiguration!.WorkgroupSize);
        Assert.Equal(4, device.Info.KernelConfiguration.KeysPerWorkItem);
        Assert.Equal("Metal", device.Info.BackendName);
        Assert.Equal(device.KernelVersion, device.Info.KernelVersion);
    }

    [Fact]
    public void EnumerateAllRescansOverflowAndKeepsFullBatchStatistics()
    {
        byte[][] keys = Keys();
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        const uint first = uint.MaxValue - 255;
        var pattern = KeyIdPattern.Parse("0");
        SearchBatchResult result = device.ScanBatch(keys, first, 256, pattern, capacity: 1);
        for (int lane = 0; lane < 4; lane++)
        {
            uint[] expected = Enumerable.Range(0, 256).Select(i => first + (uint)i)
                .Where(ts => pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, result.Hits[lane].Order());
        }
        Assert.Equal(1024L, result.UniqueCandidatesCompleted);
        Assert.Equal(session.Evaluations, result.HashEvaluations);
        Assert.True(result.RescanEvaluations > 0);
        Assert.True(result.HashRangeFullyProcessed);
        Assert.True(result.ResultsComplete);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public void FindOneKeepsFullHashRangeWithoutRescanningAndCountsDuplicateKeysOnce()
    {
        byte[][] keys = Keys();
        keys[3] = (byte[])keys[0].Clone();
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        SearchBatchResult result = device.ScanBatch(keys, 123, 512, KeyIdPattern.Parse("0"),
            resultMode: SearchResultMode.FindOne);
        Assert.Equal(1, result.ReturnedHits);
        Assert.Equal(1536, result.UniqueCandidatesCompleted);
        Assert.Equal(2048L, result.HashEvaluations);
        Assert.Equal(0L, result.RescanEvaluations);
        Assert.Equal(1, session.Dispatches);
        Assert.Equal(1u, session.LastParameters.Capacity);
        Assert.True(result.HashRangeFullyProcessed);
        int lane = Array.FindIndex(result.Hits, hits => hits.Count != 0);
        Assert.EndsWith("0", OpenPgpV4Packet.ComputeKeyId(keys[lane], result.Hits[lane][0]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(16)]
    public void UploadedSchedulesAndPatternPlanAgreeWithSha1Oracle(int suffixLength)
    {
        byte[][] keys = Keys();
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        const uint timestamp = uint.MaxValue;
        string expected = OpenPgpV4Packet.ComputeKeyId(keys[2], timestamp);
        SearchBatchResult result = device.ScanBatch(keys, timestamp, 1,
            KeyIdPattern.Parse(expected[^suffixLength..]));
        Assert.Contains(timestamp, result.Hits[2]);
        Assert.Equal(4L, result.HashEvaluations);
        Assert.Equal(Sha1ScheduleHelper.PhaseConstants, session.LastSchedule![^4..]);
    }

    [Fact]
    public void MutatedPublicKeysReplaceCachedSchedule()
    {
        byte[][] keys = Keys();
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        device.ScanBatch(keys, 10, 1, KeyIdPattern.Parse("0"));
        uint[] before = (uint[])session.LastSchedule!.Clone();
        keys[0][0] ^= 0xFF;
        device.ScanBatch(keys, 10, 1, KeyIdPattern.Parse("0"));
        Assert.NotEqual(before, session.LastSchedule);
        uint[] expected = Sha1ScheduleHelper.ComputeFixedSchedule(keys[0]);
        Assert.Equal(expected, Enumerable.Range(0, expected.Length).Select(i => session.LastSchedule![i * 4]));
    }

    [Fact]
    public void PreCancelledBatchDoesNotDispatch()
    {
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        SearchBatchResult result = device.ScanBatch(Keys(), 1, 16, KeyIdPattern.Parse("0"),
            cancellationToken: new CancellationToken(true));
        Assert.Equal(0, session.Dispatches);
        Assert.Equal(0L, result.HashEvaluations);
        Assert.False(result.HashRangeFullyProcessed);
        Assert.False(result.ResultsComplete);
        Assert.True(result.WasCancelled);
    }

    [Fact]
    public void CancellationAfterOverflowPreservesCompletedRangeAndStopsRescans()
    {
        using var cancellation = new CancellationTokenSource();
        using var session = new CpuSession { AfterDispatch = cancellation.Cancel };
        using var device = new MetalDevice(Info, session);
        SearchBatchResult result = device.ScanBatch(Keys(), 0, 512, KeyIdPattern.Parse("0"),
            capacity: 1, cancellationToken: cancellation.Token);
        Assert.Equal(1, session.Dispatches);
        Assert.Equal(2048L, result.HashEvaluations);
        Assert.Equal(2048, result.UniqueCandidatesCompleted);
        Assert.True(result.HashRangeFullyProcessed);
        Assert.False(result.ResultsComplete);
        Assert.True(result.WasCancelled);
    }

    [Fact]
    public void InvalidInputsNeverReachNativeDispatch()
    {
        using var session = new CpuSession();
        using var device = new MetalDevice(Info, session);
        byte[][] keys = Keys();
        KeyIdPattern pattern = KeyIdPattern.Parse("0");
        Assert.Throws<ArgumentNullException>(() => device.ScanBatch(null!, 0, 1, pattern));
        Assert.Throws<ArgumentNullException>(() => device.ScanBatch(keys, 0, 1, null!));
        Assert.Throws<ArgumentException>(() => device.ScanBatch(keys[..3], 0, 1, pattern));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0, 0, pattern));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, uint.MaxValue, 2, pattern));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0,
            (uint)(device.MaxCandidatesPerBatch / 4 + 1), pattern));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0, 1, pattern, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0, 1, pattern, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0, 1, pattern,
            resultMode: (SearchResultMode)2));
        Assert.Throws<ArgumentException>(() => device.ScanBatch(keys, 0, 1, pattern with { Mask = 0xF0F }));
        keys[1] = new byte[31];
        Assert.Throws<ArgumentException>(() => device.ScanBatch(keys, 0, 1, pattern));
        keys[1] = null!;
        Assert.Throws<ArgumentException>(() => device.ScanBatch(keys, 0, 1, pattern));
        Assert.Equal(0, session.Dispatches);
    }

    [Fact]
    public void DisposeReleasesSessionExactlyOnceAndRejectsFurtherScans()
    {
        var session = new CpuSession();
        var device = new MetalDevice(Info, session);
        device.Dispose();
        device.Dispose();
        Assert.Equal(1, session.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => device.ScanGroup(Keys(), 0, 1, KeyIdPattern.Parse("0")));
    }

    private static byte[][] Keys() => Enumerable.Range(0, 4).Select(lane =>
    {
        byte[] key = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        key[^1] = (byte)lane;
        return key;
    }).ToArray();

    private sealed class CpuSession : IMetalSession
    {
        public int WorkgroupSize { get; init; } = 128;
        public int Dispatches { get; private set; }
        public int DisposeCount { get; private set; }
        public long Evaluations { get; private set; }
        public uint[]? LastSchedule { get; private set; }
        public MetalDispatchParameters LastParameters { get; private set; }
        public Action? AfterDispatch { get; init; }

        public void Dispatch(uint[] schedule, MetalDispatchParameters p, uint[] results)
        {
            LastSchedule = schedule;
            LastParameters = p;
            Dispatches++;
            Evaluations += (long)p.Count * 4;
            Array.Clear(results);
            uint[] fixedSchedule = new uint[Sha1ScheduleHelper.ScheduleRounds];
            for (int lane = 0; lane < 4; lane++)
            {
                for (int round = 0; round < fixedSchedule.Length; round++)
                    fixedSchedule[round] = schedule[round * 4 + lane];
                int index = lane * ((int)p.Capacity + 1);
                for (uint offset = 0; offset < p.Count; offset++)
                {
                    uint ts = p.First + offset;
                    ulong keyId = Sha1ScheduleHelper.ComputeKeyIdFromFixedSchedule(fixedSchedule, ts);
                    uint low = System.Numerics.BitOperations.RotateLeft(unchecked((uint)keyId - 0xC3D2E1F0u), 2);
                    uint high = System.Numerics.BitOperations.RotateLeft(unchecked((uint)(keyId >> 32) - 0x10325476u), 2);
                    if ((low & p.LowMask) != p.LowTarget || (high & p.HighMask) != p.HighTarget) continue;
                    uint slot = p.ResultMode == 1 ? results[index] : results[index]++;
                    if (p.ResultMode == 1) results[index] = 1;
                    if (slot < p.Capacity) results[index + 1 + (int)slot] = ts;
                }
            }
            AfterDispatch?.Invoke();
        }

        public void Dispose() => DisposeCount++;
    }
}
