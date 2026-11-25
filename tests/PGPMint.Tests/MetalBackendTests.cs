using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class MetalBackendTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void BackendAcceptsRequestedComparisonWorkgroupSizes(int workgroupSize)
    {
        var backend = new MetalBackend(cacheDirectory: null, workgroupSize: workgroupSize);
        if (!OperatingSystem.IsMacOS()) Assert.Empty(backend.EnumerateHardwareGpus());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BackendRejectsNonpositiveRequestedWorkgroupSizes(int workgroupSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetalBackend(workgroupSize: workgroupSize));
    }

    [Fact]
    public void DeviceKeysPreserveAllRegistryBitsAndDoNotDependOnNames()
    {
        var first = MetalBackend.Describe(0xFEDCBA9876543210, "Same GPU", 8UL << 30, false, false, "macOS test");
        var second = MetalBackend.Describe(0xFEDCBA9876543211, "Same GPU", 8UL << 30, false, false, "macOS test");
        Assert.Equal("metal:fedcba9876543210", first.DeviceKey);
        Assert.Equal(0xFEDCBA9876543210UL, first.MetalRegistryId);
        Assert.NotEqual(first.DeviceKey, second.DeviceKey);
        Assert.Equal("Metal", first.BackendName);
        Assert.Equal(MetalKernelSource.KernelVersion, first.KernelVersion);
        Assert.True(first.IsDiscrete);
        Assert.Equal(8UL << 30, first.DedicatedVideoMemoryBytes);
        Assert.Equal("macOS test", first.BackendDriverVersion);
    }

    [Fact]
    public void UnifiedMemoryDoesNotPretendToBeDedicatedVram()
    {
        var info = MetalBackend.Describe(1, "Apple GPU", 12UL << 30, true, true, "macOS test");
        Assert.False(info.IsDiscrete);
        Assert.Equal(0UL, info.DedicatedVideoMemoryBytes);
    }

    [Fact]
    public void UnsupportedPlatformDoesNotLoadTheNativeBridge()
    {
        if (OperatingSystem.IsMacOS()) return;
        var backend = new MetalBackend();
        Assert.Empty(backend.EnumerateHardwareGpus());
        var info = MetalBackend.Describe(1, "GPU", 0, true, true, "test");
        Assert.Throws<PlatformNotSupportedException>(() => backend.CreateDevice(info));
    }
}
