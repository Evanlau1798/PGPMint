using System.Numerics;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PatternPlanTests
{
    [Fact]
    public void InversePredicateMatchesOutputAtEverySuffixLength()
    {
        const uint ivLow = 0xC3D2E1F0u;
        const uint ivHigh = 0x10325476u;
        var rng = new Random(0x50474D);
        ulong[] edges =
        [
            0, ulong.MaxValue, ((ulong)ivHigh << 32) | ivLow,
            ((ulong)(ivHigh - 1u) << 32) | (ivLow - 1u),
            ((ulong)(ivHigh + 1u) << 32) | (ivLow + 1u),
            0x8000_0000_7FFF_FFFF, 0x7FFF_FFFF_8000_0000
        ];

        for (int length = 1; length <= 16; length++)
        {
            foreach (ulong keyId in edges)
            {
                Check(keyId, length);
            }

            for (int i = 0; i < 64; i++)
            {
                ulong keyId = ((ulong)rng.NextInt64(1L << 32) << 32) | (uint)rng.NextInt64(1L << 32);
                Check(keyId, length);
            }
        }

        void Check(ulong keyId, int length)
        {
            KeyIdPattern pattern = KeyIdPattern.Parse(keyId.ToString("X16")[^length..]);
            PatternPlan plan = PatternPlan.From(pattern);
            uint lowState = BitOperations.RotateLeft(unchecked((uint)keyId - ivLow), 2);
            uint highState = BitOperations.RotateLeft(unchecked((uint)(keyId >> 32) - ivHigh), 2);
            Assert.True((lowState & plan.LowMask) == plan.LowTarget
                && (highState & plan.HighMask) == plan.HighTarget);

            for (int bit = 0; bit < 32; bit++)
            {
                Compare(lowState ^ (1u << bit), highState);
                Compare(lowState, highState ^ (1u << bit));
            }

            void Compare(uint low, uint high)
            {
                ulong output = ((ulong)unchecked(BitOperations.RotateLeft(high, 30) + ivHigh) << 32)
                    | unchecked(BitOperations.RotateLeft(low, 30) + ivLow);
                Assert.Equal(pattern.Matches(output),
                    (low & plan.LowMask) == plan.LowTarget && (high & plan.HighMask) == plan.HighTarget);
            }
        }
    }

    [Fact]
    public void InversePredicateRejectsNoncanonicalMasksAndTargets()
    {
        KeyIdPattern pattern = KeyIdPattern.Parse("ABC");
        Assert.Throws<ArgumentException>(() => PatternPlan.From(pattern with { Mask = 0xF0Fu }));
        Assert.Throws<ArgumentException>(() => PatternPlan.From(pattern with { Target = 0x1ABCu }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatternPlan.From(pattern with { FixedHexLength = 17 }));
    }
}
