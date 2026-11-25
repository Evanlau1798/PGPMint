using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PGPMint.App.Services;

internal static class AppIconLoader
{
    private const uint IconVersion = 0x00030000;

    public static IntPtr LoadIcon(int desiredSize = 32)
    {
        IntPtr embedded = TryLoadFromEmbeddedIco(desiredSize);
        if (embedded != IntPtr.Zero)
        {
            return embedded;
        }

        return LoadIconW(IntPtr.Zero, (IntPtr)32512);
    }

    private static IntPtr TryLoadFromEmbeddedIco(int desiredSize)
    {
        try
        {
            Assembly asm = typeof(AppIconLoader).Assembly;
            string? resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("PGPMint.ico", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return IntPtr.Zero;
            }

            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return IntPtr.Zero;
            }

            byte[] icoBytes = new byte[stream.Length];
            stream.ReadExactly(icoBytes);
            if (icoBytes.Length < 6)
            {
                return IntPtr.Zero;
            }

            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(icoBytes.AsSpan(4, 2));
            if (count == 0 || icoBytes.Length < 6 + count * 16)
            {
                return IntPtr.Zero;
            }

            int bestOffset = -1;
            int bestLength = 0;
            int bestDiff = int.MaxValue;
            int bestSize = 0;

            for (int i = 0; i < count; i++)
            {
                int entry = 6 + i * 16;
                int width = icoBytes[entry] == 0 ? 256 : icoBytes[entry];
                int bytesInRes = (int)BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(entry + 8, 4));
                int imageOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(icoBytes.AsSpan(entry + 12, 4));
                if (bytesInRes <= 0 || imageOffset < 0 || imageOffset + bytesInRes > icoBytes.Length)
                {
                    continue;
                }

                int diff = Math.Abs(width - desiredSize);
                if (diff < bestDiff || (diff == bestDiff && width > bestSize))
                {
                    bestDiff = diff;
                    bestSize = width;
                    bestOffset = imageOffset;
                    bestLength = bytesInRes;
                }
            }

            if (bestOffset < 0)
            {
                return IntPtr.Zero;
            }

            unsafe
            {
                fixed (byte* ptr = &icoBytes[bestOffset])
                {
                    return CreateIconFromResourceEx(
                        (IntPtr)ptr,
                        (uint)bestLength,
                        fIcon: true,
                        dwVer: IconVersion,
                        cxDesired: desiredSize,
                        cyDesired: desiredSize,
                        flags: 0);
                }
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(
        IntPtr presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
}
