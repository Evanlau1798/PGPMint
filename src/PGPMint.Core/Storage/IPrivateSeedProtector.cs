namespace PGPMint.Core.Storage;

public interface IPrivateSeedProtector
{
    byte[] Protect(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> context);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context);
}
