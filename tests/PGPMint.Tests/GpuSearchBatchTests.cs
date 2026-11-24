using PGPMint.Core.Gpu;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class GpuSearchBatchTests
{
    [Theory]
    [InlineData(true, 1, 4)]
    [InlineData(false, 2, 6)]
    public void CancelledOverflowKeepsCompletedWorkAndMarksResultsIncomplete(
        bool cancelAfterRoot,
        int expectedDispatches,
        long expectedEvaluations)
    {
        using var cancellation = new CancellationTokenSource();
        var ranges = new List<(uint Start, uint Count)>();
        SearchBatchResult result = GpuSearchBatch.Run(1, 1, 10, 4, 1, SearchResultMode.EnumerateAll,
            cancellation.Token, (start, count) =>
            {
                ranges.Add((start, count));
                if (cancelAfterRoot || ranges.Count == 2)
                {
                    cancellation.Cancel();
                }
                return count == 4 ? [2, start] : [1, start];
            });

        Assert.Equal(expectedDispatches, ranges.Count);
        Assert.Equal((10u, 4u), ranges[0]);
        Assert.Equal(4L, result.UniqueCandidatesCompleted);
        Assert.Equal(expectedEvaluations, result.HashEvaluations);
        Assert.Equal(expectedEvaluations - 4, result.RescanEvaluations);
        Assert.True(result.HashRangeFullyProcessed);
        Assert.False(result.ResultsComplete);
        Assert.True(result.WasCancelled);
        if (cancelAfterRoot)
        {
            Assert.Empty(result.Hits[0]);
        }
        else
        {
            Assert.Equal((10u, 2u), ranges[1]);
            Assert.Equal(10u, Assert.Single(result.Hits[0]));
        }
    }
}
