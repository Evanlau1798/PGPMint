using PGPMint.Core.Crypto;
using Xunit;

namespace PGPMint.Cli.Tests;

public sealed class ExportFailureTests
{
    [Fact]
    public void PublicOutputFailureRemovesTheNewPrivateFileWithoutTouchingExistingData()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-export-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string publicPath = Path.Combine(directory, "0123456789ABCDEF.public.asc");
        Directory.CreateDirectory(publicPath);
        try
        {
            Exception? error = Record.Exception(() => ExportCommand.WritePair(
                new OpenPgpExportResult("fingerprint", "0123456789ABCDEF", "private", "public"), directory));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(File.Exists(Path.Combine(directory, "0123456789ABCDEF.secret.asc")));
            Assert.True(Directory.Exists(publicPath));
        }
        finally { Directory.Delete(directory, true); }
    }
}
