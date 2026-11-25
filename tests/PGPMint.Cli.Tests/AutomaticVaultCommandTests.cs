using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Cli.Tests;

public sealed class AutomaticVaultCommandTests
{
    [Fact]
    public async Task InvalidVaultDataReturnsAnErrorInsteadOfEscapingTheCliEntryPoint()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-cli-invalid-" + Guid.NewGuid().ToString("N"));
        TextReader original = Console.In;
        try
        {
            using (var lease = new VaultSession(directory)) File.WriteAllText(Path.Combine(directory, "vault.key.json"), "null");
            Console.SetIn(new StringReader("unused password\n"));
            int code = await Program.Main(["export", "--key", "0123456789ABCDEF", "--name", "Test", "--output", directory,
                "--data-dir", directory, "--password-stdin"]);
            Assert.Equal(1, code);
            Assert.Equal("null", File.ReadAllText(Path.Combine(directory, "vault.key.json")));
            using var released = new VaultSession(directory);
        }
        finally { Console.SetIn(original); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void AutomaticVaultDoesNotConsumeTheExportPassphraseFromStandardInput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-cli-auto-" + Guid.NewGuid().ToString("N"));
        TextReader original = Console.In;
        var store = new TestStore();
        try
        {
            using var session = new VaultSession(directory);
            using (var created = AutomaticVault.TryOpen(session, store)) { Assert.NotNull(created); }
            Console.SetIn(new StringReader("only export password\n"));
            var options = CommandLine.Parse(["export", "--key", "0123456789ABCDEF", "--name", "User", "--output", directory, "--password-stdin"]);
            using var reopened = Program.OpenVaultProtector(session, options, store);
            Assert.Equal("only export password", Console.In.ReadLine());
            byte[] originalHeader = File.ReadAllBytes(Path.Combine(directory, "vault.key.json"));
            store.Delete("");
            Console.SetIn(new StringReader("do not consume\n"));
            Assert.Throws<InvalidDataException>(() => Program.OpenVaultProtector(session, options, store));
            Assert.Equal("do not consume", Console.In.ReadLine());
            Assert.Equal(originalHeader, File.ReadAllBytes(Path.Combine(directory, "vault.key.json")));
        }
        finally { Console.SetIn(original); Array.Clear(store.Secret); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class TestStore : IVaultSecretStore
    {
        internal char[] Secret = [];
        public string Kind => "test-cli-keychain";
        public char[]? Read(string account) => (char[])Secret.Clone();
        public void Create(string account, ReadOnlySpan<char> secret) => Secret = secret.ToArray();
        public void Delete(string account) { Array.Clear(Secret); Secret = []; }
    }
}
