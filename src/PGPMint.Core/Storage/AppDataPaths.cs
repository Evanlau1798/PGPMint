namespace PGPMint.Core.Storage;

public static class AppDataPaths
{
    public static string WindowsDpapiDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PGPMint");

    public static string PasswordVaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        OperatingSystem.IsWindows() ? "PGPMint-Portable" : "PGPMint");

    public static string PreparePasswordVaultDirectory()
    {
        string directory = PasswordVaultDirectory;
        if (!Path.IsPathFullyQualified(directory)) throw new IOException("The user application-data directory is unavailable.");
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The default PGPMint data directory cannot be a symbolic link.");
        PrivateFileSystem.CreateDirectory(directory);
        // This is the fixed application-owned default, never an arbitrary --data-dir or shared parent.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        PrivateFileSystem.SecureDirectory(directory);
        return directory;
    }
}
