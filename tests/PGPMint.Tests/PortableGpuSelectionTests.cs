using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PortableGpuSelectionTests
{
    [Fact]
    public void MeasuresSeriallyChoosesFastestAndKeepsCanonicalKeyAcrossApis()
    {
        var state = new State();
        var vk = new Backend("vk", "Vulkan", state);
        var cl = new Backend("cl", "OpenCL", state);
        var measured = new List<string>();
        var portable = new PortableGpuBackend([vk, cl], (device, _) =>
        {
            Assert.Equal(1, state.Live);
            measured.Add(device.Info.BackendName);
            return device.Info.BackendName == "Vulkan" ? 1 : 2;
        });
        GpuDeviceInfo info = Assert.Single(portable.EnumerateHardwareGpus());
        using (var device = portable.CreateDevice(info))
        {
            Assert.Equal(new[] { "Vulkan", "OpenCL" }, measured);
            Assert.Equal("OpenCL", device.Info.BackendName);
            Assert.Equal(info.DeviceKey, device.Info.DeviceKey);
            Assert.Equal(1, state.Live);
        }
        Assert.Equal(0, state.Live);
        using var again = portable.CreateDevice(info);
        Assert.Equal(2, measured.Count);
    }

    [Fact]
    public void WorkloadDriverAndConfigurationInvalidateSelectionButSameSuffixGroupReusesIt()
    {
        var state = new State();
        var vk = new Backend("vk", "Vulkan", state);
        var cl = new Backend("cl", "OpenCL", state);
        int samples = 0;
        var portable = new PortableGpuBackend([vk, cl], (_, _) => ++samples);
        var info = Assert.Single(portable.EnumerateHardwareGpus());
        portable.SetWorkload(KeyIdPattern.Parse("A"));
        portable.CreateDevice(info).Dispose();
        Assert.Equal(2, samples);
        portable.SetWorkload(KeyIdPattern.Parse("B"));
        portable.CreateDevice(info).Dispose();
        Assert.Equal(2, samples);
        portable.SetWorkload(KeyIdPattern.Parse("123456789"));
        portable.CreateDevice(info).Dispose();
        Assert.Equal(4, samples);
        vk.Driver = "updated";
        portable.CreateDevice(Assert.Single(portable.EnumerateHardwareGpus())).Dispose();
        Assert.Equal(6, samples);
        vk.Group = 256;
        portable.CreateDevice(Assert.Single(portable.EnumerateHardwareGpus())).Dispose();
        Assert.Equal(8, samples);
    }

    [Fact]
    public void CandidateFailingOracleIsDisposedExcludedAndDiagnosed()
    {
        var state = new State();
        var portable = new PortableGpuBackend([new Backend("vk", "Vulkan", state), new Backend("cl", "OpenCL", state)],
            (device, _) => device.Info.BackendName == "Vulkan" ? throw new InvalidOperationException("oracle mismatch") : 1);
        using var device = portable.CreateDevice(Assert.Single(portable.EnumerateHardwareGpus()));
        Assert.Equal("OpenCL", device.Info.BackendName);
        Assert.Equal(1, state.Live);
        Assert.Contains(portable.Diagnostics, message => message.Contains("oracle mismatch"));
    }

    [Fact]
    public void MoltenVkUsesProvenBigEndianLuidRegistryIdentity()
    {
        byte[] driver = new byte[16];
        "MVK\0"u8.CopyTo(driver);
        byte[] luid = Convert.FromHexString("0011223344556677");
        Assert.Equal(0x0011223344556677UL, GpuDeviceIdentity.MoltenVkRegistryId(luid, driver, true, true));
        Assert.Null(GpuDeviceIdentity.MoltenVkRegistryId(luid, driver, false, true));
        Assert.Null(GpuDeviceIdentity.MoltenVkRegistryId(luid, new byte[16], true, true));
        Assert.Null(GpuDeviceIdentity.MoltenVkRegistryId(luid, driver, true, false));
    }

    private sealed class State { public int Live; }
    private sealed class Backend(string key, string name, State state) : IGpuBackend
    {
        public string Driver { get; set; } = "driver";
        public int Group { get; set; } = 128;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() =>
            [new(key, 0, "same GPU", 1, 1, 0, 0, 0, Driver, true, name, "kernel")
            { DeviceUuid = "a", KernelConfiguration = new(4, Group, 1, "test") }];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new Device(info, state);
    }
    private sealed class Device : IGpuSearchDevice
    {
        private readonly State _state;
        public Device(GpuDeviceInfo info, State state) { Info = info; _state = state; state.Live++; }
        public GpuDeviceInfo Info { get; }
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 1 << 24;
        public string KernelVersion => "test";
        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> keys, uint first, uint count, KeyIdPattern pattern,
            int capacity = 1024) => throw new NotSupportedException();
        public void Dispose() => _state.Live--;
    }
}
