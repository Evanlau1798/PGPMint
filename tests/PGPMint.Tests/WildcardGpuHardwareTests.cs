using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
public sealed class WildcardGpuHardwareTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Vulkan")]
    [InlineData("OpenCL")]
    [InlineData("DirectCompute")]
    public void BackendMatchesCpuOracleOnPrefixMiddleWildcardCrossHalfAndExactLow(string backendKind)
    {
        IGpuBackend backend = backendKind switch
        {
            "Vulkan" => new VulkanBackend(),
            "OpenCL" => new OpenClBackend(),
            "DirectCompute" => new DirectComputeBackend(),
            _ => throw new ArgumentOutOfRangeException(nameof(backendKind))
        };

        GpuDeviceInfo info = backend.EnumerateHardwareGpus().First();
        using IGpuSearchDevice device = backend.CreateDevice(info);

        byte[][] keys = Enumerable.Range(0, 4)
            .Select(lane => Enumerable.Range(0, 32).Select(i => (byte)(0x40 + i + lane * 37)).ToArray())
            .ToArray();

        const uint first = 1_700_000_000u;
        const uint count = 4096u;
        const uint plantedTs = first + 1337u;

        ulong plantedKeyId = OpenPgpV4Packet.ComputeKeyIdUInt64(keys[2], plantedTs);
        string hex = plantedKeyId.ToString("X16");

        KeyIdPattern[] patterns =
        [
            KeyIdPattern.Parse($"{hex[0]}{hex[1]}**************"),              // Pure prefix (MaskLow == 0)
            KeyIdPattern.Parse($"{hex[8]}*{hex[10]}****{hex[15]}"),          // Wildcards inside low half
            KeyIdPattern.Parse($"{hex[0]}*{hex[2]}*****{hex[8]}*******"),    // Cross-half sparse wildcard
            KeyIdPattern.Parse(hex[8..]),                                    // Exact 8-hex low half (MaskLow == 0xffffffff)
            KeyIdPattern.Parse(hex)                                          // Full 16-hex exact match
        ];

        foreach (KeyIdPattern pattern in patterns)
        {
            SearchBatchResult res = device.ScanBatch(
                keys,
                first,
                count,
                pattern,
                capacity: 512,
                resultMode: SearchResultMode.EnumerateAll);

            Assert.True(res.ResultsComplete, $"{backendKind} incomplete for {pattern.CanonicalMask}");
            Assert.True(res.HashRangeFullyProcessed, $"{backendKind} truncated range for {pattern.CanonicalMask}");

            bool sawPlantedOnLane2 = false;
            for (int lane = 0; lane < 4; lane++)
            {
                var expected = new List<uint>();
                for (uint i = 0; i < count; i++)
                {
                    uint ts = first + i;
                    if (pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts)))
                    {
                        expected.Add(ts);
                    }
                }

                uint[] actual = res.Hits[lane].Order().ToArray();
                Assert.Equal(expected.ToArray(), actual);
                if (lane == 2 && actual.Contains(plantedTs))
                {
                    sawPlantedOnLane2 = true;
                }
            }

            Assert.True(sawPlantedOnLane2, $"{backendKind} missed planted hit {plantedTs} on lane 2 for {pattern.CanonicalMask}");
            output.WriteLine($"{backendKind} ({info.Name}) verified {pattern.CanonicalMask} across {count * 4} candidates.");
        }
    }
}
