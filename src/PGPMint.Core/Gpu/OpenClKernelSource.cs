using System.Text;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Gpu;

public static class OpenClKernelSource
{
    public const string KernelVersion = "opencl-sha1-four-v1";

    private static readonly string[] PhaseMixExpressions =
    {
        "bitselect(d, c, b)",
        "b ^ c ^ d",
        "(b & c) | (d & (b | c))",
        "b ^ c ^ d"
    };

    public static string BuildFourKeyOpenCl()
    {
        uint[] templateFixed = Sha1ScheduleHelper.TemplateFixedSchedule;
        uint[] masks = Sha1ScheduleHelper.TimestampMasks;
        uint[] constants = Sha1ScheduleHelper.PhaseConstants;

        var sb = new StringBuilder(32_768);
        sb.AppendLine("typedef struct { uint a, b, c, d, e; } Sha1State;");
        sb.AppendLine();
        sb.AppendLine("#define STEP(s, word, mix) do { \\");
        sb.AppendLine("    const uint b = (s).b, c = (s).c, d = (s).d; \\");
        sb.AppendLine("    const uint next = ((word) + (s).e) + ((mix) + rotate((s).a, 5u)); \\");
        sb.AppendLine("    (s).e = d; (s).d = c; (s).c = rotate(b, 30u); \\");
        sb.AppendLine("    (s).b = (s).a; (s).a = next; \\");
        sb.AppendLine("} while (0)");
        sb.AppendLine();
        sb.AppendLine("#define CHECK_LAST(s, word, mix, lane) do { \\");
        sb.AppendLine("    const uint low = rotate((s).a, 30u) + 0xc3d2e1f0u; \\");
        sb.AppendLine("    if ((low & (uint)mask) == (uint)target) { \\");
        sb.AppendLine("        STEP(s, word, mix); \\");
        sb.AppendLine("        const uint high = rotate((s).a, 30u) + 0x10325476u; \\");
        sb.AppendLine("        if (((((ulong)high << 32) | low) & mask) == target) { \\");
        sb.AppendLine("            __global uint *out = matches + (lane) * (capacity + 1u); \\");
        sb.AppendLine("            const uint index = atomic_inc(out); \\");
        sb.AppendLine("            if (index < capacity) out[1u + index] = timestamp; \\");
        sb.AppendLine("        } \\");
        sb.AppendLine("    } \\");
        sb.AppendLine("} while (0)");
        sb.AppendLine();
        sb.AppendLine("__kernel void search(__constant uint *blocks, __global uint *matches,");
        sb.AppendLine("                     uint first, uint count, ulong mask, ulong target, uint capacity) {");
        sb.AppendLine("    const uint stride = get_global_size(0);");
        sb.AppendLine("    #pragma unroll 2");
        sb.AppendLine("    for (uint offset = get_global_id(0); offset < count; offset += stride) {");
        sb.AppendLine("        const uint timestamp = first + offset;");
        sb.AppendLine("        Sha1State shared = {0x67452301u, 0xefcdab89u, 0x98badcfeu, 0x10325476u, 0xc3d2e1f0u};");

        for (int t = 0; t < 5; t++)
        {
            string mix = PhaseMixExpressions[t / 20];
            string word = FormatRoundWord(t, $"0x{templateFixed[t]:x8}u", masks[t], constants[t / 20]);
            sb.AppendLine($"        STEP(shared, {word}, {mix});");
        }

        sb.AppendLine("        Sha1State s0 = shared, s1 = shared, s2 = shared, s3 = shared;");

        for (int t = 5; t < Sha1ScheduleHelper.ScheduleRounds; t++)
        {
            string mix = PhaseMixExpressions[t / 20];
            for (int lane = 0; lane < 4; lane++)
            {
                int index = lane * Sha1ScheduleHelper.ScheduleRounds + t;
                string baseExpr = t is 14 or 15
                    ? $"0x{templateFixed[t]:x8}u"
                    : $"blocks[{index}]";
                string word = FormatRoundWord(t, baseExpr, masks[t], constants[t / 20]);

                if (t == 76)
                {
                    sb.AppendLine($"        CHECK_LAST(s{lane}, {word}, {mix}, {lane}u);");
                }
                else
                {
                    sb.AppendLine($"        STEP(s{lane}, {word}, {mix});");
                }
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FormatRoundWord(int t, string baseExpr, uint mask, uint constant)
    {
        if (mask == 0u)
        {
            return baseExpr;
        }

        var rotations = new List<string>(8);
        for (int shift = 0; shift < 32; shift++)
        {
            if (((mask >> shift) & 1u) != 0u)
            {
                rotations.Add($"rotate(timestamp, {shift}u)");
            }
        }

        return $"({baseExpr} ^ {string.Join(" ^ ", rotations)}) + 0x{constant:x8}u";
    }
}
