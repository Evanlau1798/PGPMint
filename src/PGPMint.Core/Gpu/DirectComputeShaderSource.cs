using System.Numerics;
using System.Text;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Gpu;

public static class DirectComputeShaderSource
{
    public const string KernelVersion = "d3d11-sha1-four-v4";
    public const int ThreadGroupSize = 128;

    private static readonly string[] PhaseMixExpressions =
    {
        "(d ^ (b & (c ^ d)))",
        "(b ^ c ^ d)",
        "(b ^ ((b ^ c) & (b ^ d)))",
        "(b ^ c ^ d)"
    };

    private static readonly string[] LaneComponents = { "x", "y", "z", "w" };

    public static string BuildFourKeyHlsl()
    {
        uint[] templateFixed = Sha1ScheduleHelper.TemplateFixedSchedule;
        uint[] masks = Sha1ScheduleHelper.TimestampMasks;
        TimestampXorPlan xorPlan = Sha1ScheduleHelper.SharedXorPlan;

        var sb = new StringBuilder(32_768);
        sb.AppendLine("struct Sha1State { uint a; uint b; uint c; uint d; uint e; };");
        sb.AppendLine();
        sb.AppendLine("#define ROTL(x, n) (((x) << (n)) | ((x) >> (32u - (n))))");
        sb.AppendLine("#define MIX0(b, c, d) ((d) ^ ((b) & ((c) ^ (d))))");
        sb.AppendLine("#define MIX3(b, c, d) ((b) ^ (c) ^ (d))");
        sb.AppendLine("#define STEP(s, word, mix) { \\");
        sb.AppendLine("    uint b = (s).b, c = (s).c, d = (s).d; \\");
        sb.AppendLine("    uint next = ((s).e + (mix) + ROTL((s).a, 5u)) + (word); \\");
        sb.AppendLine("    (s).e = d; (s).d = c; (s).c = ROTL(b, 30u); \\");
        sb.AppendLine("    (s).b = (s).a; (s).a = next; \\");
        sb.AppendLine("}");
        sb.AppendLine("#define MATCH_STATE(a, m, t) ((m) == 0xffffffffu ? (a) == (t) : (((a) & (m)) == (t)))");
        sb.AppendLine();
        sb.AppendLine("cbuffer SearchParams : register(b0)");
        sb.AppendLine("{");
        sb.AppendLine("    uint firstTimestamp;");
        sb.AppendLine("    uint count;");
        sb.AppendLine("    uint maskLow;");
        sb.AppendLine("    uint targetLow;");
        sb.AppendLine("    uint maskHigh;");
        sb.AppendLine("    uint targetHigh;");
        sb.AppendLine("    uint capacity;");
        sb.AppendLine("    uint totalThreads;");
        sb.AppendLine("    uint resultMode;");
        sb.AppendLine("    uint targetLowNoK3;");
        sb.AppendLine("    uint2 _pad;");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("cbuffer KeySchedule : register(b1)");
        sb.AppendLine("{");
        sb.AppendLine($"    uint4 blocks[{Sha1ScheduleHelper.ScheduleRounds}];");
        sb.AppendLine("    uint4 phaseK;");
        sb.AppendLine("};");
        sb.AppendLine();
        sb.AppendLine("RWStructuredBuffer<uint> matches : register(u0);");
        sb.AppendLine();
        sb.AppendLine($"[numthreads({ThreadGroupSize}, 1, 1)]");
        sb.AppendLine("void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)");
        sb.AppendLine("{");
        sb.AppendLine("    for (uint offset = dispatchThreadId.x; offset < count; offset += totalThreads)");
        sb.AppendLine("    {");
        sb.AppendLine("        uint timestamp = firstTimestamp + offset;");
        sb.AppendLine("        Sha1State sharedState = { 0x67452301u, 0xefcdab89u, 0x98badcfeu, 0x10325476u, 0xc3d2e1f0u };");

        for (int t = 0; t < 5; t++)
        {
            string mix = PhaseMixExpressions[t / 20];
            string wordExpr = t == 1 ? "(timestamp + phaseK.x)" : $"0x{templateFixed[t]:x8}u";
            sb.AppendLine($"        STEP(sharedState, {wordExpr}, {mix});");
        }

        sb.AppendLine("        Sha1State s0 = sharedState, s1 = sharedState, s2 = sharedState, s3 = sharedState;");

        foreach (int shift in xorPlan.RequiredRotations)
        {
            sb.AppendLine($"        uint r{shift} = ROTL(timestamp, {shift}u);");
        }

        foreach (TimestampXorStep step in xorPlan.Steps)
        {
            sb.AppendLine($"        uint {MaskVarName(step.ResultMask)} = {MaskVarName(step.LeftMask)} ^ {MaskVarName(step.RightMask)};");
        }

        // Round 5: shares (e + Ch + rotl(a, 5)) and rotl(b, 30) across all 4 lanes.
        sb.AppendLine("        uint base5 = (sharedState.e + MIX0(sharedState.b, sharedState.c, sharedState.d)) + ROTL(sharedState.a, 5u);");
        sb.AppendLine("        uint c5 = ROTL(sharedState.b, 30u);");
        for (int lane = 0; lane < 4; lane++)
        {
            string comp = LaneComponents[lane];
            sb.AppendLine($"        s{lane}.e = sharedState.d; s{lane}.d = sharedState.c; s{lane}.c = c5; s{lane}.b = sharedState.a; s{lane}.a = base5 + blocks[5].{comp};");
        }

        // Round 6: shares (e + Ch) and rotl(b, 30) across all 4 lanes.
        sb.AppendLine("        uint base6 = s0.e + MIX0(s0.b, s0.c, s0.d);");
        sb.AppendLine("        uint c6 = ROTL(s0.b, 30u);");
        for (int lane = 0; lane < 4; lane++)
        {
            string comp = LaneComponents[lane];
            sb.AppendLine($"        {{ uint nx = (base6 + ROTL(s{lane}.a, 5u)) + blocks[6].{comp}; s{lane}.e = s{lane}.d; s{lane}.d = s{lane}.c; s{lane}.c = c6; s{lane}.b = s{lane}.a; s{lane}.a = nx; }}");
        }

        for (int t = 7; t < Sha1ScheduleHelper.ScheduleRounds - 1; t++)
        {
            int phase = t / 20;
            string mix = PhaseMixExpressions[phase];
            string kComp = LaneComponents[phase];
            uint mask = masks[t];

            if (t == 75)
            {
                string tw75 = MaskVarName(masks[75]);
                string tw76 = MaskVarName(masks[76]);
                for (int lane = 0; lane < 4; lane++)
                {
                    string comp = LaneComponents[lane];
                    sb.AppendLine($"        {{ uint a76_nk = (s{lane}.e + MIX3(s{lane}.b, s{lane}.c, s{lane}.d) + ROTL(s{lane}.a, 5u)) + (blocks[75].{comp} ^ {tw75});");
                    sb.AppendLine("          bool lowMatches = maskLow == 0xffffffffu ? (a76_nk == targetLowNoK3) : (((a76_nk + phaseK.w) & maskLow) == targetLow);");
                    sb.AppendLine("          [branch] if (lowMatches) {");
                    sb.AppendLine("              bool highMatches = true;");
                    sb.AppendLine("              [branch] if (maskHigh != 0u) {");
                    sb.AppendLine("                  uint a76 = a76_nk + phaseK.w;");
                    sb.AppendLine($"                  uint c76 = ROTL(s{lane}.b, 30u);");
                    sb.AppendLine($"                  uint a77 = (s{lane}.d + MIX3(s{lane}.a, c76, s{lane}.c) + ROTL(a76, 5u)) + ((blocks[76].{comp} ^ {tw76}) + phaseK.w);");
                    sb.AppendLine("                  highMatches = MATCH_STATE(a77, maskHigh, targetHigh);");
                    sb.AppendLine("              }");
                    sb.AppendLine("              [branch] if (highMatches) {");
                    sb.AppendLine($"                  uint slot = {lane}u * (capacity + 1u);");
                    sb.AppendLine("                  uint idx;");
                    sb.AppendLine("                  if (resultMode != 0u) {");
                    sb.AppendLine("                      InterlockedCompareExchange(matches[slot], 0u, 1u, idx);");
                    sb.AppendLine("                  } else {");
                    sb.AppendLine("                      InterlockedAdd(matches[slot], 1u, idx);");
                    sb.AppendLine("                  }");
                    sb.AppendLine("                  if (idx < capacity) {");
                    sb.AppendLine("                      matches[slot + 1u + idx] = timestamp;");
                    sb.AppendLine("                  }");
                    sb.AppendLine("              }");
                    sb.AppendLine("          } }");
                }
                continue;
            }

            for (int lane = 0; lane < 4; lane++)
            {
                string baseExpr = t is 14 or 15
                    ? $"0x{templateFixed[t]:x8}u"
                    : $"blocks[{t}].{LaneComponents[lane]}";

                string wordExpr = mask == 0u
                    ? baseExpr
                    : $"(({baseExpr} ^ {MaskVarName(mask)}) + phaseK.{kComp})";

                sb.AppendLine($"        STEP(s{lane}, {wordExpr}, {mix});");
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string MaskVarName(uint mask) =>
        mask == 1u
            ? "timestamp"
            : BitOperations.IsPow2(mask)
                ? $"r{BitOperations.TrailingZeroCount(mask)}"
                : $"tw_{mask:x8}";
}
