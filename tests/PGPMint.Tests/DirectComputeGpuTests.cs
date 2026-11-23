using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

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

            // 1. RFC 8032 13-char suffix hit
            byte[][] rfcKeys = Enumerable.Range(0, 4).Select(i =>
            {
                byte[] copy = (byte[])basePub.Clone();
                copy[^1] = i == 2 ? basePub[^1] : (byte)(basePub[^1] ^ (i + 1));
                return copy;
            }).ToArray();

            string expectedKeyId = OpenPgpV4Packet.ComputeKeyId(rfcKeys[2], Rfc8032Timestamp);
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

            uint start = uint.MaxValue - 1030;
            uint count = 1031;

            var overflowPattern = KeyIdPattern.Parse("0");
            var gpuHits = device.ScanGroup(boundaryKeys, start, count, overflowPattern, capacity: 3);

            for (int lane = 0; lane < 4; lane++)
            {
                var expected = new List<uint>();
                for (uint offset = 0; offset < count; offset++)
                {
                    uint ts = start + offset;
                    if (overflowPattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(boundaryKeys[lane], ts)))
                    {
                        expected.Add(ts);
                    }
                }

                Assert.True(expected.Count > 3);
                Assert.Equal(expected, gpuHits[lane].OrderBy(x => x).ToList());
            }

            string boundaryKeyId = OpenPgpV4Packet.ComputeKeyId(boundaryKeys[3], uint.MaxValue);
            var boundaryPattern = KeyIdPattern.Parse(boundaryKeyId[^13..]);
            var boundaryHits = device.ScanGroup(boundaryKeys, start, count, boundaryPattern, capacity: 3);
            Assert.Equal(new[] { uint.MaxValue }, boundaryHits[3].ToArray());
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
