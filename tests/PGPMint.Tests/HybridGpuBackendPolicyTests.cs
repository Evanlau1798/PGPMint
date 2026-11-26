using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class HybridGpuBackendPolicyTests
{
    [Theory]
    [InlineData(20.0e9, 20.0e9, false)]     // Equal throughput keeps Vulkan default
    [InlineData(19.15e9, 20.26e9, false)]   // RTX 3080-like +5.8% keeps Vulkan (WDDM/NVDEC resilience)
    [InlineData(100.0, 114.9, false)]       // Below 15% promotion margin keeps Vulkan
    [InlineData(100.0, 115.0, true)]        // At +15% promotion margin selects OpenCL
    [InlineData(54.07e9, 80.53e9, true)]    // RTX 5090-like +48.9% selects OpenCL
    [InlineData(100.0, 0.0, false)]         // Invalid OpenCL measurement never replaces Vulkan
    [InlineData(100.0, double.NaN, false)]
    [InlineData(0.0, 50.0, true)]           // Invalid Vulkan measurement falls back to valid OpenCL
    [InlineData(double.NaN, 50.0, true)]
    public void MarginGateOnlyPromotesOpenClWhenGainMeetsThreshold(
        double vulkanScore,
        double openClScore,
        bool expectedPreferOpenCl)
    {
        Assert.Equal(expectedPreferOpenCl, HybridGpuBackend.ShouldPreferOpenCl(vulkanScore, openClScore));
    }
}
