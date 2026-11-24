using System.Collections.Concurrent;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class KeySchedulePoolTests
{
    [Fact]
    public async Task CoordinatorStartsSearchAtYear2000TimestampBoundary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pm-y2k-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new TimestampCaptureBackend();
            var stateStore = new AppStateStore(directory);
            var pattern = KeyIdPattern.Parse("FEDCBA9876543210");
            string workload = CalibrationValidator.WorkloadIdentity(backend.Info, pattern);
            string profile = CalibrationValidator.ProfileKey(backend.Info.DeviceKey, workload);
            stateStore.Update(s => s.Calibrations[profile] = new CalibrationRecord(
                backend.Info.DeviceKey, backend.Info.Name, backend.Info.DriverVersion,
                backend.Info.KernelVersion, 1e9, 1e9, true, DateTimeOffset.UtcNow, workload));

            using var coordinator = new MultiGpuCoordinator(backend, stateStore, new DpapiVaultStore(directory));
            coordinator.Initialize();

            await coordinator.StartSearchAsync(pattern);
            uint firstTimestamp = await backend.FirstStartTimestamp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(firstTimestamp >= 946_684_800u, $"Expected start timestamp >= 2000-01-01 (946684800), got {firstTimestamp}.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void KeySchedulePoolPrecomputesCachedSchedulesAndZeroesUnusedSeedsOnDispose()
    {
        var trackedGroups = new ConcurrentBag<Ed25519KeyPair[]>();
        using (var pool = new KeySchedulePool(keysPerGroup: 4, capacity: 2, onGroupGenerated: group => trackedGroups.Add(group)))
        {
            SpinWait.SpinUntil(() => trackedGroups.Count >= 2, TimeSpan.FromSeconds(5));

            PreparedKeyGroup first = pool.TakeNext(CancellationToken.None);
            Assert.Equal(4, first.Keys.Length);
            Assert.Equal(4, first.PublicKeys.Length);

            foreach (byte[] pub in first.PublicKeys)
            {
                uint[] cached = Sha1ScheduleHelper.GetOrComputeFixedSchedule(pub);
                uint[] cachedAgain = Sha1ScheduleHelper.GetOrComputeFixedSchedule(pub);
                Assert.Same(cached, cachedAgain);
                Assert.Equal(Sha1ScheduleHelper.ComputeFixedSchedule(pub), cached);
            }

            foreach (Ed25519KeyPair kp in first.Keys)
            {
                Array.Clear(kp.PrivateSeed);
            }

            SpinWait.SpinUntil(() => trackedGroups.Count >= 2, TimeSpan.FromSeconds(5));
        }

        Assert.True(trackedGroups.Count >= 2);
        foreach (Ed25519KeyPair[] group in trackedGroups)
        {
            foreach (Ed25519KeyPair pair in group)
            {
                Assert.All(pair.PrivateSeed, b => Assert.Equal(0, b));
            }
        }
    }

    [Fact]
    public void SharedTimestampXorPlanReconstructsAllRoundMasksExactly()
    {
        TimestampXorPlan plan = Sha1ScheduleHelper.SharedXorPlan;
        uint[] timestamps = [0u, 1u, 946_684_800u, 1_700_000_123u, 0xA55AA55Au, uint.MaxValue];

        foreach (uint ts in timestamps)
        {
            var values = new Dictionary<uint, uint> { [1u] = ts };
            foreach (int shift in plan.RequiredRotations)
            {
                values[1u << shift] = System.Numerics.BitOperations.RotateLeft(ts, shift);
            }

            foreach (TimestampXorStep step in plan.Steps)
            {
                Assert.True(values.ContainsKey(step.LeftMask));
                Assert.True(values.ContainsKey(step.RightMask));
                values[step.ResultMask] = values[step.LeftMask] ^ values[step.RightMask];
            }

            for (int t = 5; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                uint mask = Sha1ScheduleHelper.TimestampMasks[t];
                if (mask == 0u)
                {
                    continue;
                }

                uint expected = 0u;
                for (int shift = 0; shift < 32; shift++)
                {
                    if (((mask >> shift) & 1u) != 0u)
                    {
                        expected ^= System.Numerics.BitOperations.RotateLeft(ts, shift);
                    }
                }

                Assert.Equal(expected, values[mask]);
            }
        }
    }

    private sealed class TimestampCaptureBackend : IGpuBackend, IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = new("GPU", 1, "Mock GPU", 0x10DE, 1, 0, 1, 0, "1", true);
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 1L << 22;
        public string KernelVersion => Info.KernelVersion;
        public TaskCompletionSource<uint> FirstStartTimestamp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [Info];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo deviceInfo) => this;

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024) => throw new NotSupportedException();

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount,
            KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.EnumerateAll,
            CancellationToken cancellationToken = default)
        {
            FirstStartTimestamp.TrySetResult(startTimestamp);
            if (cancellationToken.IsCancellationRequested)
            {
                return SearchBatchResult.CancelledBeforeDispatch(KeysPerBatch);
            }

            Thread.Sleep(5);
            return new SearchBatchResult(SearchBatchResult.EmptyHits(KeysPerBatch), (long)timestampCount * KeysPerBatch, (long)timestampCount * KeysPerBatch);
        }

        public void Dispose() { }
    }
}
