namespace PGPMint.Core.Storage;

public static class AppDataPaths
{
    public static string PasswordVaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        OperatingSystem.IsWindows() ? "PGPMint-Portable" : "PGPMint");
}
