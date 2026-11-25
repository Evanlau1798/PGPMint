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

        // shortcut: bounded startup comparison, use sustained platform calibration for performance acceptance.
        const uint count = 65536;
        if (device.MaxCandidatesPerBatch < count * 4L) throw new NotSupportedException("GPU batch ceiling is below the selection workload.");
        for (int i = 0; i < 16; i++) Dispatch();
        var samples = new double[31];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Dispatch();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalSeconds;
        }
        Array.Sort(samples);
        return count * 4 / Math.Max(samples[15], 1.0 / Stopwatch.Frequency);

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

        void Dispatch() => RequireComplete(device.ScanBatch(keys, 1700000000, count, pattern, 1, SearchResultMode.FindOne), count);
    }

    private static void RequireComplete(SearchBatchResult result, uint count)
    {
        if (result.Hits.Length != 4 || !result.HashRangeFullyProcessed || !result.ResultsComplete || result.WasCancelled ||
            result.UniqueCandidatesCompleted != count * 4L || !result.HashEvaluations.HasValue || result.HashEvaluations < count * 4L)
            throw new InvalidOperationException("GPU oracle: incomplete or invalid batch metrics.");
    }
}
