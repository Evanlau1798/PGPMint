using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PGPMint.Tests;

internal static class OpenPgpFingerprintOracle
{
    // RFC 9580 §§ 5.5.4.2 and A.1.
    private static readonly byte[] HashHeader =
        Convert.FromHexString("990033040000000016092B06010401DA470F01010740");

    internal static string Compute(ReadOnlySpan<byte> publicKey, uint timestamp)
    {
        Span<byte> packet = stackalloc byte[54];
        HashHeader.CopyTo(packet);
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        publicKey.CopyTo(packet[HashHeader.Length..]);
        return Convert.ToHexString(SHA1.HashData(packet));
    }
}
