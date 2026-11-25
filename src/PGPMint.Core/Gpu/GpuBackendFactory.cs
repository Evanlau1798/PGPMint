using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public static class GpuBackendFactory
{
    public static IGpuBackend CreateDefault(string? cacheDirectory = null, int workgroupSize = 128)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && workgroupSize == 128) return new HybridGpuBackend(cacheDirectory);
#endif
        return OperatingSystem.IsMacOS()
            ? new PortableGpuBackend(new MetalBackend(cacheDirectory, workgroupSize), new VulkanBackend(cacheDirectory, workgroupSize))
            : new PortableGpuBackend(cacheDirectory, workgroupSize);
    }
}
