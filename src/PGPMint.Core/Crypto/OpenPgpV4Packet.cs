using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PGPMint.Core.Crypto;

public static class OpenPgpV4Packet
{
    public const int PublicKeyPacketHashInputLength = 54;
    public const int PublicKeyBodyLength = 51;

    // 0x99 (old-format hash prefix) || uint16_be(51) || v4 (0x04) || timestamp(0) ||
    // algo 22 (0x16) || OID len (0x09) || Ed25519 OID (9 bytes) || MPI bits 263 (0x0107) || prefix 0x40
    public static readonly byte[] PublicKeyHeaderTemplate = Convert.FromHexString(
        "990033040000000016092b06010401da470f01010740");

    public static byte[] BuildPublicKeyHashInput(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        if (publicKey.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("Ed25519 public key must be 32 bytes.", nameof(publicKey));
        }

        byte[] buffer = new byte[PublicKeyPacketHashInputLength];
        PublicKeyHeaderTemplate.CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), creationTimestamp);
        publicKey.CopyTo(buffer.AsSpan(22, Ed25519KeyGenerator.KeyByteLength));
        return buffer;
    }

    public static byte[] BuildPublicKeyPacketBody(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        byte[] hashInput = BuildPublicKeyHashInput(publicKey, creationTimestamp);
        return hashInput[3..];
    }

    public static byte[] ComputeFingerprintBytes(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        Span<byte> buffer = stackalloc byte[PublicKeyPacketHashInputLength];
        PublicKeyHeaderTemplate.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), creationTimestamp);
        publicKey.CopyTo(buffer.Slice(22, Ed25519KeyGenerator.KeyByteLength));
        return SHA1.HashData(buffer);
    }

    public static string ComputeFingerprint(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        return Convert.ToHexString(ComputeFingerprintBytes(publicKey, creationTimestamp));
    }

    public static string ComputeKeyId(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        return ComputeKeyIdUInt64(publicKey, creationTimestamp).ToString("X16");
    }

    public static ulong ComputeKeyIdUInt64(ReadOnlySpan<byte> publicKey, uint creationTimestamp)
    {
        Span<byte> buffer = stackalloc byte[PublicKeyPacketHashInputLength];
        PublicKeyHeaderTemplate.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4, 4), creationTimestamp);
        publicKey.CopyTo(buffer.Slice(22, Ed25519KeyGenerator.KeyByteLength));

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(buffer, hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash.Slice(12, 8));
    }
}
