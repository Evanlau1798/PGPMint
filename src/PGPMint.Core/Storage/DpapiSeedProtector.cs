using System.Security.Cryptography;
using System.Text;

namespace PGPMint.Core.Storage;

public sealed class DpapiSeedProtector : IPrivateSeedProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PGPMint.Ed25519.Vault.v1");

    public byte[] Protect(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> context)
    {
#if WINDOWS
        byte[] copy = seed.ToArray();
        try { return ProtectedData.Protect(copy, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(copy); }
#else
        throw new PlatformNotSupportedException("DPAPI vaults can only be opened by their Windows user. Use a password vault on this platform.");
#endif
    }

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context)
    {
#if WINDOWS
        return ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
#else
        throw new PlatformNotSupportedException("DPAPI vaults can only be opened by their Windows user. Use a password vault on this platform.");
#endif
    }
}
