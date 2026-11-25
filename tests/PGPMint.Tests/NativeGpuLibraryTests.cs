using System.Runtime.InteropServices;
using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class NativeGpuLibraryTests
{
    [Fact]
    public void LinuxUsesVersionedSystemLoaderNames()
    {
        Assert.Equal("libvulkan.so.1", NativeGpuLibraries.Candidates("vulkan-1.dll", OSPlatform.Linux)[0]);
        Assert.Equal("libOpenCL.so.1", NativeGpuLibraries.Candidates("OpenCL.dll", OSPlatform.Linux)[0]);
    }

    [Fact]
    public void WindowsKeepsOriginalLoaderNames()
    {
        Assert.Equal(new[] { "vulkan-1.dll" }, NativeGpuLibraries.Candidates("vulkan-1.dll", OSPlatform.Windows));
        Assert.Equal(new[] { "OpenCL.dll" }, NativeGpuLibraries.Candidates("OpenCL.dll", OSPlatform.Windows));
    }

    [Fact]
    public void MacSupportsLoaderAndDirectMoltenVkAndFrameworkOpenCl()
    {
        Assert.Contains("libvulkan.1.dylib", NativeGpuLibraries.Candidates("vulkan-1.dll", OSPlatform.OSX));
        Assert.Contains("libMoltenVK.dylib", NativeGpuLibraries.Candidates("vulkan-1.dll", OSPlatform.OSX));
        Assert.Contains("/System/Library/Frameworks/OpenCL.framework/OpenCL",
            NativeGpuLibraries.Candidates("OpenCL.dll", OSPlatform.OSX));
        Assert.Empty(NativeGpuLibraries.Candidates("unrelated", OSPlatform.Linux));
    }

    [Fact]
    public void AdvertisedPortabilityExtensionsMustBeEnabled()
    {
        Assert.Equal(1u, VulkanInstance.InstanceFlags([VulkanInstance.PortabilityEnumeration]));
        Assert.Equal(0u, VulkanInstance.InstanceFlags([]));
        Assert.Equal(new[] { VulkanInstance.PortabilitySubset }, VulkanInstance.DeviceExtensions([VulkanInstance.PortabilitySubset]));
        Assert.Empty(VulkanInstance.DeviceExtensions([]));
    }
}
