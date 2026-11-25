using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

[CollectionDefinition("Metal hardware", DisableParallelization = true)]
public sealed class MetalHardwareCollection { }

[Collection("Metal hardware")]
[Trait("Category", "HardwareGpu")]
public sealed class MetalHardwareTests
{
    [MetalHardwareFact]
    public void NativeKernelMatchesCpuAcrossLanesPatternsOverflowAndWorkgroupSizes()
    {
        byte[][] keys = Keys();
        const uint first = uint.MaxValue - 255;
        string keyId = OpenPgpV4Packet.ComputeKeyId(keys[3], uint.MaxValue);
        foreach (int workgroupSize in new[] { 64, 128, 256 })
        {
            var backend = new MetalBackend(workgroupSize: workgroupSize);
            IReadOnlyList<GpuDeviceInfo> adapters = backend.EnumerateHardwareGpus();
            Assert.NotEmpty(adapters);
            foreach (GpuDeviceInfo adapter in adapters)
            {
                using IGpuSearchDevice device = backend.CreateDevice(adapter);
                Assert.InRange(device.Info.KernelConfiguration!.WorkgroupSize, 1, workgroupSize);
                Assert.Equal(adapter.MetalRegistryId, device.Info.MetalRegistryId);
                foreach (int length in new[] { 1, 8, 9, 13, 16 })
                {
                    KeyIdPattern pattern = KeyIdPattern.Parse(length == 1 ? "0" : keyId[^length..]);
                    SearchBatchResult batch = device.ScanBatch(keys, first, 256, pattern, capacity: 1);
                    Assert.Equal(1024, batch.UniqueCandidatesCompleted);
                    Assert.True(batch.HashEvaluations >= 1024);
                    Assert.True(batch.HashRangeFullyProcessed);
                    Assert.True(batch.ResultsComplete);
                    for (int lane = 0; lane < 4; lane++)
                    {
                        uint[] expected = Enumerable.Range(0, 256).Select(offset => first + (uint)offset)
                            .Where(ts => pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).ToArray();
                        Assert.Equal(expected, batch.Hits[lane].Order());
                    }
                    if (length == 1) Assert.True(batch.RescanEvaluations > 0);
                    else Assert.Contains(uint.MaxValue, batch.Hits[3]);
                }

                SearchBatchResult bounded = device.ScanBatch(keys, first, 256, KeyIdPattern.Parse("0"),
                    resultMode: SearchResultMode.FindOne);
                Assert.Equal(1, bounded.ReturnedHits);
                Assert.Equal(1024L, bounded.HashEvaluations);
                Assert.Equal(0L, bounded.RescanEvaluations);
                Assert.True(bounded.HashRangeFullyProcessed);
                int hitLane = Array.FindIndex(bounded.Hits, hits => hits.Count != 0);
                Assert.EndsWith("0", OpenPgpV4Packet.ComputeKeyId(keys[hitLane], bounded.Hits[hitLane][0]));

                SearchBatchResult zero = device.ScanBatch(keys, first, 256, KeyIdPattern.Parse("0000000000000000"));
                Assert.Equal(0, zero.ReturnedHits);
                Assert.Equal(1024L, zero.HashEvaluations);
                Assert.True(zero.ResultsComplete);

                SearchBatchResult cancelled = device.ScanBatch(keys, first, 256, KeyIdPattern.Parse("0"),
                    cancellationToken: new CancellationToken(true));
                Assert.Equal(0L, cancelled.HashEvaluations);
                Assert.True(cancelled.WasCancelled);

                for (int lane = 0; lane < 4; lane++)
                {
                    string exact = OpenPgpV4Packet.ComputeKeyId(keys[lane], 0);
                    SearchBatchResult atZero = device.ScanBatch(keys, 0, 1, KeyIdPattern.Parse(exact));
                    Assert.Contains(0u, atZero.Hits[lane]);
                }
            }
        }
    }

    private static byte[][] Keys() => Enumerable.Range(0, 4).Select(lane =>
    {
        byte[] key = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        key[^1] = (byte)lane;
        return key;
    }).ToArray();
}

internal sealed class MetalHardwareFactAttribute : FactAttribute
{
    public MetalHardwareFactAttribute()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(11)) Skip = "Requires macOS 11+, the native bridge and a Metal GPU.";
    }
}
