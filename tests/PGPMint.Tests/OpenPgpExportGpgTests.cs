using System.Diagnostics;
using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public class OpenPgpExportGpgTests
{
    private const string Rfc8032SeedHex = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const uint Rfc8032Timestamp = 1700000000u;
    private const string ExpectedFingerprint = "7629413EDCE0D2380410F05599933B470FD7C498";

    internal static void VerifyPersistedKeysSign(DpapiVaultStore vault, string directory)
    {
        string? gpg = FindGpgExecutable();
        Assert.NotNull(gpg);
        // Git for Windows' GnuPG agent socket needs a short home path.
        string home = Path.Combine(Path.GetTempPath(), "pmg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(home);
        var env = new Dictionary<string, string> { ["GNUPGHOME"] = FormatGpgHomeEnv(gpg, home) };
        string payload = Path.Combine(directory, "payload.txt");
        string keyFile = Path.Combine(directory, "secret.asc");
        string signature = Path.Combine(directory, "payload.sig");
        try
        {
            File.WriteAllText(payload, "PGPMint continuous mint signing verification\n");
            foreach (MatchedKeyRecord record in vault.LoadMatches())
            {
                byte[] seed = vault.DecryptPrivateSeed(record);
                OpenPgpExportResult exported;
                try
                {
                    byte[] publicKey = Convert.FromHexString(record.PublicKeyHex);
                    Assert.Equal(publicKey, Ed25519KeyGenerator.DerivePublicKey(seed));
                    exported = OpenPgpArmorExporter.ExportKeyPair(new OpenPgpExportRequest(
                        seed, publicKey, record.CreationTimestamp, "PGPMint Tester", "tester@pgpmint.example", null));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(seed);
                }

                Assert.Equal(record.Fingerprint, exported.Fingerprint);
                File.WriteAllText(keyFile, exported.PrivateKeyArmor);
                var import = RunProcess(gpg, ["--batch", "--import", keyFile], directory, env);
                Assert.True(import.ExitCode == 0, import.StdErr);

                var sign = RunProcess(gpg, ["--batch", "--yes", "--detach-sign", "--local-user", record.Fingerprint,
                    "--output", signature, payload], directory, env);
                Assert.True(sign.ExitCode == 0, sign.StdErr);

                var verify = RunProcess(gpg, ["--batch", "--verify", signature, payload], directory, env);
                Assert.True(verify.ExitCode == 0, verify.StdErr);
                Assert.Contains(record.Fingerprint, verify.StdOut + verify.StdErr);
            }
        }
        finally
        {
            TryKillGpgAgent(gpg, home, home);
            try { Directory.Delete(home, recursive: true); } catch { }
        }
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string cleaned = dir.Trim().Trim('"');
                if (cleaned.Length == 0)
                {
                    continue;
                }

                string full = Path.Combine(cleaned, fileName);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
            catch
            {
                // Ignore malformed PATH segments
            }
        }

        return null;
    }

    private static string? FindGitExecutable()
    {
        if (!OperatingSystem.IsWindows()) return FindExecutableOnPath("git");
        string[] candidates =
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files (Arm)\Git\cmd\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe"
        };
        return candidates.FirstOrDefault(File.Exists) ?? FindExecutableOnPath("git.exe");
    }

    private static string? FindGpgExecutable()
    {
        if (!OperatingSystem.IsWindows()) return FindExecutableOnPath("gpg");
        var candidates = new List<string>
        {
            @"C:\Program Files\Git\usr\bin\gpg.exe",
            @"C:\Program Files (Arm)\Git\usr\bin\gpg.exe",
            @"C:\Program Files (x86)\Git\usr\bin\gpg.exe",
            @"C:\Program Files (x86)\GnuPG\bin\gpg.exe",
            @"C:\Program Files\GnuPG\bin\gpg.exe"
        };

        string? gitPath = FindGitExecutable();
        if (!string.IsNullOrWhiteSpace(gitPath))
        {
            string? gitRoot = Directory.GetParent(Path.GetDirectoryName(gitPath) ?? string.Empty)?.FullName;
            if (!string.IsNullOrWhiteSpace(gitRoot))
            {
                candidates.Insert(0, Path.Combine(gitRoot, "usr", "bin", "gpg.exe"));
            }
        }

        return candidates.FirstOrDefault(File.Exists) ?? FindExecutableOnPath("gpg.exe");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PGPMint-Test-Passphrase!2026")]
    public void ExportedArmoredKeyImportsIntoRealGpgAndSignsGitCommit(string? passphrase)
    {
        string? gpgPath = FindGpgExecutable();
        string? gitPath = FindGitExecutable();
        Assert.NotNull(gpgPath);
        Assert.NotNull(gitPath);

        byte[] seed = Convert.FromHexString(Rfc8032SeedHex);
        byte[] publicKey = Ed25519KeyGenerator.DerivePublicKey(seed);

        OpenPgpExportResult exported = OpenPgpArmorExporter.ExportKeyPair(
            new OpenPgpExportRequest(
                PrivateSeed: seed,
                PublicKey: publicKey,
                CreationTimestamp: Rfc8032Timestamp,
                UserName: "PGPMint Tester",
                UserEmail: "tester@pgpmint.example",
                Passphrase: passphrase));

        Assert.Contains("-----BEGIN PGP PRIVATE KEY BLOCK-----", exported.PrivateKeyArmor);
        Assert.Contains("-----END PGP PRIVATE KEY BLOCK-----", exported.PrivateKeyArmor);
        Assert.Contains("-----BEGIN PGP PUBLIC KEY BLOCK-----", exported.PublicKeyArmor);
        Assert.Equal(ExpectedFingerprint, exported.Fingerprint);
        Assert.Equal(ExpectedFingerprint[^16..], exported.KeyId);

        string tempRoot = Path.Combine(Path.GetTempPath(), "pm-" + Guid.NewGuid().ToString("N")[..6]);
        string gpgHome = Path.Combine(tempRoot, "g");
        string repoDir = Path.Combine(tempRoot, "r");
        Directory.CreateDirectory(gpgHome);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(gpgHome, "gpg-agent.conf"), "allow-loopback-pinentry\n");

        try
        {
            string keyFile = Path.Combine(tempRoot, "secret.asc");
            File.WriteAllText(keyFile, exported.PrivateKeyArmor);

            string gpgHomeEnv = FormatGpgHomeEnv(gpgPath!, gpgHome);
            var env = new Dictionary<string, string> { ["GNUPGHOME"] = gpgHomeEnv };

            // 1. Import secret key into isolated GNUPGHOME
            var importArgs = new List<string> { "--batch", "--yes", "--pinentry-mode", "loopback" };
            if (!string.IsNullOrEmpty(passphrase))
            {
                importArgs.Add("--passphrase");
                importArgs.Add(passphrase);
            }
            importArgs.Add("--import");
            importArgs.Add(keyFile);

            var importResult = RunProcess(gpgPath!, importArgs, tempRoot, env);
            Assert.True(importResult.ExitCode == 0, $"gpg --import failed: {importResult.StdErr}\n{importResult.StdOut}");

            // 2. Verify fingerprint & signing capability via --list-secret-keys --with-colons
            var listResult = RunProcess(gpgPath!, new[] { "--batch", "--list-secret-keys", "--with-colons" }, tempRoot, env);
            Assert.Equal(0, listResult.ExitCode);
            Assert.Contains(ExpectedFingerprint, listResult.StdOut);
            // Primary key capability field must include 's' (sign) and 'c' (certify)
            string? secLine = listResult.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("sec:"));
            Assert.NotNull(secLine);
            string[] fields = secLine!.Split(':');
            Assert.True(fields.Length > 11);
            Assert.Contains("s", fields[11], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("c", fields[11], StringComparison.OrdinalIgnoreCase);

            // 3. Real detached sign + verify
            string payloadPath = Path.Combine(tempRoot, "hello.txt");
            string sigPath = Path.Combine(tempRoot, "hello.txt.asc");
            File.WriteAllText(payloadPath, "PGPMint signing verification payload\n");

            var signArgs = new List<string>
            {
                "--batch", "--yes", "--pinentry-mode", "loopback",
                "--armor", "--detach-sign", "--local-user", ExpectedFingerprint,
                "--output", sigPath
            };
            if (!string.IsNullOrEmpty(passphrase))
            {
                signArgs.Add("--passphrase");
                signArgs.Add(passphrase);
            }
            signArgs.Add(payloadPath);

            var signResult = RunProcess(gpgPath!, signArgs, tempRoot, env);
            Assert.True(signResult.ExitCode == 0, $"gpg --detach-sign failed: {signResult.StdErr}");

            var verifyResult = RunProcess(gpgPath!, new[] { "--batch", "--verify", sigPath, payloadPath }, tempRoot, env);
            Assert.True(verifyResult.ExitCode == 0, $"gpg --verify failed: {verifyResult.StdErr}");
            Assert.Contains(ExpectedFingerprint, verifyResult.StdErr + verifyResult.StdOut);

            // 4. Real git commit -S + git verify-commit HEAD (for unencrypted key; passphrase-protected keys are verified via gpg above)
            if (string.IsNullOrEmpty(passphrase))
            {
                Assert.Equal(0, RunProcess(gitPath!, new[] { "init", "-b", "main" }, repoDir, env).ExitCode);
                Assert.Equal(0, RunProcess(gitPath!, new[] { "config", "user.name", "PGPMint Tester" }, repoDir, env).ExitCode);
                Assert.Equal(0, RunProcess(gitPath!, new[] { "config", "user.email", "tester@pgpmint.example" }, repoDir, env).ExitCode);
                Assert.Equal(0, RunProcess(gitPath!, new[] { "config", "user.signingkey", ExpectedFingerprint }, repoDir, env).ExitCode);
                Assert.Equal(0, RunProcess(gitPath!, new[] { "config", "gpg.program", gpgPath! }, repoDir, env).ExitCode);

                File.WriteAllText(Path.Combine(repoDir, "README.md"), "signed commit test\n");
                Assert.Equal(0, RunProcess(gitPath!, new[] { "add", "README.md" }, repoDir, env).ExitCode);

                var commitResult = RunProcess(gitPath!, new[] { "commit", "-S", "-m", "Signed by PGPMint" }, repoDir, env);
                Assert.True(commitResult.ExitCode == 0, $"git commit -S failed: {commitResult.StdErr}\n{commitResult.StdOut}");

                var verifyCommitResult = RunProcess(gitPath!, new[] { "verify-commit", "HEAD" }, repoDir, env);
                Assert.True(verifyCommitResult.ExitCode == 0, $"git verify-commit failed: {verifyCommitResult.StdErr}");
                Assert.Contains(ExpectedFingerprint, verifyCommitResult.StdErr + verifyCommitResult.StdOut);
            }
        }
        finally
        {
            // Kill gpg-agent holding files inside temp GNUPGHOME before cleanup
            TryKillGpgAgent(gpgPath!, tempRoot, gpgHome);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LeadingZeroBytePrivateSeedImportsAndSignsInRealGpg()
    {
        string? gpgPath = FindGpgExecutable();
        Assert.NotNull(gpgPath);

        // Seed starting with two 0x00 bytes exercises RFC 4880 MPI leading-zero stripping
        byte[] seed = Convert.FromHexString("0000112233445566778899aabbccddeeff00112233445566778899aabbccddee");
        byte[] pub = Ed25519KeyGenerator.DerivePublicKey(seed);
        uint ts = 1700000123u;

        OpenPgpExportResult exported = OpenPgpArmorExporter.ExportKeyPair(
            new OpenPgpExportRequest(seed, pub, ts, "ZeroSeed", "<zero@pgpmint.example>", null));

        string tempRoot = Path.Combine(Path.GetTempPath(), "pmz-" + Guid.NewGuid().ToString("N")[..6]);
        string gpgHome = Path.Combine(tempRoot, "g");
        Directory.CreateDirectory(gpgHome);
        File.WriteAllText(Path.Combine(gpgHome, "gpg-agent.conf"), "allow-loopback-pinentry\n");

        try
        {
            string keyFile = Path.Combine(tempRoot, "zero.asc");
            File.WriteAllText(keyFile, exported.PrivateKeyArmor);
            string gpgHomeEnv = FormatGpgHomeEnv(gpgPath!, gpgHome);
            var env = new Dictionary<string, string> { ["GNUPGHOME"] = gpgHomeEnv };

            var importResult = RunProcess(gpgPath!, new[] { "--batch", "--yes", "--import", keyFile }, tempRoot, env);
            Assert.True(importResult.ExitCode == 0, $"gpg --import failed: {importResult.StdErr}");

            string payload = Path.Combine(tempRoot, "m.txt");
            string sig = Path.Combine(tempRoot, "m.txt.asc");
            File.WriteAllText(payload, "leading zero seed payload\n");

            var signRes = RunProcess(gpgPath!, new[]
            {
                "--batch", "--yes", "--pinentry-mode", "loopback",
                "--armor", "--detach-sign", "--local-user", exported.Fingerprint,
                "--output", sig, payload
            }, tempRoot, env);
            Assert.True(signRes.ExitCode == 0, $"gpg --detach-sign failed: {signRes.StdErr}");

            var verifyRes = RunProcess(gpgPath!, new[] { "--batch", "--verify", sig, payload }, tempRoot, env);
            Assert.True(verifyRes.ExitCode == 0, $"gpg --verify failed: {verifyRes.StdErr}");
            Assert.Contains(exported.Fingerprint, verifyRes.StdErr + verifyRes.StdOut);
        }
        finally
        {
            TryKillGpgAgent(gpgPath!, tempRoot, gpgHome);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PGPMint-Subkey!2026")]
    public void ExportWithCv25519SubkeyEncryptsDecryptsAndSupportsPrimaryOnlyOptOutInRealGpg(string? passphrase)
    {
        string? gpgPath = FindGpgExecutable();
        Assert.NotNull(gpgPath);

        byte[] seed = Convert.FromHexString(Rfc8032SeedHex);
        byte[] pub = Ed25519KeyGenerator.DerivePublicKey(seed);

        OpenPgpExportResult withSubkey = OpenPgpArmorExporter.ExportKeyPair(
            new OpenPgpExportRequest(seed, pub, Rfc8032Timestamp, "PGPMint Encrypt", "enc@pgpmint.example", passphrase, IncludeEncryptionSubkey: true));
        OpenPgpExportResult primaryOnly = OpenPgpArmorExporter.ExportKeyPair(
            new OpenPgpExportRequest(seed, pub, Rfc8032Timestamp, "PGPMint PrimaryOnly", "only@pgpmint.example", passphrase, IncludeEncryptionSubkey: false));

        string tempRoot = Path.Combine(Path.GetTempPath(), "pms-" + Guid.NewGuid().ToString("N")[..6]);
        string gpgHome = Path.Combine(tempRoot, "g");
        string gpgHomeOnly = Path.Combine(tempRoot, "go");
        Directory.CreateDirectory(gpgHome);
        Directory.CreateDirectory(gpgHomeOnly);
        File.WriteAllText(Path.Combine(gpgHome, "gpg-agent.conf"), "allow-loopback-pinentry\n");

        try
        {
            var env = new Dictionary<string, string> { ["GNUPGHOME"] = FormatGpgHomeEnv(gpgPath!, gpgHome) };
            string keyFile = Path.Combine(tempRoot, "secret-sub.asc");
            File.WriteAllText(keyFile, withSubkey.PrivateKeyArmor);

            var importArgs = new List<string> { "--batch", "--yes", "--pinentry-mode", "loopback" };
            if (!string.IsNullOrEmpty(passphrase))
            {
                importArgs.Add("--passphrase");
                importArgs.Add(passphrase);
            }
            importArgs.Add("--import");
            importArgs.Add(keyFile);

            var imp = RunProcess(gpgPath!, importArgs, tempRoot, env);
            Assert.True(imp.ExitCode == 0, $"gpg --import failed: {imp.StdErr}");

            var list = RunProcess(gpgPath!, new[] { "--batch", "--list-secret-keys", "--with-colons" }, tempRoot, env);
            Assert.Equal(0, list.ExitCode);
            string? ssbLine = list.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("ssb:"));
            Assert.NotNull(ssbLine);
            string[] ssbFields = ssbLine!.Split(':');
            Assert.Equal("18", ssbFields[3]); // ECDH / Cv25519
            Assert.Contains("e", ssbFields[11], StringComparison.OrdinalIgnoreCase);

            string plainPath = Path.Combine(tempRoot, "secret-msg.txt");
            string encPath = Path.Combine(tempRoot, "secret-msg.asc");
            string decPath = Path.Combine(tempRoot, "secret-msg.dec");
            const string expectedMsg = "PGPMint Cv25519 round-trip message!\n";
            File.WriteAllText(plainPath, expectedMsg);

            var enc = RunProcess(gpgPath!, new[]
            {
                "--batch", "--yes", "--trust-model", "always",
                "--armor", "--encrypt", "--recipient", withSubkey.Fingerprint,
                "--output", encPath, plainPath
            }, tempRoot, env);
            Assert.True(enc.ExitCode == 0, $"gpg --encrypt failed: {enc.StdErr}");

            var decArgs = new List<string> { "--batch", "--yes", "--pinentry-mode", "loopback" };
            if (!string.IsNullOrEmpty(passphrase))
            {
                decArgs.Add("--passphrase");
                decArgs.Add(passphrase);
            }
            decArgs.Add("--decrypt");
            decArgs.Add("--output");
            decArgs.Add(decPath);
            decArgs.Add(encPath);

            var dec = RunProcess(gpgPath!, decArgs, tempRoot, env);
            Assert.True(dec.ExitCode == 0, $"gpg --decrypt failed: {dec.StdErr}");
            Assert.Equal(expectedMsg, File.ReadAllText(decPath));

            // Opt-out must contain no ssb
            var envOnly = new Dictionary<string, string> { ["GNUPGHOME"] = FormatGpgHomeEnv(gpgPath!, gpgHomeOnly) };
            File.WriteAllText(Path.Combine(gpgHomeOnly, "gpg-agent.conf"), "allow-loopback-pinentry\n");
            string keyOnlyFile = Path.Combine(tempRoot, "secret-only.asc");
            File.WriteAllText(keyOnlyFile, primaryOnly.PrivateKeyArmor);
            var impOnlyArgs = new List<string> { "--batch", "--yes", "--pinentry-mode", "loopback" };
            if (!string.IsNullOrEmpty(passphrase))
            {
                impOnlyArgs.Add("--passphrase");
                impOnlyArgs.Add(passphrase);
            }
            impOnlyArgs.Add("--import");
            impOnlyArgs.Add(keyOnlyFile);
            Assert.Equal(0, RunProcess(gpgPath!, impOnlyArgs, tempRoot, envOnly).ExitCode);
            var listOnly = RunProcess(gpgPath!, new[] { "--batch", "--list-secret-keys", "--with-colons" }, tempRoot, envOnly);
            Assert.DoesNotContain("ssb:", listOnly.StdOut);
        }
        finally
        {
            TryKillGpgAgent(gpgPath!, tempRoot, gpgHome);
            TryKillGpgAgent(gpgPath!, tempRoot, gpgHomeOnly);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExportWithExpirationDateSetsPrimaryAndSubkeyExpirationInRealGpg()
    {
        string? gpgPath = FindGpgExecutable();
        Assert.NotNull(gpgPath);

        byte[] seed = Convert.FromHexString(Rfc8032SeedHex);
        byte[] pub = Ed25519KeyGenerator.DerivePublicKey(seed);
        long expUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 365L * 86_400L;
        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(expUnix);

        OpenPgpExportResult exported = OpenPgpArmorExporter.ExportKeyPair(
            new OpenPgpExportRequest(seed, pub, Rfc8032Timestamp, "PGPMint Expiring", "exp@pgpmint.example", null, true, expiresAt));

        string tempRoot = Path.Combine(Path.GetTempPath(), "pmx-" + Guid.NewGuid().ToString("N")[..6]);
        string gpgHome = Path.Combine(tempRoot, "g");
        Directory.CreateDirectory(gpgHome);
        try
        {
            var env = new Dictionary<string, string> { ["GNUPGHOME"] = FormatGpgHomeEnv(gpgPath!, gpgHome) };
            string keyFile = Path.Combine(tempRoot, "exp.asc");
            File.WriteAllText(keyFile, exported.PublicKeyArmor);
            var imp = RunProcess(gpgPath!, new[] { "--batch", "--yes", "--import", keyFile }, tempRoot, env);
            Assert.True(imp.ExitCode == 0, $"gpg --import failed: {imp.StdErr}");

            var list = RunProcess(gpgPath!, new[] { "--batch", "--list-keys", "--with-colons" }, tempRoot, env);
            Assert.Equal(0, list.ExitCode);
            string[] lines = list.StdOut.Split('\n');
            string? pubLine = lines.FirstOrDefault(l => l.StartsWith("pub:"));
            string? subLine = lines.FirstOrDefault(l => l.StartsWith("sub:"));
            Assert.NotNull(pubLine);
            Assert.NotNull(subLine);
            Assert.Equal(expUnix.ToString(), pubLine!.Split(':')[6]);
            Assert.Equal(expUnix.ToString(), subLine!.Split(':')[6]);
        }
        finally
        {
            TryKillGpgAgent(gpgPath!, tempRoot, gpgHome);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string ToMsysPath(string windowsPath)
    {
        string full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            return "/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        }
        return full.Replace('\\', '/');
    }

    private static string FormatGpgHomeEnv(string gpgPath, string gpgHome)
    {
        string normalized = Path.GetFullPath(gpgPath);
        return normalized.Contains(@"\usr\bin\", StringComparison.OrdinalIgnoreCase)
            ? ToMsysPath(gpgHome)
            : gpgHome;
    }

    private static void TryKillGpgAgent(string gpgPath, string workDir, string gpgHome)
    {
        string gpgconf = Path.Combine(Path.GetDirectoryName(gpgPath)!, OperatingSystem.IsWindows() ? "gpgconf.exe" : "gpgconf");
        string gpgHomeEnv = FormatGpgHomeEnv(gpgPath, gpgHome);
        if (File.Exists(gpgconf))
        {
            try
            {
                RunProcess(gpgconf, new[] { "--kill", "gpg-agent" }, workDir,
                    new Dictionary<string, string> { ["GNUPGHOME"] = gpgHomeEnv });
            }
            catch { }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName, IEnumerable<string> args, string workingDir, IDictionary<string, string> extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        foreach (var kv in extraEnv)
        {
            psi.Environment[kv.Key] = kv.Value;
        }
        using var proc = Process.Start(psi)!;
        string stdOut = proc.StandardOutput.ReadToEnd();
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, stdOut, stdErr);
    }
}
