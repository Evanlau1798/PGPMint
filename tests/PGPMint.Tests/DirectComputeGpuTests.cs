using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[CollectionDefinition("Hardware GPU", DisableParallelization = true)]
public sealed class HardwareGpuCollection { }

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
public class DirectComputeGpuTests
{
    private const string Rfc8032PublicHex = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const uint Rfc8032Timestamp = 1700000000u;
    private readonly ITestOutputHelper _output;

    public DirectComputeGpuTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HybridBackendEnumeratesHardwareGpusAndExcludesSoftwareAndVirtualAdapters()
    {
        var backend = new HybridGpuBackend();
        IReadOnlyList<GpuDeviceInfo> gpus = backend.EnumerateHardwareGpus();

        Assert.NotEmpty(gpus);
        foreach (var g in gpus)
        {
            _output.WriteLine($"GPU: Key={g.DeviceKey} Backend={g.BackendName} Kernel={g.KernelVersion} LUID=0x{g.AdapterLuid:X16} Name={g.Name} VRAM={g.DedicatedVideoMemoryBytes} Driver={g.DriverVersion}");
        }

        Assert.Equal(gpus.Select(g => g.AdapterLuid).Distinct().Count(), gpus.Count);
        Assert.DoesNotContain(gpus, g => g.Name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(gpus, g => g.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(gpus, g => g.Name.Contains("SuperDisplay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HybridAndDirectComputeKernelsFindRfc8032VectorAndHandleOverflowAndMaxTimestamp()
    {
        var backends = new IGpuBackend[] { new HybridGpuBackend(), new DirectComputeBackend() };
        byte[] basePub = Convert.FromHexString(Rfc8032PublicHex);

        foreach (IGpuBackend backend in backends)
        {
            GpuDeviceInfo gpuInfo = backend.EnumerateHardwareGpus()[0];
            using IGpuSearchDevice device = backend.CreateDevice(gpuInfo);
            _output.WriteLine($"GPU: {device.Info.Name}, backend={device.Info.BackendName}, driver={device.Info.DriverVersion}, kernel={device.KernelVersion}");

            // 1. RFC 8032 13-char suffix hit
            byte[][] rfcKeys = Enumerable.Range(0, 4).Select(i =>
            {
                byte[] copy = (byte[])basePub.Clone();
                copy[^1] = i == 2 ? basePub[^1] : (byte)(basePub[^1] ^ (i + 1));
                return copy;
            }).ToArray();

            string expectedKeyId = OpenPgpFingerprintOracle.Compute(rfcKeys[2], Rfc8032Timestamp)[^16..];
            var suffixPattern = KeyIdPattern.Parse(expectedKeyId[^13..]);
            var suffixHits = device.ScanGroup(rfcKeys, Rfc8032Timestamp - 500, 1024, suffixPattern);
            Assert.Contains(Rfc8032Timestamp, suffixHits[2]);

            // 2. Overflow recursive halving + 0xFFFFFFFF max timestamp boundary
            byte[][] boundaryKeys = Enumerable.Range(0, 4).Select(i =>
            {
                byte[] copy = (byte[])basePub.Clone();
                copy[^1] = (byte)i;
                return copy;
            }).ToArray();

            const uint start = uint.MaxValue - 1030;
            const uint count = 1031;
            const int capacity = 3;
            const string boundaryKeyId = "5A89F6783F33E405";
            const string zeroHitSuffix = "0000000000000000";
            Assert.Equal(boundaryKeyId, OpenPgpFingerprintOracle.Compute(boundaryKeys[3], uint.MaxValue)[^16..]);
            int[] denseCounts = [71, 66, 71, 53];
            string[] suffixes = ["0", boundaryKeyId[^4..], boundaryKeyId[^8..], boundaryKeyId[^9..],
                boundaryKeyId[^12..], boundaryKeyId, zeroHitSuffix];
            foreach (string suffix in suffixes)
            {
                SearchBatchResult batch = device.ScanBatch(boundaryKeys, start, count, KeyIdPattern.Parse(suffix), capacity);
                Assert.Equal(4_124L, batch.UniqueCandidatesCompleted);
                Assert.Equal(suffix.Length == 1 ? 26_408L : 4_124L, batch.HashEvaluations);
                Assert.Equal(suffix.Length == 1 ? 22_284L : 0L, batch.RescanEvaluations);
                Assert.Equal(suffix.Length == 1 ? 261L : suffix == zeroHitSuffix ? 0L : 1L, batch.ReturnedHits);
                for (int lane = 0; lane < boundaryKeys.Length; lane++)
                {
                    var expected = new List<uint>();
                    for (uint offset = 0; offset < count; offset++)
                    {
                        uint timestamp = start + offset;
                        if (OpenPgpFingerprintOracle.Compute(boundaryKeys[lane], timestamp).EndsWith(suffix, StringComparison.Ordinal))
                        {
                            expected.Add(timestamp);
                        }
                    }

                    if (suffix.Length == 1)
                    {
                        Assert.Equal(denseCounts[lane], expected.Count);
                    }
                    else if (lane == 3 && suffix != zeroHitSuffix)
                    {
                        Assert.Equal(new[] { uint.MaxValue }, expected);
                    }
                    else
                    {
                        Assert.Empty(expected);
                    }
                    Assert.Equal(expected, batch.Hits[lane].Order());
                }
            }
        }
    }

    [Fact]
    public void FindOneReturnsOneValidHitWithoutOverflowRescans()
    {
        var backends = new IGpuBackend[] { new HybridGpuBackend(), new DirectComputeBackend() };
        byte[][] keys = Enumerable.Range(0, 4).Select(lane =>
        {
            byte[] key = Convert.FromHexString(Rfc8032PublicHex);
            key[^1] = (byte)lane;
            return key;
        }).ToArray();

        foreach (IGpuBackend backend in backends)
        {
            using IGpuSearchDevice device = backend.CreateDevice(backend.EnumerateHardwareGpus()[0]);
            _output.WriteLine($"GPU: {device.Info.Name}, backend={device.Info.BackendName}, kernel={device.KernelVersion}");
            const uint start = uint.MaxValue - 1030;
            const uint count = 1031;
            var dense = KeyIdPattern.Parse("0");
            SearchBatchResult hit = device.ScanBatch(keys, start, count, dense, capacity: 3,
                resultMode: SearchResultMode.FindOne);

            Assert.Equal(1L, hit.ReturnedHits);
            Assert.Equal(4_124L, hit.UniqueCandidatesCompleted);
            Assert.Equal(4_124L, hit.HashEvaluations);
            Assert.True(hit.HashRangeFullyProcessed);
            Assert.True(hit.ResultsComplete);
            Assert.False(hit.WasCancelled);
            int lane = Enumerable.Range(0, keys.Length).Single(i => hit.Hits[i].Count == 1);
            uint timestamp = Assert.Single(hit.Hits[lane]);
            Assert.InRange(timestamp, start, uint.MaxValue);
            Assert.EndsWith("0", OpenPgpFingerprintOracle.Compute(keys[lane], timestamp));

            SearchBatchResult none = device.ScanBatch(keys, start, count, KeyIdPattern.Parse("0000000000000000"),
                resultMode: SearchResultMode.FindOne);
            Assert.Equal(0L, none.ReturnedHits);
            Assert.Equal(4_124L, none.HashEvaluations);
            Assert.True(none.ResultsComplete);

            SearchBatchResult boundary = device.ScanBatch(keys, start, count, KeyIdPattern.Parse("5A89F6783F33E405"),
                resultMode: SearchResultMode.FindOne);
            Assert.Equal(1L, boundary.ReturnedHits);
            Assert.Equal(uint.MaxValue, Assert.Single(boundary.Hits[3]));
        }
    }

    [Fact]
    public void CancelledScanReturnsBeforeAnyDispatch()
    {
        var backend = new HybridGpuBackend();
        using IGpuSearchDevice device = backend.CreateDevice(backend.EnumerateHardwareGpus()[0]);
        byte[][] keys = Enumerable.Range(0, 4).Select(_ => Convert.FromHexString(Rfc8032PublicHex)).ToArray();
        SearchBatchResult batch = device.ScanBatch(keys, 1, 128, KeyIdPattern.Parse("0"),
            cancellationToken: new CancellationToken(canceled: true));

        Assert.Equal(0L, batch.UniqueCandidatesCompleted);
        Assert.Equal(0L, batch.HashEvaluations);
        Assert.Equal(0L, batch.ReturnedHits);
        Assert.False(batch.HashRangeFullyProcessed);
        Assert.False(batch.ResultsComplete);
        Assert.True(batch.WasCancelled);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0000000000000000", false)]
    [InlineData("0", true)]
    [InlineData("0000000000000000", true)]
    public async Task CoordinatorStopsHardwareSearchAtBatchBoundary(string suffix, bool forceDirectCompute)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-gpu-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            IGpuBackend backend = forceDirectCompute ? new DirectComputeBackend() : new HybridGpuBackend();
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory),
                new DpapiVaultStore(directory), new CoordinatorOptions { TelemetryInterval = TimeSpan.FromMilliseconds(20) });
            coordinator.Initialize();
            coordinator.SetAutoPauseOnHit(false);
            var firstProgress = new TaskCompletionSource<CoordinatorTelemetry>(TaskCreationOptions.RunContinuationsAsynchronously);
            coordinator.TelemetryUpdated += (_, telemetry) =>
            {
                if (telemetry.SessionWork.UniqueCandidatesCompleted != 0)
                {
                    firstProgress.TrySetResult(telemetry);
                }
            };

            await coordinator.StartSearchAsync(KeyIdPattern.Parse(suffix));
            CoordinatorTelemetry beforeStop = await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long stopStarted = Stopwatch.GetTimestamp();
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(stopStarted);

            CoordinatorTelemetry final = coordinator.LatestTelemetry;
            Assert.Equal(CoordinatorState.Idle, final.State);
            Assert.True(final.SessionWork.UniqueCandidatesCompleted >= beforeStop.SessionWork.UniqueCandidatesCompleted);
            Assert.Equal(0L, final.SessionWork.RescanEvaluations);
            GpuDeviceInfo device = coordinator.GetEnabledDevices()[0];
            _output.WriteLine($"GPU: {device.Name}, backend={device.BackendName}, kernel={device.KernelVersion}, suffix={suffix.Length} hex, StopAsync to cleanup={elapsed.TotalMilliseconds:F2} ms");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void AllSuffixClassesMatchTheIndependentOracleAfterLaneReordering()
    {
        byte[] first = Convert.FromHexString(Rfc8032PublicHex);
        byte[] second = (byte[])first.Clone();
        second[^1] = 1;
        byte[] third = (byte[])first.Clone();
        third[^1] = 3;
        byte[][] keys = [third, second, third, first];
        const uint start = uint.MaxValue - 132;
        const uint count = 133;
        string keyId = OpenPgpFingerprintOracle.Compute(third, uint.MaxValue)[^16..];

        foreach (IGpuBackend backend in new IGpuBackend[] { new HybridGpuBackend(), new DirectComputeBackend() })
        {
            using IGpuSearchDevice device = backend.CreateDevice(backend.EnumerateHardwareGpus()[0]);
            _output.WriteLine($"GPU: {device.Info.Name}, backend={device.Info.BackendName}, kernel={device.KernelVersion}");
            for (int length = 1; length <= 16; length++)
            {
                string suffix = keyId[^length..];
                KeyIdPattern pattern = KeyIdPattern.Parse(suffix);
                SearchBatchResult all = device.ScanBatch(keys, start, count, pattern, capacity: 2);
                SearchBatchResult one = device.ScanBatch(keys, start, count, pattern, resultMode: SearchResultMode.FindOne);
                Assert.Equal((long)count * 3, all.UniqueCandidatesCompleted);
                Assert.Equal((long)count * 3, one.UniqueCandidatesCompleted);
                Assert.Equal((long)count * 4, one.HashEvaluations);
                Assert.Equal(0L, one.RescanEvaluations);
                Assert.Equal(1L, one.ReturnedHits);

                for (int lane = 0; lane < keys.Length; lane++)
                {
                    uint[] expected = Enumerable.Range(0, (int)count).Select(offset => start + (uint)offset)
                        .Where(timestamp => OpenPgpFingerprintOracle.Compute(keys[lane], timestamp).EndsWith(suffix, StringComparison.Ordinal))
                        .ToArray();
                    Assert.Equal(expected, all.Hits[lane].Order());
                    Assert.All(one.Hits[lane], timestamp => Assert.Contains(timestamp, expected));
                }

                Assert.Contains(uint.MaxValue, all.Hits[0]);
                Assert.Contains(uint.MaxValue, all.Hits[2]);

                if (length is 8 or 9 or 15 or 16)
                {
                    ulong fingerprintId = Convert.ToUInt64(keyId, 16);
                    ulong flipped = fingerprintId ^ (1UL << (length > 8 ? 32 : 0));
                    string otherSuffix = flipped.ToString("X16")[^length..];
                    SearchBatchResult other = device.ScanBatch(keys, start, count, KeyIdPattern.Parse(otherSuffix));
                    Assert.DoesNotContain(uint.MaxValue, other.Hits[0]);
                    for (int lane = 0; lane < keys.Length; lane++)
                    {
                        uint[] expected = Enumerable.Range(0, (int)count).Select(offset => start + (uint)offset)
                            .Where(timestamp => OpenPgpFingerprintOracle.Compute(keys[lane], timestamp).EndsWith(otherSuffix, StringComparison.Ordinal))
                            .ToArray();
                        Assert.Equal(expected, other.Hits[lane].Order());
                    }
                }
            }
        }
    }

    [Fact]
    public void TimestampAndDispatchBoundariesMatchTheCpuOracle()
    {
        byte[][] keys = Enumerable.Range(0, 4).Select(lane =>
        {
            byte[] key = Convert.FromHexString(Rfc8032PublicHex);
            key[^1] = (byte)lane;
            return key;
        }).ToArray();
        uint[] counts = [1, 2, 3, 127, 128, 129, 255, 256, 257, 4095, 4096, 4097];
        (uint Start, uint Count)[] boundaries = [(0u, 1u), (1u, 1u), (uint.MaxValue, 1u), (0xFFFFFFF0u, 16u)];

        foreach (IGpuBackend backend in new IGpuBackend[] { new HybridGpuBackend(), new DirectComputeBackend() })
        {
            using IGpuSearchDevice device = backend.CreateDevice(backend.EnumerateHardwareGpus()[0]);
            foreach (var (start, count) in boundaries.Concat(counts.Select(count => (0x80000000u - count / 2, count))))
            {
                uint last = start + (count - 1);
                string suffix = OpenPgpFingerprintOracle.Compute(keys[0], last)[^16..];
                SearchBatchResult result = device.ScanBatch(keys, start, count, KeyIdPattern.Parse(suffix), capacity: 1);
                Assert.Equal((long)count * keys.Length, result.UniqueCandidatesCompleted);
                Assert.Equal((long)count * keys.Length, result.HashEvaluations);
                Assert.Equal(last, Assert.Single(result.Hits[0]));

                for (int lane = 0; lane < keys.Length; lane++)
                {
                    uint[] expected = Enumerable.Range(0, (int)count).Select(offset => start + (uint)offset)
                        .Where(timestamp => OpenPgpFingerprintOracle.Compute(keys[lane], timestamp).EndsWith(suffix, StringComparison.Ordinal))
                        .ToArray();
                    Assert.Equal(expected, result.Hits[lane].Order());
                }
            }

            var pattern = KeyIdPattern.Parse("0");
            Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, 0, 0, pattern));
            Assert.Throws<ArgumentOutOfRangeException>(() => device.ScanBatch(keys, uint.MaxValue, 2, pattern));
        }
    }

    [Fact]
    public void HybridLiveGpuSustainedThroughputMeetsHighPerformanceBaseline()
    {
        var backend = new HybridGpuBackend();
        GpuDeviceInfo gpuInfo = backend.EnumerateHardwareGpus()[0];
        using IGpuSearchDevice device = backend.CreateDevice(gpuInfo);

        byte[][] keys = Enumerable.Range(0, 4).Select(_ => Ed25519KeyGenerator.Generate().PublicKey).ToArray();
        var pattern = KeyIdPattern.Parse("1145141919810");
        uint batchStamps = (uint)(device.MaxCandidatesPerBatch / 4);

        // Warmup
        device.ScanGroup(keys, 1u, batchStamps, pattern);

        long startTs = Stopwatch.GetTimestamp();
        long totalCandidates = 0;
        uint cursor = 1u;
        while (Stopwatch.GetElapsedTime(startTs).TotalSeconds < 2.5)
        {
            if ((ulong)cursor + batchStamps > (1UL << 32))
            {
                keys = Enumerable.Range(0, 4).Select(_ => Ed25519KeyGenerator.Generate().PublicKey).ToArray();
                cursor = 1u;
            }
            device.ScanGroup(keys, cursor, batchStamps, pattern);
            cursor += batchStamps;
            totalCandidates += (long)batchStamps * 4;
        }

        double elapsed = Stopwatch.GetElapsedTime(startTs).TotalSeconds;
        double ghs = totalCandidates / elapsed / 1e9;
        _output.WriteLine($"GPU: {gpuInfo.Name} [{gpuInfo.BackendName}/{device.KernelVersion}] (Driver {gpuInfo.DriverVersion}) -> {ghs:F3} GH/s ({totalCandidates:N0} candidates in {elapsed:F2}s)");
        Assert.True(ghs > 1.0, $"Expected high-performance GPU throughput > 1.0 GH/s, got {ghs:F3} GH/s");
    }
}
