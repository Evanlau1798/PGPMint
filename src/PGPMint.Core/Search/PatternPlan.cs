using System.Numerics;

namespace PGPMint.Core.Search;

internal readonly record struct PatternPlan(uint LowMask, uint LowTarget, uint HighMask, uint HighTarget)
{
    private const uint LowInitialWord = 0xC3D2E1F0u;
    private const uint HighInitialWord = 0x10325476u;

    internal static PatternPlan From(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        int length = pattern.FixedHexLength;
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, KeyIdPattern.KeyIdHexLength);
        ulong expectedMask = length == KeyIdPattern.KeyIdHexLength ? ulong.MaxValue : (1UL << (4 * length)) - 1;
        if (pattern.Mask != expectedMask || (pattern.Target & ~expectedMask) != 0)
        {
            throw new ArgumentException("Expected a canonical hexadecimal suffix.", nameof(pattern));
        }

        return new PatternPlan(
            LowMask: BitOperations.RotateLeft(pattern.MaskLow, 2),
            LowTarget: BitOperations.RotateLeft(unchecked(pattern.TargetLow - LowInitialWord) & pattern.MaskLow, 2),
            HighMask: BitOperations.RotateLeft(pattern.MaskHigh, 2),
            HighTarget: BitOperations.RotateLeft(unchecked(pattern.TargetHigh - HighInitialWord) & pattern.MaskHigh, 2));
    }
}
