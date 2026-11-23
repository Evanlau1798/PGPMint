using System.Text;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Gpu;

public static class DirectComputeShaderSource
{
    public const string KernelVersion = "d3d11-sha1-four-v1";
    public const int ThreadGroupSize = 128;

    private static readonly string[] PhaseMixExpressions =
    {
        "(d ^ (b & (c ^ d)))",
        "(b ^ c ^ d)",
        "((b & c) | (d & (b | c)))",
        "(b ^ c ^ d)"
    };

    private static readonly string[] LaneComponents = { "x", "y", "z", "w" };

    public static string BuildFourKeyHlsl()
    {
        uint[] templateFixed = Sha1ScheduleHelper.TemplateFixedSchedule;
        uint[] masks = Sha1ScheduleHelper.TimestampMasks;
        uint[] constants = Sha1ScheduleHelper.PhaseConstants;

        var sb = new StringBuilder(32_768);
        sb.AppendLine("struct Sha1State { uint a; uint b; uint c; uint d; uint e; };");
        sb.AppendLine();
        sb.AppendLine("#define ROTL(x, n) (((x) << (n)) | ((x) >> (32u - (n))))");
        sb.AppendLine("#define STEP(s, word, mix) { \\");
        sb.AppendLine("    uint b = (s).b, c = (s).c, d = (s).d; \\");
        sb.AppendLine("    uint next = ((word) + (s).e) + ((mix) + ROTL((s).a, 5u)); \\");
        sb.AppendLine("    (s).e = d; (s).d = c; (s).c = ROTL(b, 30u); \\");
        sb.AppendLine("    (s).b = (s).a; (s).a = next; \\");
        sb.AppendLine("}");
        sb.AppendLine("#define CHECK_LAST(s, word, mix, lane) { \\");
        sb.AppendLine("    uint low = ROTL((s).a, 30u) + 0xc3d2e1f0u; \\");
        sb.AppendLine("    if ((low & maskLow) == targetLow) { \\");
        sb.AppendLine("        STEP(s, word, mix); \\");
        sb.AppendLine("        uint high = ROTL((s).a, 30u) + 0x10325476u; \\");
        sb.AppendLine("        if ((high & maskHigh) == targetHigh) { \\");
        sb.AppendLine("            uint slot = (lane) * (capacity + 1u); \\");
        sb.AppendLine("            uint idx; \\");
        sb.AppendLine("            InterlockedAdd(matches[slot], 1u, idx); \\");
        sb.AppendLine("            if (idx < capacity) { \\");
        sb.AppendLine("                matches[slot + 1u + idx] = timestamp; \\");
        sb.AppendLine("            } \\");
        sb.AppendLine("        } \\");
        sb.AppendLine("    } \\");
        sb.AppendLine("}");
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
        sb.AppendLine("    uint4 blocks[77];");
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
            string baseLiteral = $"0x{templateFixed[t]:x8}u";
            string wordExpr = FormatRoundWord(t, baseLiteral, masks[t], constants[t / 20], hoistedTsVar: null);
            sb.AppendLine($"        STEP(sharedState, {wordExpr}, {mix});");
        }

        sb.AppendLine("        Sha1State s0 = sharedState, s1 = sharedState, s2 = sharedState, s3 = sharedState;");

        for (int t = 5; t < Sha1ScheduleHelper.ScheduleRounds; t++)
        {
            string mix = PhaseMixExpressions[t / 20];
            uint mask = masks[t];
            string? tsVar = null;

            if (mask != 0u)
            {
                tsVar = $"tw{t}";
                sb.AppendLine($"        uint {tsVar} = {BuildTimestampXorExpression(mask)};");
            }

            for (int lane = 0; lane < 4; lane++)
            {
                string baseExpr = t is 14 or 15
                    ? $"0x{templateFixed[t]:x8}u"
                    : $"blocks[{t}].{LaneComponents[lane]}";

                string wordExpr = FormatRoundWord(t, baseExpr, mask, constants[t / 20], tsVar);

                if (t == 76)
                {
                    sb.AppendLine($"        CHECK_LAST(s{lane}, {wordExpr}, {mix}, {lane}u);");
                }
                else
                {
                    sb.AppendLine($"        STEP(s{lane}, {wordExpr}, {mix});");
                }
            }
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FormatRoundWord(int t, string baseExpr, uint mask, uint constant, string? hoistedTsVar)
    {
        if (mask == 0u)
        {
            return baseExpr;
        }

        string tsExpr = hoistedTsVar ?? BuildTimestampXorExpression(mask);
        return $"(({baseExpr} ^ ({tsExpr})) + 0x{constant:x8}u)";
    }

    private static string BuildTimestampXorExpression(uint mask)
    {
        var terms = new List<string>(8);
        for (int shift = 0; shift < 32; shift++)
        {
            if (((mask >> shift) & 1u) != 0u)
            {
                terms.Add(shift == 0 ? "timestamp" : $"ROTL(timestamp, {shift}u)");
            }
        }

        return string.Join(" ^ ", terms);
    }
}
