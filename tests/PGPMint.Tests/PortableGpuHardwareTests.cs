using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
public sealed class PortableGpuHardwareTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Vulkan", 64)]
    [InlineData("Vulkan", 128)]
    [InlineData("Vulkan", 256)]
    [InlineData("OpenCL", 64)]
    [InlineData("OpenCL", 128)]
    [InlineData("OpenCL", 256)]
    public void NativeBackendMatchesCpuOracleAndPreservesBatchSemantics(string backendName, int group)
    {
        IGpuBackend backend = backendName == "Vulkan" ? new VulkanBackend(workgroupSize: group) : new OpenClBackend(workgroupSize: group);
        GpuDeviceInfo info = Assert.Single(backend.EnumerateHardwareGpus().Take(1));
        Assert.False(string.IsNullOrWhiteSpace(info.DeviceUuid) && string.IsNullOrWhiteSpace(info.PciAddress));
        using var device = backend.CreateDevice(info);
        Assert.Equal(group, device.Info.KernelConfiguration!.WorkgroupSize);
        double rate = PortableGpuProbe.Measure(device, KeyIdPattern.Parse("1145141919810"));
        Assert.True(rate > 0);
        output.WriteLine($"{device.Info.Name} backend={device.Info.BackendName} driver={device.Info.BackendDriverVersion} " +
            $"uuid={device.Info.DeviceUuid} pci={device.Info.PciAddress} workgroup={group} micro={rate:F0} candidates/s (16 warm/31 timed)");

        byte[][] keys = Enumerable.Range(0, 4).Select(lane => Enumerable.Range(0, 32).Select(i => (byte)(i + lane * 32)).ToArray()).ToArray();
        KeyIdPattern dense = KeyIdPattern.Parse("0");
        SearchBatchResult overflow = device.ScanBatch(keys, 1, 128, dense, capacity: 1);
        for (int lane = 0; lane < 4; lane++)
        {
            uint[] expected = Enumerable.Range(1, 128).Select(i => (uint)i)
                .Where(ts => dense.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).ToArray();
            Assert.Equal(expected, overflow.Hits[lane].Order());
        }
        Assert.True(overflow.ResultsComplete);
        Assert.True(overflow.HashRangeFullyProcessed);
        Assert.True(overflow.HashEvaluations >= overflow.UniqueCandidatesCompleted);

        SearchBatchResult one = device.ScanBatch(keys, 1, 128, dense, capacity: 1, resultMode: SearchResultMode.FindOne);
        Assert.Single(one.Hits.SelectMany(lane => lane));
        Assert.Equal(512, one.UniqueCandidatesCompleted);
        Assert.Equal(512, one.HashEvaluations);
    }

    [Fact]
    public void PortableSelectionMeasuresConfirmedCandidatesAndKeepsOneWorkerIdentity()
    {
        var backend = new PortableGpuBackend();
        backend.SetWorkload(KeyIdPattern.Parse("1145141919810"));
        GpuDeviceInfo info = Assert.Single(backend.EnumerateHardwareGpus().Take(1));
        using var device = backend.CreateDevice(info);
        Assert.Equal(info.DeviceKey, device.Info.DeviceKey);
        foreach (string message in backend.Diagnostics) output.WriteLine(message);
        Assert.Contains(backend.Diagnostics, message => message.Contains("passed the oracle"));
    }
}
