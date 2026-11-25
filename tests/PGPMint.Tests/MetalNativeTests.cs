using System.Runtime.InteropServices;
using System.Text;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class MetalNativeTests
{
    [Fact]
    public void AdapterAbiMatchesNativeLayout()
    {
        Assert.Equal(280, Marshal.SizeOf<MetalNative.Adapter>());
        Assert.Equal(0, Marshal.OffsetOf<MetalNative.Adapter>(nameof(MetalNative.Adapter.RegistryId)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<MetalNative.Adapter>(nameof(MetalNative.Adapter.WorkingSetBytes)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<MetalNative.Adapter>(nameof(MetalNative.Adapter.UnifiedMemory)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<MetalNative.Adapter>(nameof(MetalNative.Adapter.LowPower)).ToInt32());
    }

    [Fact]
    public void NativeErrorMessagesPreserveUtf8AndDeviceLossClassification()
    {
        byte[] message = Encoding.UTF8.GetBytes("GPU 已移除\0unused");
        var lost = Assert.Throws<GpuDeviceLostException>(() => MetalNative.Check(-2, message));
        Assert.Contains("GPU 已移除", lost.Message);
        Assert.DoesNotContain("unused", lost.Message);
        var operation = Assert.Throws<InvalidOperationException>(() => MetalNative.Check(-1, message));
        Assert.Contains("GPU 已移除", operation.Message);
        MetalNative.Check(0, message);
    }

    [Fact]
    public void NonTerminatedNativeErrorIsBoundedByManagedBuffer()
    {
        var error = Assert.Throws<InvalidOperationException>(() => MetalNative.Check(-1, [65, 66, 67]));
        Assert.EndsWith("ABC", error.Message);
    }
}
