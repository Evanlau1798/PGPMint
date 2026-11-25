using System.Diagnostics;

namespace PGPMint.Core.Storage;

internal static class VaultFileLock
{
    internal static FileStream Acquire(string directory)
    {
        PrivateFileSystem.SecureDirectory(directory);
        string path = Path.Combine(directory, ".vault.lock");
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(path, options);
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(25);
            }
        }
    }
}
