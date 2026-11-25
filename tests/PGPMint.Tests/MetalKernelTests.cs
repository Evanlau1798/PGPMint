using System.Runtime.InteropServices;
using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public sealed class MetalKernelTests
{
    [Fact]
    public void MetalKernelPreservesOpenClFourLaneSha1Math()
    {
        string metal = MetalKernelSource.BuildFourKeyMetal();
        string openCl = OpenClKernelSource.BuildFourKeyOpenCl();
        // Compare arithmetic statements, independently of the platform's buffer/dispatch syntax.
        string[] expected = MathLines(openCl);
        Assert.NotEmpty(expected);
        Assert.Equal(expected, MathLines(metal));
        Assert.Contains("[[buffer(0)]]", metal);
        Assert.Contains("[[buffer(1)]]", metal);
        Assert.Contains("[[buffer(2)]]", metal);
        Assert.Contains("[[thread_position_in_grid]]", metal);
        Assert.Contains("[[threads_per_grid]]", metal);
        Assert.Contains("atomic_exchange_explicit", metal);
        Assert.Contains("atomic_fetch_add_explicit", metal);
        Assert.DoesNotContain("get_global_", metal);
        Assert.DoesNotContain("__kernel", metal);
    }

    [Fact]
    public void DispatchParametersHaveEightUintAbiLayout()
    {
        Assert.Equal(32, Marshal.SizeOf<MetalDispatchParameters>());
        var p = new MetalDispatchParameters(1, 2, 3, 4, 5, 6, 7, 8);
        ReadOnlySpan<MetalDispatchParameters> values = [p];
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }, MemoryMarshal.Cast<MetalDispatchParameters, uint>(values).ToArray());
    }

    private static string[] MathLines(string source) => source.Split('\n')
        .Select(line => line.Trim().Replace("rol(", "rotate(", StringComparison.Ordinal)
            .Replace("bitselect(d, c, b)", "(d ^ (b & (c ^ d)))", StringComparison.Ordinal)
            .Replace("bitselect(b, c, b ^ d)", "((b & c) | (d & (b | c)))", StringComparison.Ordinal))
        .Where(line => line.StartsWith("STEP(", StringComparison.Ordinal)
            || line.StartsWith("Sha1State ", StringComparison.Ordinal)
            || line.StartsWith("const uint r", StringComparison.Ordinal)
            || line.StartsWith("const uint tw_", StringComparison.Ordinal)
            || line.StartsWith("const uint base", StringComparison.Ordinal)
            || line.StartsWith("const uint c5", StringComparison.Ordinal)
            || line.StartsWith("const uint c6", StringComparison.Ordinal)
            || line.StartsWith("s0.", StringComparison.Ordinal) || line.StartsWith("s1.", StringComparison.Ordinal)
            || line.StartsWith("s2.", StringComparison.Ordinal) || line.StartsWith("s3.", StringComparison.Ordinal)
            || line.StartsWith("{ const uint", StringComparison.Ordinal)
            || line.StartsWith("const bool lowMatches", StringComparison.Ordinal)
            || line.StartsWith("const uint a76", StringComparison.Ordinal)
            || line.StartsWith("const uint c76", StringComparison.Ordinal)
            || line.StartsWith("const uint a77", StringComparison.Ordinal)
            || line.StartsWith("highMatches =", StringComparison.Ordinal))
        .ToArray();
}
