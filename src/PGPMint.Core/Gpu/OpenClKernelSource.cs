using System.Numerics;
using System.Text;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Gpu;

public static class OpenClKernelSource
{
    public const string KernelVersion = "opencl-sha1-four-v4";

    private static readonly string[] PhaseMixExpressions =
    {
        "bitselect(d, c, b)",
        "b ^ c ^ d",
        "bitselect(b, c, b ^ d)",
        "b ^ c ^ d"
    };

    public static string BuildFourKeyOpenCl(bool useByValueSchedule = true)
    {
        uint[] templateFixed = Sha1ScheduleHelper.TemplateFixedSchedule;
        uint[] masks = Sha1ScheduleHelper.TimestampMasks;
        TimestampXorPlan xorPlan = Sha1ScheduleHelper.SharedXorPlan;
        string schedAccessor = useByValueSchedule ? "sched." : "sched->";
        string schedParamDecl = useByValueSchedule ? "const ScheduleParam sched" : "__constant ScheduleParam *sched";

        var sb = new StringBuilder(32_768);
        sb.AppendLine($"typedef struct {{ uint w[{Sha1ScheduleHelper.ScheduleRounds * 4}]; uint k[4]; }} ScheduleParam;");
        sb.AppendLine("typedef struct { uint a, b, c, d, e; } Sha1State;");
        sb.AppendLine();
        sb.AppendLine("#define MIX0(b, c, d) bitselect((d), (c), (b))");
        sb.AppendLine("#define MIX1(b, c, d) ((b) ^ (c) ^ (d))");
        sb.AppendLine("#define MIX2(b, c, d) bitselect((b), (c), (b) ^ (d))");
        sb.AppendLine("#define MIX3(b, c, d) ((b) ^ (c) ^ (d))");
        sb.AppendLine("#define STEP(s, word, mix) do { \\");
        sb.AppendLine("    const uint b = (s).b, c = (s).c, d = (s).d; \\");
        sb.AppendLine("    const uint next = ((s).e + (mix) + rotate((s).a, 5u)) + (word); \\");
        sb.AppendLine("    (s).e = d; (s).d = c; (s).c = rotate(b, 30u); \\");
        sb.AppendLine("    (s).b = (s).a; (s).a = next; \\");
        sb.AppendLine("} while (0)");
        sb.AppendLine();
        sb.AppendLine("#define MATCH_STATE(a, m, t) ((m) == 0xffffffffu ? (a) == (t) : (((a) & (m)) == (t)))");
        sb.AppendLine();
        sb.AppendLine($"__kernel void search({schedParamDecl}, __global uint *matches,");
        sb.AppendLine("                     uint first, uint count, uint maskLow, uint targetLow, uint maskHigh, uint targetHigh, uint capacity, uint resultMode) {");
        sb.AppendLine("    const uint stride = get_global_size(0);");
        sb.AppendLine("    const uint targetLowNoK3 = targetLow - 0xca62c1d6u;");
        sb.AppendLine("    #pragma unroll 1");
        sb.AppendLine("    for (uint offset = get_global_id(0); offset < count; offset += stride) {");
        sb.AppendLine("        const uint ts = first + offset;");
        sb.AppendLine("        Sha1State shared = {0x67452301u, 0xefcdab89u, 0x98badcfeu, 0x10325476u, 0xc3d2e1f0u};");

        for (int t = 0; t < 5; t++)
        {
            string mix = PhaseMixExpressions[t / 20];
            string word = t == 1 ? $"(ts + {schedAccessor}k[0])" : $"0x{templateFixed[t]:x8}u";
            sb.AppendLine($"        STEP(shared, {word}, {mix});");
        }

        sb.AppendLine("        Sha1State s0 = shared, s1 = shared, s2 = shared, s3 = shared;");

        foreach (int shift in xorPlan.RequiredRotations)
        {
            sb.AppendLine($"        const uint r{shift} = rotate(ts, {shift}u);");
        }

        foreach (TimestampXorStep step in xorPlan.Steps)
        {
            sb.AppendLine($"        const uint {MaskVarName(step.ResultMask)} = {MaskVarName(step.LeftMask)} ^ {MaskVarName(step.RightMask)};");
        }

        // Round 5: shares (e + Ch + rotl(a, 5)) and rotl(b, 30) across all 4 lanes.
        sb.AppendLine("        const uint base5 = (shared.e + MIX0(shared.b, shared.c, shared.d)) + rotate(shared.a, 5u);");
        sb.AppendLine("        const uint c5 = rotate(shared.b, 30u);");
        for (int lane = 0; lane < 4; lane++)
        {
            sb.AppendLine($"        s{lane}.e = shared.d; s{lane}.d = shared.c; s{lane}.c = c5; s{lane}.b = shared.a; s{lane}.a = base5 + {schedAccessor}w[{5 * 4 + lane}];");
        }

        // Round 6: shares (e + Ch) and rotl(b, 30) across all 4 lanes.
        sb.AppendLine("        const uint base6 = s0.e + MIX0(s0.b, s0.c, s0.d);");
        sb.AppendLine("        const uint c6 = rotate(s0.b, 30u);");
        for (int lane = 0; lane < 4; lane++)
        {
            sb.AppendLine($"        {{ const uint nx = (base6 + rotate(s{lane}.a, 5u)) + {schedAccessor}w[{6 * 4 + lane}]; s{lane}.e = s{lane}.d; s{lane}.d = s{lane}.c; s{lane}.c = c6; s{lane}.b = s{lane}.a; s{lane}.a = nx; }}");
        }

        for (int t = 7; t < Sha1ScheduleHelper.ScheduleRounds - 1; t++)
        {
            int phase = t / 20;
            string mix = PhaseMixExpressions[phase];
            uint mask = masks[t];

            if (t == 75)
            {
                string tw75 = MaskVarName(masks[75]);
                string tw76 = MaskVarName(masks[76]);
                for (int lane = 0; lane < 4; lane++)
                {
                    string w75 = $"{schedAccessor}w[{75 * 4 + lane}]";
                    string w76 = $"{schedAccessor}w[{76 * 4 + lane}]";
                    sb.AppendLine($"        {{ const uint a76_nk = (s{lane}.e + MIX3(s{lane}.b, s{lane}.c, s{lane}.d) + rotate(s{lane}.a, 5u)) + ({w75} ^ {tw75});");
                    sb.AppendLine($"          const bool lowMatches = maskLow == 0xffffffffu ? (a76_nk == targetLowNoK3) : (((a76_nk + {schedAccessor}k[3]) & maskLow) == targetLow);");
                    sb.AppendLine("          if (lowMatches) {");
                    sb.AppendLine("              bool highMatches = true;");
                    sb.AppendLine("              if (maskHigh != 0u) {");
                    sb.AppendLine($"                  const uint a76 = a76_nk + {schedAccessor}k[3];");
                    sb.AppendLine($"                  const uint c76 = rotate(s{lane}.b, 30u);");
                    sb.AppendLine($"                  const uint a77 = (s{lane}.d + MIX3(s{lane}.a, c76, s{lane}.c) + rotate(a76, 5u)) + (({w76} ^ {tw76}) + {schedAccessor}k[3]);");
                    sb.AppendLine("                  highMatches = MATCH_STATE(a77, maskHigh, targetHigh);");
                    sb.AppendLine("              }");
                    sb.AppendLine("              if (highMatches) {");
                    sb.AppendLine($"                  __global uint *out = matches + {lane}u * (capacity + 1u);");
                    sb.AppendLine("                  const uint index = resultMode != 0u ? atomic_cmpxchg(out, 0u, 1u) : atomic_inc(out);");
                    sb.AppendLine("                  if (index < capacity) out[1u + index] = ts;");
                    sb.AppendLine("              }");
                    sb.AppendLine("          } }");
                }
                continue;
            }

            for (int lane = 0; lane < 4; lane++)
            {
                string baseExpr = t is 14 or 15
                    ? $"0x{templateFixed[t]:x8}u"
                    : $"{schedAccessor}w[{t * 4 + lane}]";
                string wordExpr = mask == 0u
                    ? baseExpr
                    : $"(({baseExpr} ^ {MaskVarName(mask)}) + {schedAccessor}k[{phase}])";
                sb.AppendLine($"        STEP(s{lane}, {wordExpr}, {mix});");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string MaskVarName(uint mask) =>
        mask == 1u
            ? "ts"
            : BitOperations.IsPow2(mask)
                ? $"r{BitOperations.TrailingZeroCount(mask)}"
                : $"tw_{mask:x8}";
}
