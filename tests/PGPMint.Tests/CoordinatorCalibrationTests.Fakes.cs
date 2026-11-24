using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public partial class CoordinatorCalibrationTests
{
    private sealed class PerDeviceFakeBackend : IGpuBackend
    {
        private readonly IReadOnlyList<GpuDeviceInfo> _devices;
        public PerDeviceFakeBackend(IReadOnlyList<GpuDeviceInfo> devices) => _devices = devices;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _devices;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new FakeGpuDevice(info, 5, 4000, info.KernelVersion);
    }

    private sealed class FallbackFakeBackend : IGpuBackend
    {
        private readonly GpuDeviceInfo _enumerated;
        public FallbackFakeBackend(GpuDeviceInfo enumerated) => _enumerated = enumerated;
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => new[] { _enumerated };
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info)
        {
            var actual = info with { BackendName = "DirectCompute", KernelVersion = "d3d11-sha1-four-v1" };
            return new FakeGpuDevice(actual, 5, 4000, actual.KernelVersion);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25);
        }
        Assert.True(predicate(), "Timed out waiting for coordinator state transition.");
    }

    private sealed class FakeGpuBackend : IGpuBackend
    {
        private readonly IReadOnlyList<GpuDeviceInfo> _devices;
        private readonly int _batchDelayMs;
        private readonly long _maxCandidatesPerBatch;
        private readonly string _kernelVersion;
        private readonly int _maxHitBatches;

        public FakeGpuBackend(IReadOnlyList<GpuDeviceInfo> devices, int batchDelayMs, long maxCandidatesPerBatch,
            string kernelVersion = "fake-kernel-v1", int maxHitBatches = int.MaxValue)
        {
            _devices = devices;
            _batchDelayMs = batchDelayMs;
            _maxCandidatesPerBatch = maxCandidatesPerBatch;
            _kernelVersion = kernelVersion;
            _maxHitBatches = maxHitBatches;
        }

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => _devices;

        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) =>
            new FakeGpuDevice(deviceInfo, _batchDelayMs, _maxCandidatesPerBatch, _kernelVersion, _maxHitBatches);
    }

    private sealed class FakeGpuDevice : IGpuSearchDevice
    {
        private readonly int _batchDelayMs;
        private int _remainingHitBatches;
        public GpuDeviceInfo Info { get; }
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch { get; }
        public string KernelVersion { get; }

        public FakeGpuDevice(GpuDeviceInfo info, int batchDelayMs, long maxCandidatesPerBatch, string kernelVersion, int maxHitBatches = int.MaxValue)
        {
            Info = info;
            _batchDelayMs = batchDelayMs;
            _remainingHitBatches = maxHitBatches;
            MaxCandidatesPerBatch = maxCandidatesPerBatch;
            KernelVersion = kernelVersion;
        }

        public IReadOnlyList<uint>[] ScanGroup(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024) =>
            ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

        public SearchBatchResult ScanBatch(
            IReadOnlyList<byte[]> publicKeys,
            uint startTimestamp,
            uint timestampCount,
            KeyIdPattern pattern,
            int capacity = 1024,
            SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new SearchBatchResult([[], [], [], []], 0, 0)
                {
                    HashRangeFullyProcessed = false,
                    ResultsComplete = false,
                    WasCancelled = true
                };
            }

            if (_batchDelayMs > 0)
            {
                Thread.Sleep(_batchDelayMs);
            }

            var results = new IReadOnlyList<uint>[4];
            Array.Fill<IReadOnlyList<uint>>(results, Array.Empty<uint>());
            var batch = new SearchBatchResult(results, (long)timestampCount * KeysPerBatch, HashEvaluations: null);
            if (pattern.FixedHexLength >= 10 || _remainingHitBatches == 0)
            {
                return batch;
            }

            _remainingHitBatches--;
            uint scanLimit = Math.Min(timestampCount, 64u);
            for (int lane = 0; lane < 4; lane++)
            {
                var hits = new List<uint>();
                for (uint offset = 0; offset < scanLimit; offset++)
                {
                    uint ts = startTimestamp + offset;
                    if (pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(publicKeys[lane], ts)))
                    {
                        hits.Add(ts);
                        if (resultMode == SearchResultMode.FindOne)
                        {
                            results[lane] = hits;
                            return batch;
                        }
                    }
                }
                results[lane] = hits;
            }
            return batch;
        }

        public void Dispose() { }
    }
}
