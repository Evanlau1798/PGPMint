using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class CoordinatorBackendWorkloadTests
{
    [Fact]
    public async Task StartSelectsTheRequestedWorkloadBeforeCreatingAnyGpuSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-workload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new WorkloadBackend();
            using var coordinator = new MultiGpuCoordinator(backend, new AppStateStore(directory), new DpapiVaultStore(directory));
            coordinator.Initialize();
            await coordinator.StartSearchAsync(KeyIdPattern.Parse("ABCDEF0123456789"));
            await coordinator.StopAsync();
            Assert.Equal("ABCDEF0123456789", backend.Pattern?.CanonicalMask);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class WorkloadBackend : IGpuBackend, IWorkloadGpuBackend
    {
        public KeyIdPattern? Pattern;
        public void SetWorkload(KeyIdPattern pattern) => Pattern = pattern;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() =>
            [new("workload-test", 0, "test", 0, 0, 0, 0, 0, "test", false)];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => throw new InvalidOperationException("No GPU needed for this contract test.");
    }
}
