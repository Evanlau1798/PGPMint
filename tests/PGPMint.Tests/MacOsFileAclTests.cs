using System.Diagnostics;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class MacOsFileAclTests
{
    [Theory]
    [InlineData(0, 0)] // Darwin: an ACL entry was returned.
    [InlineData(-1, 1)] // Permission failure is not an empty ACL.
    [InlineData(1, 0)] // Unknown native result must fail closed.
    public void NativeAclEntryOrErrorCannotBeAcceptedAsPrivate(int result, int error) =>
        Assert.Throws<IOException>(() => MacOsFileAcl.RequireEmptyAclResult(result, error));

    [Fact]
    public void NativeEndOfEmptyAclIsAccepted() => MacOsFileAcl.RequireEmptyAclResult(-1, 22);

    [MacOsFact]
    public void ExistingMode700DirectoryWithAclIsRefusedWithoutChangingItsAcl()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateFileSystem.CreateDirectory(directory);
            Chmod("+a", "everyone allow read,search", directory);
            string before = Listing(directory);
            Assert.Throws<IOException>(() => new VaultSession(directory));
            Assert.Equal(before, Listing(directory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { if (Directory.Exists(directory)) { Chmod("-N", directory); Directory.Delete(directory, true); } }
    }

    [MacOsFact]
    public void InheritedAclsAreRejectedBeforeWritingPrivateDataOrChangingTheParent()
    {
        string parent = Path.Combine(Path.GetTempPath(), "PGPMint-inherit-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrivateFileSystem.CreateDirectory(parent);
            Chmod("+a", "everyone allow read,search,file_inherit,directory_inherit", parent);
            string before = Listing(parent);
            string file = Path.Combine(parent, "secret.asc"), child = Path.Combine(parent, "vault");
            Assert.Throws<IOException>(() => PrivateFileSystem.CreateNew(file));
            Assert.False(File.Exists(file));
            Assert.Throws<IOException>(() => PasswordSeedProtector.Open(child, "test password"));
            Assert.False(File.Exists(Path.Combine(child, "vault.key.json")));
            Assert.Equal(before, Listing(parent));
        }
        finally
        {
            if (Directory.Exists(parent)) { Chmod("-RN", parent); Directory.Delete(parent, true); }
        }
    }

    [MacOsFact]
    public void ExistingMode600VaultHeaderWithAclIsRefusedWithoutChangingItsContents()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-header-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (PasswordSeedProtector.Open(directory, "test password")) { }
            string header = Path.Combine(directory, "vault.key.json");
            byte[] before = File.ReadAllBytes(header);
            Chmod("+a", "everyone allow read", header);
            Assert.Throws<IOException>(() => PasswordSeedProtector.Open(directory, "test password"));
            Assert.Equal(before, File.ReadAllBytes(header));
        }
        finally { if (Directory.Exists(directory)) { Chmod("-RN", directory); Directory.Delete(directory, true); } }
    }

    private static void Chmod(params string[] args) => Run("/bin/chmod", args);
    private static string Listing(string path) => string.Join('\n', Run("/bin/ls", "-lde", path).Split('\n').Skip(1));
    private static string Run(string command, params string[] args)
    {
        var info = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), command + " timed out");
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}

internal sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute() { if (!OperatingSystem.IsMacOS()) Skip = "Native ACL checks require macOS."; }
}
