using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public class KeyIdPatternTests
{
    [Fact]
    public void SuffixPatternCompilesMaskTargetAndDisplay()
    {
        KeyIdPattern pattern = KeyIdPattern.Parse("1145141919810");

        Assert.Equal("1145141919810", pattern.Suffix);
        Assert.Equal("***1145141919810", pattern.CanonicalMask);
        Assert.Equal("***1 1451 4191 9810", pattern.FormattedDisplay);
        Assert.Equal(13, pattern.FixedHexLength);
        Assert.Equal(0x000FFFFFFFFFFFFFul, pattern.Mask);
        Assert.Equal(0x0001145141919810ul, pattern.Target);
        Assert.Equal(0xFFFFFFFFu, pattern.MaskLow);
        Assert.Equal(0x41919810u, pattern.TargetLow);
        Assert.Equal(0x000FFFFFu, pattern.MaskHigh);
        Assert.Equal(0x00011451u, pattern.TargetHigh);

        Assert.True(pattern.Matches(0xABC1145141919810ul));
        Assert.False(pattern.Matches(0xABC1145141919811ul));
    }

    [Fact]
    public void Full16CharAndSpacedSuffixPatternsParseCleanly()
    {
        KeyIdPattern spaced = KeyIdPattern.Parse("dead beef");
        Assert.Equal("DEADBEEF", spaced.Suffix);
        Assert.Equal("********DEADBEEF", spaced.CanonicalMask);
        Assert.Equal("**** **** DEAD BEEF", spaced.FormattedDisplay);
        Assert.Equal(8, spaced.FixedHexLength);
        Assert.True(spaced.Matches(0x12345678DEADBEEFul));
        Assert.False(spaced.Matches(0xDEADBEEF12345678ul));

        KeyIdPattern full16 = KeyIdPattern.Parse("0123 4567 89AB CDEF");
        Assert.Equal(16, full16.FixedHexLength);
        Assert.Equal(ulong.MaxValue, full16.Mask);
        Assert.Equal(0x0123456789ABCDEFul, full16.Target);
        Assert.True(full16.Matches(0x0123456789ABCDEFul));
    }

    [Theory]
    [InlineData("", KeyIdValidationError.Empty)]
    [InlineData("   ", KeyIdValidationError.Empty)]
    [InlineData("---", KeyIdValidationError.InvalidCharacters)]
    [InlineData("___", KeyIdValidationError.InvalidCharacters)]
    [InlineData("1234567890ABCDEF0", KeyIdValidationError.TooLong)]
    [InlineData("GHIJ", KeyIdValidationError.InvalidCharacters)]
    [InlineData("CAFE*123", KeyIdValidationError.InvalidCharacters)]
    [InlineData("1145@14", KeyIdValidationError.InvalidCharacters)]
    public void InvalidSuffixPatternsReportExactValidationError(string raw, KeyIdValidationError expectedError)
    {
        Assert.False(KeyIdPattern.TryParse(raw, out KeyIdPattern? parsed, out KeyIdValidationError actualError));
        Assert.Null(parsed);
        Assert.Equal(expectedError, actualError);
    }

    [Fact]
    public void ProbabilityAndEtaMathMatchAnalyticalFormula()
    {
        KeyIdPattern pattern = KeyIdPattern.Parse("1145141919810");

        Assert.Equal(0.0, pattern.ComputeCumulativeProbability(0));
        double probAtMean = pattern.ComputeCumulativeProbability(1UL << 52);
        Assert.InRange(probAtMean, 0.63212, 0.63213);

        TimeSpan? eta = pattern.EstimateMeanWait(20_000_000_000.0);
        Assert.NotNull(eta);
        Assert.InRange(eta.Value.TotalSeconds, 225_179.0, 225_181.0);
        Assert.Null(pattern.EstimateMeanWait(0));
    }
}
