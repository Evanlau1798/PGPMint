using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;
using Xunit.Abstractions;

namespace PGPMint.Tests;

[Collection("Hardware GPU")]
[Trait("Category", "HardwareGpu")]
[Trait("Backend", "OpenCL")]
public sealed class OpenClProgramCacheTests(ITestOutputHelper output)
{
    [Fact]
    public void CreatesCacheAndRecoversFromARejectedBinary()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm-clbin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var backend = new HybridGpuBackend(root);
            var info = Assert.Single(backend.EnumerateHardwareGpus().Where(gpu => gpu.BackendName == "OpenCL").Take(1));
            OpenAndCheck(expectCached: false, stage: "pair-1-source");
            string entry = Assert.Single(Directory.EnumerateFiles(root, "*.clbin", SearchOption.AllDirectories));
            Assert.True(new FileInfo(entry).Length > 64);

            OpenAndCheck(expectCached: true, stage: "pair-1-binary");
            OpenAndCheck(expectCached: true, stage: "pair-2-binary");
            File.Delete(entry);
            OpenAndCheck(expectCached: false, stage: "pair-2-source");
            File.Delete(entry);
            OpenAndCheck(expectCached: false, stage: "pair-3-source");
            OpenAndCheck(expectCached: true, stage: "pair-3-binary");

            byte[] rejected = [0, 1, 2, 3];
            string rejectedContents = $"{Convert.ToHexString(SHA256.HashData(rejected))}\n{Convert.ToBase64String(rejected)}";
            File.WriteAllText(entry, rejectedContents);
            OpenAndCheck(expectCached: false, stage: "rebuild");
            Assert.NotEqual(rejectedContents, File.ReadAllText(entry));
            OpenAndCheck(expectCached: true, stage: "rebuilt-binary");

            void OpenAndCheck(bool expectCached, string stage)
            {
                long start = Stopwatch.GetTimestamp();
                using var device = Assert.IsType<OpenClDevice>(backend.CreateDevice(info));
                double openMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert.Equal(expectCached, device.LoadedProgramBinary);

                byte[][] keys = Enumerable.Range(0, device.KeysPerBatch)
                    .Select(_ => Convert.FromHexString("D75A980182B10AB7D54BFED3C964073A0EE172F3DAA62325AF021A68F707511A"))
                    .ToArray();
                var pattern = KeyIdPattern.Parse("99933B470FD7C498");
                SearchBatchResult result = device.ScanBatch(keys, 1700000000u, 1, pattern, resultMode: SearchResultMode.FindOne);
                Assert.Equal(1700000000u, Assert.Single(result.Hits.SelectMany(lane => lane)));
                output.WriteLine($"{device.Info.Name} {device.Info.DriverVersion} ICD={device.Info.BackendDriverVersion} " +
                    $"kernel={device.KernelVersion} stage={stage} open={openMs:F2} ms");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
