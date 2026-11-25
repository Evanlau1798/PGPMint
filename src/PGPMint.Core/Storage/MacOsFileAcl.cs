using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PGPMint.Core.Storage;

internal static class MacOsFileAcl
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    internal static void RequireNoAcl(string path)
    {
        int descriptor = open(path, 0x100 | 0x1000000); // Darwin O_RDONLY | O_NOFOLLOW | O_CLOEXEC.
        if (descriptor < 0) throw NativeError("Cannot inspect private file access", Marshal.GetLastPInvokeError());
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        RequireNoAcl(handle);
    }

    internal static void RequireNoAcl(SafeFileHandle handle)
    {
        nint acl = acl_get_fd(handle);
        int error = Marshal.GetLastPInvokeError();
        // Darwin returns ENOENT for an absent ACL on an already-open descriptor.
        if (acl == 0 && error == 2) return;
        if (acl == 0) throw NativeError("Cannot inspect private file ACL", error);
        try
        {
            int result = acl_get_entry(acl, 0, out _); // ACL_FIRST_ENTRY.
            RequireEmptyAclResult(result, Marshal.GetLastPInvokeError());
        }
        finally { acl_free(acl); }
    }

    internal static void RequireEmptyAclResult(int result, int error)
    {
        if (result == -1 && error == 22) return; // EINVAL: no first entry in a valid ACL.
        if (result == 0) throw new IOException("Private vault and export paths must not have macOS ACL entries. Use a dedicated location without inherited ACLs.");
        throw NativeError("Cannot inspect private file ACL entries", error);
    }

    private static IOException NativeError(string message, int error) => new(message, new Win32Exception(error));

    [DllImport(LibSystem, SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(LibSystem, SetLastError = true)]
    private static extern nint acl_get_fd(SafeFileHandle descriptor);
    [DllImport(LibSystem, SetLastError = true)]
    private static extern int acl_get_entry(nint acl, int entryId, out nint entry);
    [DllImport(LibSystem)]
    private static extern int acl_free(nint acl);
}
