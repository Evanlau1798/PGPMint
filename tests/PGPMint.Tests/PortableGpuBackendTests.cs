using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuBackendTests
{
    private static GpuDeviceInfo Info(string key, string backend, string? uuid = null, string? pci = null) =>
        new(key, 0, "Identical GPU", 1, 1, 0, 0, 0, "driver", true, backend)
        { DeviceUuid = uuid, PciAddress = pci };

    [Fact]
    public void SameGpuAcrossApisSchedulesOnceAndPrefersVulkan()
    {
        var vk = new FakeBackend(Info("vk", "Vulkan", "a"));
        var cl = new FakeBackend(Info("cl", "OpenCL", "a"));
        var backend = Create(vk, cl);
        var info = Assert.Single(backend.EnumerateHardwareGpus());
        Assert.Equal("Vulkan", info.BackendName);
        using var device = backend.CreateDevice(info);
        Assert.Equal(2, vk.Opens);
        Assert.Equal(1, cl.Opens);
    }

    [Fact]
    public void DifferentSameModelGpusAreNotMerged()
    {
        var backend = Create(
            new FakeBackend(Info("vk", "Vulkan", "a")),
            new FakeBackend(Info("cl", "OpenCL", "b")));
        Assert.Equal(2, backend.EnumerateHardwareGpus().Count);
    }

    [Fact]
    public void AmbiguousCrossApiIdentitySuppressesDuplicateAndExplainsLimitation()
    {
        var backend = Create(
            new FakeBackend(Info("vk", "Vulkan", "a")),
            new FakeBackend(Info("cl", "OpenCL", null, "0000:01:00.0")));
        Assert.Single(backend.EnumerateHardwareGpus());
        Assert.Contains(backend.Diagnostics, message => message.Contains("identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UuidAndPciBridgeDeduplicatesOtherDriverEntries()
    {
        var backend = Create(
            new FakeBackend(Info("vk", "Vulkan", "a")),
            new FakeBackend(Info("cl", "OpenCL", "a", "0000:01:00.0"),
                Info("cl-other", "OpenCL", null, "0000:01:00.0")));
        Assert.Single(backend.EnumerateHardwareGpus());
        Assert.Empty(backend.Diagnostics);
    }

    [Fact]
    public void SharedPciIdentityCollapsesPreviouslySeparateDriverUuidGroups()
    {
        var backend = Create(
            new FakeBackend(Info("vk-a", "Vulkan", "a"), Info("vk-b", "Vulkan", "b")),
            new FakeBackend(Info("cl-a", "OpenCL", "a", "0000:01:00.0"),
                Info("cl-b", "OpenCL", "b", "0000:01:00.0"), Info("cl-b-again", "OpenCL", "b")));
        Assert.Single(backend.EnumerateHardwareGpus());
        Assert.NotEmpty(backend.Diagnostics);
    }

    [Fact]
    public void FallbackRequiresConfirmedSameGpuAndPreservesDeviceLost()
    {
        var vk = new FakeBackend(Info("vk", "Vulkan", "a")) { Fail = true };
        var cl = new FakeBackend(Info("cl", "OpenCL", "a"));
        var backend = Create(vk, cl);
        using var device = backend.CreateDevice(Assert.Single(backend.EnumerateHardwareGpus()));
        Assert.Equal("OpenCL", device.Info.BackendName);
        Assert.Equal(1, vk.Opens);
        Assert.Equal(2, cl.Opens);
        Assert.Throws<GpuDeviceLostException>(() => device.ScanGroup([], 0, 1, KeyIdPattern.Parse("A")));
        Assert.Contains(backend.Diagnostics, message => message.Contains("Vulkan"));
    }

    [Fact]
    public void UnidentifiedOpenClOnlyKeepsOneCandidateWithWarning()
    {
        var backend = Create(new FakeBackend(), new FakeBackend(
            Info("cl1", "OpenCL"), Info("cl2", "OpenCL")));
        Assert.Single(backend.EnumerateHardwareGpus());
        Assert.NotEmpty(backend.Diagnostics);
    }

    [Fact]
    public void UnavailableOptionalBackendDoesNotHideRemainingHardware()
    {
        var backend = Create(new BrokenBackend(), new FakeBackend(Info("vk", "Vulkan", "a")));
        Assert.Single(backend.EnumerateHardwareGpus());
        Assert.Contains(backend.Diagnostics, message => message.Contains("loader missing"));
    }

    private sealed class BrokenBackend : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => throw new DllNotFoundException("loader missing");
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => throw new NotSupportedException();
    }

    private static PortableGpuBackend Create(params IGpuBackend[] backends) => new(backends, (_, _) => 1);

    private sealed class FakeBackend(params GpuDeviceInfo[] devices) : IGpuBackend
    {
        public int Opens { get; private set; }
        public bool Fail { get; init; }
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => devices;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info)
        {
            Opens++;
            if (Fail) throw new InvalidOperationException("driver initialization failed");
            return new FakeDevice(info);
        }
    }

    private sealed class FakeDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info => info;
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 100;
        public string KernelVersion => "test";
        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp,
            uint timestampCount, KeyIdPattern pattern, int capacity = 65536) => throw new GpuDeviceLostException("test");
        public void Dispose() { }
    }
}
