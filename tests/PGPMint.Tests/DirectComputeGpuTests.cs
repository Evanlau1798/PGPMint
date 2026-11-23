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
                var gpuHits = device.ScanGroup(boundaryKeys, start, count, KeyIdPattern.Parse(suffix), capacity);
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
                    Assert.Equal(expected, gpuHits[lane].Order());
                }
            }
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
