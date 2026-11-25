namespace PGPMint.Core.Storage;

public sealed class VaultSession : IDisposable
{
    private readonly FileStream _lease;
    private readonly string _directory;
    public string DirectoryPath
    {
        get { ObjectDisposedException.ThrowIf(_lease.SafeFileHandle.IsClosed, this); return _directory; }
    }

    public VaultSession(string directory)
    {
        _directory = Path.GetFullPath(directory);
        PrivateFileSystem.SecureDirectory(directory);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { _lease = new FileStream(Path.Combine(directory, ".session.lock"), options); }
        catch (IOException ex) { throw new IOException("Another PGPMint session is using this data directory.", ex); }
    }

    public void Dispose() => _lease.Dispose();
}
