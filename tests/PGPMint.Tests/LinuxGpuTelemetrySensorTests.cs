using PGPMint.Core.Search;
using Xunit;
using NvmlIdentity = PGPMint.Core.Search.LinuxGpuTelemetrySensor.NvmlAdapterIdentity;

namespace PGPMint.Tests;

public sealed class LinuxGpuTelemetrySensorTests
{
    [Theory]
    [InlineData("GPU-ddb3ce84-9a41-ac12-26a8-f7fa63676e02", "ddb3ce849a41ac1226a8f7fa63676e02")]
    [InlineData("gpu-DDB3CE84-9A41-AC12-26A8-F7FA63676E02", "ddb3ce849a41ac1226a8f7fa63676e02")]
    [InlineData("MIG-01234567-89ab-cdef-0123-456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData(" f956b01613550c3f7b500ca819eec47f ", "f956b01613550c3f7b500ca819eec47f")]
    public void NormalizeNvmlUuidAcceptsValidFormats(string raw, string expected)
    {
        Assert.Equal(expected, LinuxGpuTelemetrySensor.NormalizeNvmlUuid(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("GPU-ddb3ce84-9a41-ac12-26a8-f7fa63676e0")]
    [InlineData("GPU-ddb3ce84-9a41-ac12-26a8-f7fa63676e02aa")]
    [InlineData("GPU-zzzzce84-9a41-ac12-26a8-f7fa63676e02")]
    public void NormalizeNvmlUuidRejectsMalformedInputs(string? raw)
    {
        Assert.Null(LinuxGpuTelemetrySensor.NormalizeNvmlUuid(raw));
    }

    [Theory]
    [InlineData("0000:61:00.0", 0x0u, 0x61u, 0x0u)]
    [InlineData("00000000:61:00.0", 0x0u, 0x61u, 0x0u)]
    [InlineData("pci:0000:02:00.0", 0x0u, 0x02u, 0x0u)]
    [InlineData("0001:ab:1f", 0x1u, 0xabu, 0x1fu)]
    public void TryParsePciAddressParsesValidFormats(string input, uint expectedDomain, uint expectedBus, uint expectedDev)
    {
        bool ok = LinuxGpuTelemetrySensor.TryParsePciAddress(input, out uint domain, out uint bus, out uint dev);

        Assert.True(ok);
        Assert.Equal(expectedDomain, domain);
        Assert.Equal(expectedBus, bus);
        Assert.Equal(expectedDev, dev);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("61:00.0")]
    [InlineData("0000:61:00.0.1")]
    [InlineData("0000:zz:00.0")]
    [InlineData("0000:61:00.")]
    public void TryParsePciAddressRejectsMalformedInputs(string? input)
    {
        Assert.False(LinuxGpuTelemetrySensor.TryParsePciAddress(input, out _, out _, out _));
    }

    [Fact]
    public void ResolveAdapterIndexPrefersUuidOnMultiGpuIdenticalCards()
    {
        NvmlIdentity[] adapters =
        [
            new(0, 0x02, 0, 0x2B8510DEu, 0x53031462u, "11111111111111111111111111111111"),
            new(0, 0x61, 0, 0x2B8510DEu, 0x53031462u, "ddb3ce849a41ac1226a8f7fa63676e02")
        ];

        var byProperty = new GpuDeviceInfo(
            "pci:0000:99:00.0", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0x2B85u, 0, 0, 0, "595.91.07", true, "OpenCL")
        {
            DeviceUuid = "GPU-ddb3ce84-9a41-ac12-26a8-f7fa63676e02"
        };
        var byKey = new GpuDeviceInfo(
            "uuid:ddb3ce849a41ac1226a8f7fa63676e02", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0, 0, 0, 0, "595.91.07", true, "OpenCL");

        Assert.Equal(1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(byProperty, adapters));
        Assert.Equal(1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(byKey, adapters));
    }

    [Fact]
    public void ResolveAdapterIndexFallsBackToPciAddressWhenUuidMissing()
    {
        NvmlIdentity[] adapters =
        [
            new(0, 0x02, 0, 0x2B8510DEu, 0x53031462u, "11111111111111111111111111111111"),
            new(0, 0x61, 0, 0x2B8510DEu, 0x53031462u, "ddb3ce849a41ac1226a8f7fa63676e02")
        ];

        var byPciProp = new GpuDeviceInfo(
            "legacy:0", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0, 0, 0, 0, "595.91.07", true, "Vulkan")
        {
            PciAddress = "0000:61:00.0"
        };
        var byPciKey = new GpuDeviceInfo(
            "pci:0000:02:00.0", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0, 0, 0, 0, "595.91.07", true, "Vulkan");

        Assert.Equal(1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(byPciProp, adapters));
        Assert.Equal(0, LinuxGpuTelemetrySensor.ResolveAdapterIndex(byPciKey, adapters));
    }

    [Fact]
    public void ResolveAdapterIndexUsesPciDeviceIdAndSingleGpuFallbackSafely()
    {
        NvmlIdentity[] multi =
        [
            new(0, 0x02, 0, 0x220410DEu, 0x123410DEu, null),
            new(0, 0x61, 0, 0x2B8510DEu, 0x53031462u, null)
        ];

        var byDevId = new GpuDeviceInfo(
            "vk:0", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0x2B85u, 0, 0, 0, "595.91.07", true, "Vulkan");
        var ambiguous = new GpuDeviceInfo(
            "unknown:0", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0, 0, 0, 0, "595.91.07", true, "OpenCL");

        Assert.Equal(1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(byDevId, multi));
        Assert.Equal(-1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(ambiguous, multi));

        NvmlIdentity[] single = [new(0, 0x61, 0, 0x2B8510DEu, 0x53031462u, null)];
        var nonNvidia = new GpuDeviceInfo(
            "amd:0", 0, "AMD Radeon RX", 0x1002u, 0x744Cu, 0, 0, 0, "24.1", true, "Vulkan");

        Assert.Equal(0, LinuxGpuTelemetrySensor.ResolveAdapterIndex(ambiguous, single));
        Assert.Equal(-1, LinuxGpuTelemetrySensor.ResolveAdapterIndex(nonNvidia, single));
    }

    [Fact]
    public void LinuxTelemetrySensorIsSafeAndIdempotentOnNonLinuxHosts()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        using var sensor = new LinuxGpuTelemetrySensor();
        var device = new GpuDeviceInfo(
            "uuid:ddb3ce849a41ac1226a8f7fa63676e02", 0, "NVIDIA GeForce RTX 5090", 0x10DEu, 0x2B85u, 0, 0, 0, "595.91.07", true, "OpenCL");

        Assert.False(sensor.HasPowerSensor(device));
        Assert.Equal(GpuSensorSample.Empty, sensor.Sample(device));

        sensor.Dispose();
        sensor.Dispose();
    }

    [Fact]
    public void CoordinatorDefaultTelemetrySensorMatchesOperatingSystem()
    {
        using IGpuTelemetrySensor sensor = MultiGpuCoordinator.CreateDefaultTelemetrySensor();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsGpuTelemetrySensor>(sensor);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxGpuTelemetrySensor>(sensor);
        }
        else
        {
            Assert.Same(NullGpuTelemetrySensor.Instance, sensor);
        }
    }
}
