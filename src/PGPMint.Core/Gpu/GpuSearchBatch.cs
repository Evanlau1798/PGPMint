using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal static class GpuSearchBatch
{
    internal static SearchBatchResult Run(
        int laneCount,
        uint startTimestamp,
        uint timestampCount,
        int capacity,
        SearchResultMode resultMode,
        CancellationToken cancellationToken,
        Func<uint, uint, uint[]> dispatch)
    {
        long evaluations = 0;
        bool cancelled = false;
        IReadOnlyList<uint>[] hits = ScanRange(startTimestamp, timestampCount);
        bool rangeCompleted = evaluations != 0;
        return new SearchBatchResult(hits, rangeCompleted ? (long)timestampCount * laneCount : 0, evaluations)
        {
            HashRangeFullyProcessed = rangeCompleted,
            ResultsComplete = !cancelled,
            WasCancelled = cancelled
        };

        IReadOnlyList<uint>[] ScanRange(uint first, uint count)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return SearchBatchResult.EmptyHits(laneCount);
            }

            uint[] readback = dispatch(first, count);
            evaluations += (long)count * laneCount;
            int stride = capacity + 1;
            var result = SearchBatchResult.EmptyHits(laneCount);
            for (int lane = 0; lane < laneCount; lane++)
            {
                int index = lane * stride;
                uint found = readback[index];
                if (found > (uint)capacity)
                {
                    if (resultMode == SearchResultMode.FindOne || count == 1)
                    {
                        throw new InvalidOperationException("GPU hit buffer exceeded its expected capacity.");
                    }

                    uint half = count / 2;
                    IReadOnlyList<uint>[] left = ScanRange(first, half);
                    if (cancelled)
                    {
                        return left;
                    }

                    IReadOnlyList<uint>[] right = ScanRange(first + half, count - half);
                    for (int i = 0; i < laneCount; i++)
                    {
                        left[i] = left[i].Count == 0 ? right[i]
                            : right[i].Count == 0 ? left[i] : left[i].Concat(right[i]).ToArray();
                    }

                    return left;
                }

                if (found != 0)
                {
                    result[lane] = readback.AsSpan(index + 1, (int)found).ToArray();
                    if (resultMode == SearchResultMode.FindOne)
                    {
                        break;
                    }
                }
            }

            return result;
        }
    }

    internal static bool HasHits(uint[] readback, int stride, int laneCount)
    {
        for (int lane = 0; lane < laneCount; lane++)
        {
            if (readback[lane * stride] != 0)
            {
                return true;
            }
        }

        return false;
    }
}
