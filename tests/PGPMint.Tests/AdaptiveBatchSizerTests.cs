using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class AdaptiveBatchSizerTests
{
    [Fact]
    public void FastAndSlowDispatchesChangeTheNextBatchByAtMostTwofold()
    {
        var sizer = new AdaptiveBatchSizer(4L << 20, 4, 16);
        Assert.Equal(65_536u, sizer.NextTimestampLimit);

        sizer.Observe(65_536u, 1);
        Assert.Equal(131_072u, sizer.NextTimestampLimit);

        sizer.Observe(131_072u, 100);
        Assert.Equal(65_536u, sizer.NextTimestampLimit);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDispatchTimeReturnsToTheSmallInitialBatch(double time)
    {
        var sizer = new AdaptiveBatchSizer(4L << 20, 4, 16);
        sizer.Observe(65_536u, 1);
        Assert.Equal(131_072u, sizer.NextTimestampLimit);

        sizer.Observe(131_072u, time);

        Assert.Equal(65_536u, sizer.NextTimestampLimit);
    }

    [Fact]
    public void DenseSuffixDoesNotGrowBeyondItsCandidateLimit()
    {
        var sizer = new AdaptiveBatchSizer(4L << 20, 4, 1);
        Assert.Equal(512L, sizer.NextCandidateLimit);

        sizer.Observe(sizer.NextTimestampLimit, 0.01);
        Assert.Equal(512L, sizer.NextCandidateLimit);
    }

    [Fact]
    public void DeviceAndTailLimitsAreRespectedWithoutShrinkingTheNextRange()
    {
        var sizer = new AdaptiveBatchSizer(10, 4, 16);
        Assert.Equal(8L, sizer.NextCandidateLimit);
        var fullSpeed = new RateDirective(RateMode.FullSpeed, RateControl.FullSpeedSentinel);
        uint tail = RateControl.ComputeBatchTimestamps(sizer.NextCandidateLimit, 4, fullSpeed, remainingTimestamps: 1);

        Assert.Equal(1u, tail);
        sizer.Observe(tail, 0.01);
        Assert.Equal(8L, sizer.NextCandidateLimit);
    }
}
