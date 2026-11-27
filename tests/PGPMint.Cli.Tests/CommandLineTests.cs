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

    [Theory]
    [InlineData("search --pattern BEEF --tier unknown")]
    [InlineData("search --pattern BEEF --max-temp 64")]
    [InlineData("search --pattern BEEF --max-temp 91")]
    [InlineData("search --pattern BEEF --status-interval 0")]
    [InlineData("search --pattern BEEF --on-hit-exec ./does-not-exist.exe")]
    [InlineData("curve --settle-seconds 0")]
    [InlineData("curve --measure-seconds 0")]
    public void InvalidPhase1CliOptionsAreRejected(string args) =>
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split(' ')));

    [Fact]
    public void SearchAndCurveAcceptPhase1Options()
    {
        var search = CommandLine.Parse([
            "search", "--pattern", "CAFE", "--tier", "Efficiency", "--max-temp", "80",
            "--keep-running", "--status-interval", "10", "--json"
        ]);
        Assert.Equal(PGPMint.Core.Search.PowerTier.Efficiency, CommandLine.ParseTier(search.Get("tier")));
        Assert.Equal(80, search.Number("max-temp", 0));
        Assert.True(search.Has("keep-running"));
        Assert.True(search.Has("json"));
        Assert.Equal(10, search.Number("status-interval", 5));

        var curve = CommandLine.Parse([
            "curve", "--pattern", "DEAD", "--settle-seconds", "2", "--measure-seconds", "4",
            "--out-csv", "out.csv", "--out-json", "out.json"
        ]);
        Assert.Equal("curve", curve.Command);
        Assert.Equal(2, curve.Number("settle-seconds", 3));
        Assert.Equal(4, curve.Number("measure-seconds", 5));
        Assert.Equal("out.csv", curve.Get("out-csv"));
        Assert.Equal("out.json", curve.Get("out-json"));
    }

    [Fact]
    public async Task HitHookRunnerExecutesCleanlyWithPublicMetadataEnvVarsAndZeroSecrets()
    {
        DateTimeOffset foundAt = DateTimeOffset.UtcNow;
        var match = new PGPMint.Core.Search.MatchedKeyRecord(
            Id: "id1",
            Fingerprint: "0123456789ABCDEF0123456789ABCDEFDEADBEEF",
            KeyId: "01234567DEADBEEF",
            CreationTimestamp: 1_700_000_000u,
            MatchedPattern: "***DEADBEEF",
            DeviceName: "Test GPU",
            FoundAtUtc: foundAt,
            PublicKeyHex: "00",
            ProtectedPrivateSeedBase64: "SECRET_SEED_DO_NOT_LEAK");

        System.Diagnostics.ProcessStartInfo psi = HitHookRunner.CreateStartInfo("git", match);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(match.KeyId, psi.Environment["PGPMINT_KEY_ID"]);
        Assert.Equal(match.Fingerprint, psi.Environment["PGPMINT_FINGERPRINT"]);
        Assert.Equal(match.MatchedPattern, psi.Environment["PGPMINT_MASK"]);
        Assert.Equal(match.DeviceName, psi.Environment["PGPMINT_DEVICE"]);
        Assert.Equal(foundAt.ToString("O"), psi.Environment["PGPMINT_FOUND_AT_UTC"]);
        Assert.DoesNotContain(psi.Environment.Values, v => v is not null && v.Contains("SECRET_SEED_DO_NOT_LEAK", StringComparison.Ordinal));

        using var err = new StringWriter();
        int rc = await HitHookRunner.InvokeAsync("git", match, timeout: TimeSpan.FromSeconds(10), errorOutput: err);
        Assert.Equal(1, rc); // bare `git` prints usage and exits 1 across Windows/Linux/macOS
        Assert.Contains("Hook exited with code 1", err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sync")]
    [InlineData("sync --export a --import b")]
    [InlineData("sync --export a --windows-dpapi --data-dir ./dir")]
    public void InvalidSyncArgumentsAreRejected(string args) =>
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split(' ')));

    [Fact]
    public void SyncOptionsAndProgressRoundTripWorkEndToEnd()
    {
        var exp = CommandLine.Parse(["sync", "--export", "bundle.pgpmint-sync", "--progress-only", "--data-dir", "./a"]);
        Assert.Equal("sync", exp.Command);
        Assert.True(exp.Has("progress-only"));
        Assert.Equal("./a", exp.Get("data-dir"));

        var imp = CommandLine.Parse(["sync", "--import", "bundle.pgpmint-sync", "--windows-dpapi", "--password-stdin"]);
        Assert.True(imp.Has("windows-dpapi"));
        Assert.True(imp.Has("password-stdin"));

        string root = Path.Combine(Path.GetTempPath(), "PGPMint-CliSync-" + Guid.NewGuid().ToString("N"));
        string dirA = Path.Combine(root, "a");
        string dirB = Path.Combine(root, "b");
        string bundlePath = Path.Combine(root, "state.pgpmint-sync");
        try
        {
            var storeA = new PGPMint.Core.Storage.AppStateStore(dirA);
            storeA.Update(s =>
            {
                s.LastPatternMask = "****CAFE";
                s.TargetInput = "CAFE";
                s.ProgressLedger["session-1"] = 123_456;
                s.TotalCheckedCandidates = 123_456;
            });

            int rcExport = SyncCommand.Run(CommandLine.Parse(["sync", "--export", bundlePath, "--progress-only", "--data-dir", dirA]));
            Assert.Equal(0, rcExport);
            Assert.True(File.Exists(bundlePath));

            int rcImport = SyncCommand.Run(CommandLine.Parse(["sync", "--import", bundlePath, "--data-dir", dirB]));
            Assert.Equal(0, rcImport);

            var stateB = new PGPMint.Core.Storage.AppStateStore(dirB).Load();
            Assert.Equal(123_456UL, stateB.TotalCheckedCandidates);
            Assert.Equal("****CAFE", stateB.LastPatternMask);

            int rcReimport = SyncCommand.Run(CommandLine.Parse(["sync", "--import", bundlePath, "--data-dir", dirB]));
            Assert.Equal(0, rcReimport);
            Assert.Equal(123_456UL, new PGPMint.Core.Storage.AppStateStore(dirB).Load().TotalCheckedCandidates);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Phase3SearchTimestampRangeAndExportOptionsParseAndValidate()
    {
        var search = CommandLine.Parse([
            "search", "--pattern", "DEAD*BEEF", "--since", "2020-01-01", "--until", "2020-02-01"
        ]);
        Assert.Equal("2020-01-01", search.Get("since"));
        Assert.Equal("2020-02-01", search.Get("until"));

        var export = CommandLine.Parse([
            "export", "--key", "0123456789ABCDEF", "--name", "Alice", "--output", "./out",
            "--no-encrypt-subkey", "--expires", "365d"
        ]);
        Assert.True(export.Has("no-encrypt-subkey"));
        Assert.Equal("365d", export.Get("expires"));

        // Invalid range (< 7 days) & invalid expiration must be rejected
        Assert.Throws<ArgumentException>(() =>
            CommandLine.Parse(["search", "--pattern", "CAFE", "--since", "2020-01-01", "--until", "2020-01-03"]));
        Assert.Throws<ArgumentException>(() =>
            CommandLine.Parse(["export", "--key", "0123456789ABCDEF", "--name", "Alice", "--output", "./out", "--expires", "1999-01-01"]));
    }
}
