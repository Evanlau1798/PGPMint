using PGPMint.Core.Search;
using PGPMint.Core.Crypto;
using System.Diagnostics;

namespace PGPMint.Core.Gpu;

internal static class PortableGpuProbe
{
    internal static double Measure(IGpuSearchDevice device, KeyIdPattern pattern)
    {
        if (device.KeysPerBatch != 4) throw new NotSupportedException("Portable GPU selection requires four-key batches.");
        byte[][] keys = Enumerable.Range(0, 4).Select(lane => Enumerable.Range(0, 32)
            .Select(i => (byte)(i + lane * 32)).ToArray()).ToArray();
        const uint first = uint.MaxValue - 127;
        Verify(KeyIdPattern.Parse("0"), 1);
        Verify(KeyIdPattern.Parse(OpenPgpV4Packet.ComputeKeyId(keys[2], uint.MaxValue)), 128);
        Verify(pattern, 128);

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            SearchBatchResult cancelled = device.ScanBatch(keys, first, 128, pattern, cancellationToken: cancellation.Token);
            if (!cancelled.WasCancelled || cancelled.UniqueCandidatesCompleted != 0 || cancelled.ReturnedHits != 0)
                throw new InvalidOperationException("GPU oracle: cancellation contract mismatch.");
        }

        const uint initialCount = 65536;
        const uint maxTimedCount = 1u << 25;
        const double targetDispatchSeconds = 0.002;
        const int warmupDispatches = 16;
        const int minSteadyWarmups = 4;
        const int timedDispatches = 31;

        if (device.MaxCandidatesPerBatch < initialCount * 4L) throw new NotSupportedException("GPU batch ceiling is below the selection workload.");
        uint maxCount = pattern.FixedHexLength <= 4
            ? initialCount
            : (uint)Math.Min(device.MaxCandidatesPerBatch / 4L, maxTimedCount);
        uint count = initialCount;
        for (int i = 0; i < warmupDispatches; i++)
        {
            long warmupStart = Stopwatch.GetTimestamp();
            Dispatch(count);
            double warmupSeconds = Stopwatch.GetElapsedTime(warmupStart).TotalSeconds;
            if (i < warmupDispatches - minSteadyWarmups && count < maxCount && warmupSeconds < targetDispatchSeconds)
            {
                count = (uint)Math.Min((ulong)maxCount, (ulong)count * 2UL);
            }
        }

        var samples = new double[timedDispatches];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Dispatch(count);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
        Array.Sort(samples);
        return (double)count * 4.0 / Math.Max(samples[15], 1.0 / Stopwatch.Frequency);

        void Verify(KeyIdPattern oraclePattern, int capacity)
        {
            SearchBatchResult result = device.ScanBatch(keys, first, 128, oraclePattern, capacity);
            RequireComplete(result, 128);
            for (int lane = 0; lane < 4; lane++)
            {
                uint[] expected = Enumerable.Range(0, 128).Select(i => first + (uint)i)
                    .Where(ts => oraclePattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).ToArray();
                if (!expected.SequenceEqual(result.Hits[lane].Order()))
                    throw new InvalidOperationException($"GPU oracle: full CPU hit set mismatch on lane {lane}.");
            }
        }

        void Dispatch(uint batchCount) =>
            RequireComplete(device.ScanBatch(keys, 1700000000, batchCount, pattern, 1, SearchResultMode.FindOne), batchCount);
    }

    private static void RequireComplete(SearchBatchResult result, uint count)
    {
        if (result.Hits.Length != 4 || !result.HashRangeFullyProcessed || !result.ResultsComplete || result.WasCancelled ||
            result.UniqueCandidatesCompleted != count * 4L || !result.HashEvaluations.HasValue || result.HashEvaluations < count * 4L)
            throw new InvalidOperationException("GPU oracle: incomplete or invalid batch metrics.");
    }
}
