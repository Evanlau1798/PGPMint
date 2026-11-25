using System.Reflection;
using Avalonia.Controls;

namespace PGPMint.Desktop;

internal static class AppIcon
{
    private static readonly Lazy<byte[]?> IconBytes = new(LoadEmbeddedBytes);

    public static WindowIcon? Load()
    {
        if (IconBytes.Value is not { Length: > 0 } bytes) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadEmbeddedBytes()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("pgpmint-icon.png", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
