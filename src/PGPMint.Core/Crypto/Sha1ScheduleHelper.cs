using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PGPMint.Core.Crypto;

internal readonly record struct TimestampXorStep(uint ResultMask, uint LeftMask, uint RightMask);

internal sealed record TimestampXorPlan(
    int[] RequiredRotations,
    TimestampXorStep[] Steps);

public static class Sha1ScheduleHelper
{
    public const int ScheduleRounds = 77;

    public static readonly uint[] PhaseConstants =
    {
        0x5A827999u,
        0x6ED9EBA1u,
        0x8F1BBCDCu,
        0xCA62C1D6u
    };

    private static readonly ConditionalWeakTable<byte[], uint[]> ScheduleCache = new();

    // Linearity under XOR: bit k of TimestampMasks[t] indicates rotl32(timestamp, k) enters W[t].
    public static uint[] TimestampMasks { get; } = BuildTimestampMasks();

    // Fixed schedule for the template header with zero public key (rounds 0..4, 14, 15 are key-independent).
    public static uint[] TemplateFixedSchedule { get; } = ComputeFixedSchedule(new byte[Ed25519KeyGenerator.KeyByteLength]);

    internal static TimestampXorPlan SharedXorPlan { get; } = BuildTimestampXorPlan();

    public static uint[] GetOrComputeFixedSchedule(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(publicKey));
        }

        return ScheduleCache.GetValue(publicKey, static key => ComputeFixedSchedule(key));
    }

    private static TimestampXorPlan BuildTimestampXorPlan()
    {
        uint[] masks = TimestampMasks;
        var requiredShifts = new SortedSet<int>();
        for (int t = 5; t < ScheduleRounds; t++)
        {
            uint mask = masks[t];
            while (mask != 0u)
            {
                int shift = BitOperations.TrailingZeroCount(mask);
                if (shift > 0)
                {
                    requiredShifts.Add(shift);
                }
                mask &= mask - 1u;
            }
        }

        var built = new HashSet<uint> { 1u };
        foreach (int shift in requiredShifts)
        {
            built.Add(1u << shift);
        }

        var steps = new List<TimestampXorStep>();
        for (int t = 5; t < ScheduleRounds; t++)
        {
            uint mask = masks[t];
            if (mask == 0u || built.Contains(mask))
            {
                continue;
            }

            // First check if mask equals XOR of any two already-materialized masks.
            bool paired = false;
            foreach (uint existing in built)
            {
                uint complement = mask ^ existing;
                if (complement != existing && built.Contains(complement))
                {
                    steps.Add(new TimestampXorStep(mask, existing, complement));
                    built.Add(mask);
                    paired = true;
                    break;
                }
            }

            if (paired)
            {
                continue;
            }

            // Otherwise extend the largest already-built bit-subset of mask one bit at a time.
            uint bestSub = 0u;
            int bestBits = 0;
            foreach (uint candidate in built)
            {
                if ((candidate & mask) == candidate)
                {
                    int bits = BitOperations.PopCount(candidate);
                    if (bits > bestBits)
                    {
                        bestSub = candidate;
                        bestBits = bits;
                    }
                }
            }

            uint current = bestSub;
            uint remaining = mask ^ current;
            while (remaining != 0u)
            {
                uint lsb = remaining & (~remaining + 1u);
                remaining ^= lsb;
                uint next = current | lsb;
                if (built.Add(next))
                {
                    steps.Add(new TimestampXorStep(next, current, lsb));
                }
                current = next;
            }
        }

        return new TimestampXorPlan(requiredShifts.ToArray(), steps.ToArray());
    }

    private static uint[] BuildTimestampMasks()
    {
        uint[] words = new uint[16];
        words[1] = 1u;
        return ExpandSchedule(words);
    }

    public static uint[] ExpandSchedule(ReadOnlySpan<uint> words16)
    {
        if (words16.Length != 16)
        {
            throw new ArgumentException("SHA-1 message block requires 16 uint32 words.", nameof(words16));
        }

        uint[] schedule = new uint[ScheduleRounds];
        words16.CopyTo(schedule);
        for (int t = 16; t < ScheduleRounds; t++)
        {
            uint mixed = schedule[t - 3] ^ schedule[t - 8] ^ schedule[t - 14] ^ schedule[t - 16];
            schedule[t] = BitOperations.RotateLeft(mixed, 1);
        }

        return schedule;
    }

    public static uint[] ComputeFixedSchedule(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(publicKey));
        }

        Span<byte> block = stackalloc byte[64];
        block.Clear();
        OpenPgpV4Packet.PublicKeyHeaderTemplate.CopyTo(block);
        publicKey.CopyTo(block.Slice(22, Ed25519KeyGenerator.KeyByteLength));
        block[54] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(block.Slice(56, 8), OpenPgpV4Packet.PublicKeyPacketHashInputLength * 8UL);

        uint[] words = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
        }

        words[1] = 0u;
        uint[] schedule = ExpandSchedule(words);
        uint[] masks = TimestampMasks;

        for (int t = 0; t < ScheduleRounds; t++)
        {
            if (masks[t] == 0u)
            {
                schedule[t] = unchecked(schedule[t] + PhaseConstants[t / 20]);
            }
        }

        return schedule;
    }

    public static ulong ComputeKeyIdFromFixedSchedule(ReadOnlySpan<uint> fixedSchedule, uint timestamp)
    {
        if (fixedSchedule.Length != ScheduleRounds)
        {
            throw new ArgumentException($"Fixed schedule must have {ScheduleRounds} words.", nameof(fixedSchedule));
        }

        uint a = 0x67452301u;
        uint b = 0xEFCDAB89u;
        uint c = 0x98BADCFEu;
        uint d = 0x10325476u;
        uint e = 0xC3D2E1F0u;
        uint[] masks = TimestampMasks;

        for (int t = 0; t < ScheduleRounds; t++)
        {
            uint word = fixedSchedule[t];
            uint mask = masks[t];
            if (mask != 0u)
            {
                uint tsXor = 0u;
                for (int shift = 0; shift < 32; shift++)
                {
                    if (((mask >> shift) & 1u) != 0u)
                    {
                        tsXor ^= BitOperations.RotateLeft(timestamp, shift);
                    }
                }
                word = unchecked((word ^ tsXor) + PhaseConstants[t / 20]);
            }

            uint mix = (t / 20) switch
            {
                0 => d ^ (b & (c ^ d)),
                1 => b ^ c ^ d,
                2 => (b & c) | (d & (b | c)),
                _ => b ^ c ^ d
            };

            if (t == 76)
            {
                uint low = unchecked(BitOperations.RotateLeft(a, 30) + 0xC3D2E1F0u);
                uint next77 = unchecked(BitOperations.RotateLeft(a, 5) + mix + e + word);
                uint high = unchecked(BitOperations.RotateLeft(next77, 30) + 0x10325476u);
                return ((ulong)high << 32) | low;
            }

            uint next = unchecked(BitOperations.RotateLeft(a, 5) + mix + e + word);
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = next;
        }

        throw new InvalidOperationException("Unreachable");
    }
}
