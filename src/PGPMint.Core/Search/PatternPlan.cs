using System.Numerics;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Search;

internal readonly record struct PatternPlan(
    uint LowMask,
    uint LowTarget,
    uint HighMask,
    uint HighTarget,
    uint LowTargetExactNoK3)
{
    private const uint LowInitialWord = 0xC3D2E1F0u;

    internal static PatternPlan From(KeyIdPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        int length = pattern.FixedHexLength;
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, KeyIdPattern.KeyIdHexLength);
        if (pattern.Mask == 0 ||
            BitOperations.PopCount(pattern.Mask) != length * 4 ||
            (pattern.Target & ~pattern.Mask) != 0)
        {
            throw new ArgumentException("Expected a canonical nibble-aligned hexadecimal mask and target.", nameof(pattern));
        }

        for (int nibble = 0; nibble < KeyIdPattern.KeyIdHexLength; nibble++)
        {
            ulong nibbleMask = (pattern.Mask >> (nibble * 4)) & 0xFUL;
            if (nibbleMask != 0UL && nibbleMask != 0xFUL)
            {
                throw new ArgumentException("Mask bits must be nibble-aligned.", nameof(pattern));
            }
        }

        uint exactLowTargetNoK3 = unchecked(
            BitOperations.RotateLeft(unchecked(pattern.TargetLow - LowInitialWord), 2)
            - Sha1ScheduleHelper.PhaseConstants[3]);

        return new PatternPlan(
            LowMask: pattern.MaskLow,
            LowTarget: pattern.TargetLow,
            HighMask: pattern.MaskHigh,
            HighTarget: pattern.TargetHigh,
            LowTargetExactNoK3: exactLowTargetNoK3);
    }
}
