using System.Text;

namespace PGPMint.Core.Storage;

public static class AtomicFileStore
{
    private const int MaxRetries = 20;

    public static void WriteTextAtomic(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            PrivateFileSystem.CreateDirectory(directory);
        }

        string tempPath = Path.Combine(
            directory ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var fs = PrivateFileSystem.CreateNew(tempPath))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (IsTransientSharingError(ex) && attempt < MaxRetries - 1)
                {
                    Thread.Sleep(Math.Min(100, 15 + attempt * 10));
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of temp file
            }
        }
    }

    public static string ReadTextWithRetry(string path, string defaultContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return defaultContent;
                }

                return File.ReadAllText(path, Encoding.UTF8);
            }
            catch (FileNotFoundException)
            {
                return defaultContent;
            }
            catch (DirectoryNotFoundException)
            {
                return defaultContent;
            }
            catch (Exception ex) when (IsTransientSharingError(ex) && attempt < MaxRetries - 1)
            {
                Thread.Sleep(50);
            }
        }

        return defaultContent;
    }

    private static bool IsTransientSharingError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;
}
