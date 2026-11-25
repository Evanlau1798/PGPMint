using PGPMint.Cli;
using Xunit;

namespace PGPMint.Cli.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("search --password secret")]
    [InlineData("search --seconds -1")]
    [InlineData("benchmark --mode unexpected")]
    [InlineData("verify --workgroup 63")]
    [InlineData("devices --backend unknown")]
    [InlineData("devices --json --json")]
    [InlineData("devices --pattern ABC")]
    public void InvalidOrSecretArgumentsAreRejected(string args) =>
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split(' ')));

    [Fact]
    public void SearchAcceptsStdinPasswordWithoutPuttingSecretInArguments()
    {
        var options = CommandLine.Parse(["search", "--pattern", "BEEF", "--password-stdin", "--seconds", "3"]);
        Assert.Equal("search", options.Command);
        Assert.Equal("BEEF", options.Get("pattern"));
        Assert.True(options.Has("password-stdin"));
    }

    [Fact]
    public void DefaultPasswordVaultNeverSharesTheWindowsDpapiDirectory()
    {
        string expectedName = OperatingSystem.IsWindows() ? "PGPMint-Portable" : "PGPMint";
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), expectedName),
            Program.DataDirectory(CommandLine.Parse(["search", "--pattern", "A"])));
    }
}
