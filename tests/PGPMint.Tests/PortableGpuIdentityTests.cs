using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuIdentityTests
{
    [Fact]
    public void DeviceUuidIsRawBytesRatherThanGuidEndianOrder()
    {
        Assert.Equal("00112233445566778899aabbccddeeff",
            GpuDeviceIdentity.NormalizeUuid(Convert.FromHexString("00112233445566778899AABBCCDDEEFF")));
        Assert.Null(GpuDeviceIdentity.NormalizeUuid(new byte[16]));
    }

    [Fact]
    public void SameModelIsDistinctOnlyWithComparableHardwareIdentity()
    {
        var a = Info("a", "00112233445566778899aabbccddeeff", "0000:01:00.0");
        var b = Info("b", "10112233445566778899aabbccddeeff", "0000:02:00.0");
        Assert.True(GpuDeviceIdentity.Different(a, b));
        Assert.False(GpuDeviceIdentity.Same(a, b));
        Assert.True(GpuDeviceIdentity.Same(a, Info("cl", a.DeviceUuid, null)));
        Assert.True(GpuDeviceIdentity.Same(a, Info("cl", null, a.PciAddress)));
        Assert.False(GpuDeviceIdentity.Different(a, Info("unknown", null, null)));
        Assert.False(GpuDeviceIdentity.Same(a, Info("unknown", null, null)));
    }

    [Fact]
    public void StableKeysDoNotMergeSameModelAdapters()
    {
        Assert.NotEqual(GpuDeviceIdentity.Key("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, null),
            GpuDeviceIdentity.Key("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", null, null));
        Assert.Equal("pci:0000:02:00.0", GpuDeviceIdentity.Key(null, "0000:02:00.0", null));
    }

    private static GpuDeviceInfo Info(string key, string? uuid, string? pci) =>
        new(key, 0, "Identical GPU", 1, 1, 0, 0, 0, "driver", true)
        { DeviceUuid = uuid, PciAddress = pci };

    [Fact]
    public void KnownDifferentLuidsCannotFallBackToSameModelName()
    {
        var hardware = new GpuDeviceInfo("gpu", 42, "Identical GPU", 0x10de, 1, 0, 0, 0, "driver", true);
        var other = new VulkanAdapterDescriptor(0x10de, 1, hardware.Name, "driver", 0, 128, 128, 65535, 1, 99, new byte[16]);

        Assert.Equal(-1, VulkanBackend.FindMatchingAdapterIndex(hardware, [other], []));
    }

    [Fact]
    public void StandaloneVulkanHasNoDxgiDependency()
    {
        var fields = typeof(VulkanBackend).GetFields(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, field => field.FieldType.Name == "DirectComputeBackend");
    }
}
