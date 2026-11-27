using System.Numerics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using Xunit;

namespace PGPMint.Tests;

public sealed class PatternPlanTests
{
    [Fact]
    public void InversePredicateAndRawMasksMatchOutputAtEverySuffixLengthAndWildcards()
    {
        const uint ivLow = 0xC3D2E1F0u;
        const uint ivHigh = 0x10325476u;
        uint k3 = Sha1ScheduleHelper.PhaseConstants[3];
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
                Check(KeyIdPattern.Parse(keyId.ToString("X16")[^length..]), keyId);
            }

            for (int i = 0; i < 64; i++)
            {
                ulong keyId = ((ulong)rng.NextInt64(1L << 32) << 32) | (uint)rng.NextInt64(1L << 32);
                Check(KeyIdPattern.Parse(keyId.ToString("X16")[^length..]), keyId);
            }
        }

        // Exercise prefix and arbitrary wildcard positions across both 32-bit halves.
        Check(KeyIdPattern.Parse("DEAD************"), 0xDEAD123456789ABCul);
        Check(KeyIdPattern.Parse("CAFE*123"), 0x12345678CAFE0123ul);
        Check(KeyIdPattern.Parse("A*******B*******"), 0xA0001111B2223333ul);
        Check(KeyIdPattern.Parse("*1*2*3*4*5*6*7*8"), 0x0102030405060708ul);

        void Check(KeyIdPattern pattern, ulong keyId)
        {
            PatternPlan plan = PatternPlan.From(pattern);
            uint a76Nk = unchecked(BitOperations.RotateLeft(unchecked((uint)keyId - ivLow), 2) - k3);
            uint a76 = unchecked(a76Nk + k3);
            uint a77 = BitOperations.RotateLeft(unchecked((uint)(keyId >> 32) - ivHigh), 2);
            Assert.True(ShaderMatches(plan, a76Nk, a76, a77));

            for (int bit = 0; bit < 32; bit++)
            {
                Compare(a76Nk ^ (1u << bit), a76 ^ (1u << bit), a77);
                Compare(a76Nk, a76, a77 ^ (1u << bit));
            }

            void Compare(uint testA76Nk, uint testA76, uint testA77)
            {
                ulong output = ((ulong)unchecked(BitOperations.RotateLeft(testA77, 30) + ivHigh) << 32)
                    | unchecked(BitOperations.RotateLeft(testA76, 30) + ivLow);
                Assert.Equal(pattern.Matches(output), ShaderMatches(plan, testA76Nk, testA76, testA77));
            }
        }

        bool ShaderMatches(PatternPlan plan, uint a76Nk, uint a76, uint a77)
        {
            bool lowOk = plan.LowMask == 0xFFFFFFFFu
                ? a76Nk == plan.LowTargetExactNoK3
                : (plan.LowMask == 0u || ((unchecked(BitOperations.RotateLeft(a76, 30) + ivLow) & plan.LowMask) == plan.LowTarget));
            if (!lowOk)
            {
                return false;
            }

            return plan.HighMask == 0u ||
                   ((unchecked(BitOperations.RotateLeft(a77, 30) + ivHigh) & plan.HighMask) == plan.HighTarget);
        }
    }

    [Fact]
    public void InversePredicateRejectsNoncanonicalMasksAndTargets()
    {
        KeyIdPattern pattern = KeyIdPattern.Parse("ABC");
        Assert.Throws<ArgumentException>(() => PatternPlan.From(pattern with { Mask = 0xF0Fu }));
        Assert.Throws<ArgumentException>(() => PatternPlan.From(pattern with { Mask = 0xFFEu, FixedHexLength = 3 }));
        Assert.Throws<ArgumentException>(() => PatternPlan.From(pattern with { Target = 0x1ABCu }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatternPlan.From(pattern with { FixedHexLength = 17 }));
    }
}
