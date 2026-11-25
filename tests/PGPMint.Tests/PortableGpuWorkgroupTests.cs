using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuWorkgroupTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    public void RequestedWorkgroupPropagatesWithinHardwareLimit(int group)
    {
        Assert.Equal(group, VulkanDevice.ConfigurationFor(1024, group).WorkgroupSize);
        Assert.Equal(group, OpenClDevice.ConfigurationFor(1024, true, group).WorkgroupSize);
        Assert.Equal(64, VulkanDevice.ConfigurationFor(64, group).WorkgroupSize);
        Assert.Equal(64, OpenClDevice.ConfigurationFor(64, true, group).WorkgroupSize);
    }

    [Fact]
    public void LegacyWindowsDefaultStays128()
    {
        Assert.Equal(128, VulkanDevice.ConfigurationFor(128).WorkgroupSize);
        Assert.Equal(128, OpenClDevice.ConfigurationFor(1024).WorkgroupSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(512)]
    public void BackendRejectsUnsupportedRequestedWorkgroups(int group)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VulkanBackend(null, group));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpenClBackend(null, group));
    }
}
