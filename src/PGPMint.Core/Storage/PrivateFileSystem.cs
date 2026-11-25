namespace PGPMint.Core.Storage;

public static class PrivateFileSystem
{
    internal static void SecureDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            string fullPath = Path.GetFullPath(path);
            if (fullPath == Path.GetPathRoot(fullPath)) throw new IOException("A filesystem root cannot be used as a private vault directory.");
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Use a real private vault directory, not a symbolic link.");
        }
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!OperatingSystem.IsWindows() && Directory.Exists(path) && File.GetUnixFileMode(path) != privateMode)
            throw new IOException("An existing vault directory must have owner-only permissions (0700). Use a dedicated directory or secure it explicitly.");
        CreateDirectory(path);
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != privateMode)
            throw new IOException("The vault directory does not have owner-only permissions (0700).");
    }

    internal static void SecureFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Private vault files cannot be symbolic links.");
        if (OperatingSystem.IsMacOS()) MacOsFileAcl.RequireNoAcl(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); return; }
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var missing = new Stack<string>();
        string current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("Cannot create a private filesystem root.");
        }
        // The Unix mode overload only protects the final component, not newly created parents.
        while (missing.TryPop(out string? directory))
        {
            Directory.CreateDirectory(directory, privateMode);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || File.GetUnixFileMode(directory) != privateMode)
                throw new IOException("A new private directory was replaced or does not have owner-only permissions.");
            if (OperatingSystem.IsMacOS()) MacOsFileAcl.RequireNoAcl(directory);
        }
        if (OperatingSystem.IsMacOS()) MacOsFileAcl.RequireNoAcl(path);
    }

    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try
        {
            if (OperatingSystem.IsMacOS()) MacOsFileAcl.RequireNoAcl(stream.SafeFileHandle);
            return stream;
        }
        catch { stream.Dispose(); File.Delete(path); throw; }
    }
}
