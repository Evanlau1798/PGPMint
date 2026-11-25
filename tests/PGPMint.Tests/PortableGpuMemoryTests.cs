using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuMemoryTests
{
    private const ulong HeapBytes = 12UL * 1024 * 1024 * 1024;

    [Theory]
    [InlineData(false, 0UL)]
    [InlineData(true, HeapBytes)]
    public void VulkanSharedDeviceLocalHeapIsNotDedicatedVram(bool discrete, ulong expected)
    {
        var adapter = new VulkanAdapterDescriptor(0x8086, 0x46b3, "GPU", "driver", 0, 128, 128, 65535, 1, null, new byte[16])
        { DeviceUuid = "test", IsDiscrete = discrete, LocalMemoryBytes = HeapBytes };
        Assert.Equal(expected, adapter.ToDeviceInfo().DedicatedVideoMemoryBytes);
    }

    [Theory]
    [InlineData(false, 0UL)]
    [InlineData(true, HeapBytes)]
    public void OpenClUnifiedGlobalMemoryIsNotDedicatedVram(bool discrete, ulong expected)
    {
        var adapter = new OpenClAdapterDescriptor(1, "GPU", 0x8086, "driver", HeapBytes, null, true, 128, 4096)
        { DeviceUuid = "test", IsDiscrete = discrete };
        Assert.Equal(expected, adapter.ToDeviceInfo().DedicatedVideoMemoryBytes);
    }
}
