using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Cli;

internal static class GpuVerification
{
    internal static byte[][] PublicKeys() => Enumerable.Range(0, 4).Select(lane =>
    {
        byte[] key = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        key[^1] ^= (byte)lane;
        return key;
    }).ToArray();

    internal static void Verify(IGpuSearchDevice device)
    {
        byte[][] keys = PublicKeys();
        foreach (uint first in new uint[] { 1_700_000_000u, uint.MaxValue - 63 })
        {
            const uint count = 64;
            for (int digits = 1; digits <= 16; digits++)
            {
                string target = OpenPgpV4Packet.ComputeFingerprint(keys[0], first + 31)[^digits..];
                var pattern = KeyIdPattern.Parse(target);
                var result = device.ScanBatch(keys, first, count, pattern, capacity: 1, resultMode: SearchResultMode.EnumerateAll);
                if (!result.ResultsComplete || !result.HashRangeFullyProcessed || result.UniqueCandidatesCompleted != count * 4L)
                    throw new InvalidOperationException("GPU oracle received an incomplete batch.");
                for (int lane = 0; lane < 4; lane++)
                {
                    uint[] expected = Enumerable.Range(0, (int)count).Select(offset => first + (uint)offset)
                        .Where(ts => pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))).Order().ToArray();
                    if (!expected.SequenceEqual(result.Hits[lane].Order()))
                        throw new InvalidOperationException($"GPU oracle mismatch: {digits} hex, lane {lane}.");
                }
                var one = device.ScanBatch(keys, first, count, pattern, capacity: 1, resultMode: SearchResultMode.FindOne);
                if (!one.ResultsComplete || !one.HashRangeFullyProcessed || !one.Hits.SelectMany(x => x).Any())
                    throw new InvalidOperationException("GPU FindOne missed a known hit.");
                for (int lane = 0; lane < 4; lane++)
                    if (one.Hits[lane].Any(ts => ts < first || (ulong)ts >= (ulong)first + count || !pattern.Matches(OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane], ts))))
                        throw new InvalidOperationException("GPU returned an invalid FindOne hit.");
            }
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = device.ScanBatch(keys, 1_700_000_000u, 64, KeyIdPattern.Parse("A"), cancellationToken: cancelled.Token);
        if (!stopped.WasCancelled || stopped.UniqueCandidatesCompleted != 0)
            throw new InvalidOperationException("Cancelled GPU work was counted as completed.");
    }
}
