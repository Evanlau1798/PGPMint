using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace PGPMint.Core.Crypto;

public sealed record OpenPgpExportRequest(
    byte[] PrivateSeed,
    byte[] PublicKey,
    uint CreationTimestamp,
    string UserName,
    string? UserEmail,
    string? Passphrase);

public sealed record OpenPgpExportResult(
    string Fingerprint,
    string KeyId,
    string PrivateKeyArmor,
    string PublicKeyArmor);

public static class OpenPgpArmorExporter
{
    private const byte TagSignature = 2;
    private const byte TagSecretKey = 5;
    private const byte TagPublicKey = 6;
    private const byte TagUserId = 13;

    private const byte AlgoEdDsaLegacy = 22;
    private const byte HashSha256 = 8;
    private const byte CipherAes256 = 9;
    private const byte S2KIteratedSalted = 3;
    private const byte S2KCountEncoded = 0xE0; // 16,777,216 bytes

    public static OpenPgpExportResult ExportKeyPair(OpenPgpExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PrivateSeed is null || request.PrivateSeed.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("PrivateSeed must be 32 bytes.", nameof(request));
        }

        if (request.PublicKey is null || request.PublicKey.Length != Ed25519KeyGenerator.KeyByteLength)
        {
            throw new ArgumentException("PublicKey must be 32 bytes.", nameof(request));
        }

        byte[] derivedPub = Ed25519KeyGenerator.DerivePublicKey(request.PrivateSeed);
        if (!derivedPub.AsSpan().SequenceEqual(request.PublicKey))
        {
            throw new ArgumentException("PublicKey does not match PrivateSeed.", nameof(request));
        }

        string userName = SanitizeUidComponent(request.UserName);
        string userEmail = SanitizeUidComponent(request.UserEmail).Trim('<', '>').Trim();
        if (string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(userEmail))
        {
            throw new ArgumentException("UserName or UserEmail is required.", nameof(request));
        }

        string uidText = string.IsNullOrEmpty(userEmail)
            ? userName
            : string.IsNullOrEmpty(userName)
                ? $"<{userEmail}>"
                : $"{userName} <{userEmail}>";

        byte[] fingerprintBytes = OpenPgpV4Packet.ComputeFingerprintBytes(request.PublicKey, request.CreationTimestamp);
        string fingerprint = Convert.ToHexString(fingerprintBytes);
        string keyId = fingerprint[^16..];

        byte[] pubKeyBody = OpenPgpV4Packet.BuildPublicKeyPacketBody(request.PublicKey, request.CreationTimestamp);
        byte[] secKeyBody = BuildSecretKeyBody(pubKeyBody, request.PrivateSeed, request.Passphrase);
        byte[] uidBody = Encoding.UTF8.GetBytes(uidText);
        byte[] selfSigBody = BuildSelfSignatureBody(
            request.PrivateSeed,
            pubKeyBody,
            uidBody,
            fingerprintBytes,
            request.CreationTimestamp);

        byte[] secretPacketStream = Combine(
            BuildNewFormatPacket(TagSecretKey, secKeyBody),
            BuildNewFormatPacket(TagUserId, uidBody),
            BuildNewFormatPacket(TagSignature, selfSigBody));

        byte[] publicPacketStream = Combine(
            BuildNewFormatPacket(TagPublicKey, pubKeyBody),
            BuildNewFormatPacket(TagUserId, uidBody),
            BuildNewFormatPacket(TagSignature, selfSigBody));

        string privateArmor = EncodeAsciiArmor("PGP PRIVATE KEY BLOCK", secretPacketStream);
        string publicArmor = EncodeAsciiArmor("PGP PUBLIC KEY BLOCK", publicPacketStream);

        return new OpenPgpExportResult(fingerprint, keyId, privateArmor, publicArmor);
    }

    private static byte[] BuildSecretKeyBody(byte[] pubKeyBody, byte[] privateSeed, string? passphrase)
    {
        byte[] secretMpi = EncodeMpi(privateSeed);

        using var ms = new MemoryStream();
        ms.Write(pubKeyBody);

        if (string.IsNullOrEmpty(passphrase))
        {
            // S2K usage 0x00: unencrypted + 2-octet modular checksum
            ms.WriteByte(0x00);
            ms.Write(secretMpi);
            ushort checksum = 0;
            foreach (byte b in secretMpi)
            {
                checksum = unchecked((ushort)(checksum + b));
            }

            Span<byte> chk = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(chk, checksum);
            ms.Write(chk);
        }
        else
        {
            // S2K usage 0xFE: AES-256-CFB with Iterated & Salted SHA-256 S2K + trailing SHA-1 integrity hash
            byte[] salt = RandomNumberGenerator.GetBytes(8);
            byte[] iv = RandomNumberGenerator.GetBytes(16);
            byte[] key = DeriveIteratedSaltedS2KKey(passphrase, salt, S2KCountEncoded, 32);

            byte[] sha1Check = SHA1.HashData(secretMpi);
            byte[] plaintext = Combine(secretMpi, sha1Check);
            byte[] ciphertext = EncryptAes256Cfb(plaintext, key, iv);

            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);

            ms.WriteByte(0xFE);
            ms.WriteByte(CipherAes256);
            ms.WriteByte(S2KIteratedSalted);
            ms.WriteByte(HashSha256);
            ms.Write(salt);
            ms.WriteByte(S2KCountEncoded);
            ms.Write(iv);
            ms.Write(ciphertext);
        }

        CryptographicOperations.ZeroMemory(secretMpi);
        return ms.ToArray();
    }

    private static byte[] DeriveIteratedSaltedS2KKey(string passphrase, byte[] salt, byte encodedCount, int keyLength)
    {
        if (keyLength != 32)
        {
            throw new ArgumentOutOfRangeException(nameof(keyLength), "Only 32-byte SHA-256 derived keys are supported.");
        }

        byte[] passBytes = Encoding.UTF8.GetBytes(passphrase);
        byte[] combined = Combine(salt, passBytes);
        int targetCount = (16 + (encodedCount & 15)) << ((encodedCount >> 4) + 6);
        int bytesToHash = Math.Max(targetCount, combined.Length);

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int remaining = bytesToHash;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, combined.Length);
                hash.AppendData(combined, 0, chunk);
                remaining -= chunk;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passBytes);
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    private static byte[] EncryptAes256Cfb(byte[] plaintext, byte[] key, byte[] iv)
    {
        var cfb = new CfbBlockCipher(new AesEngine(), 128);
        cfb.Init(forEncryption: true, new ParametersWithIV(new KeyParameter(key), iv));

        byte[] output = new byte[plaintext.Length];
        int offset = 0;
        const int blockSize = 16;

        while (offset + blockSize <= plaintext.Length)
        {
            cfb.ProcessBlock(plaintext, offset, output, offset);
            offset += blockSize;
        }

        int tail = plaintext.Length - offset;
        if (tail > 0)
        {
            byte[] paddedIn = new byte[blockSize];
            byte[] paddedOut = new byte[blockSize];
            Buffer.BlockCopy(plaintext, offset, paddedIn, 0, tail);
            cfb.ProcessBlock(paddedIn, 0, paddedOut, 0);
            Buffer.BlockCopy(paddedOut, 0, output, offset, tail);
            CryptographicOperations.ZeroMemory(paddedIn);
        }

        return output;
    }

    private static byte[] BuildSelfSignatureBody(
        byte[] privateSeed,
        byte[] pubKeyBody,
        byte[] uidBody,
        byte[] fingerprintBytes,
        uint keyCreationTimestamp)
    {
        uint nowUnix = (uint)Math.Max(1L, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        uint sigTime = Math.Max(keyCreationTimestamp, nowUnix);

        using var hashedSubpackets = new MemoryStream();
        // Subpacket 33: Issuer Fingerprint (v4 || 20-byte fp)
        byte[] issuerFp = new byte[21];
        issuerFp[0] = 0x04;
        Buffer.BlockCopy(fingerprintBytes, 0, issuerFp, 1, 20);
        WriteSubpacket(hashedSubpackets, 33, issuerFp);

        // Subpacket 2: Signature Creation Time
        byte[] sigTimeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sigTimeBytes, sigTime);
        WriteSubpacket(hashedSubpackets, 2, sigTimeBytes);

        // Subpacket 27: Key Flags (0x01 Certify | 0x02 Sign)
        WriteSubpacket(hashedSubpackets, 27, new byte[] { 0x03 });

        // Subpacket 11: Preferred Symmetric Algorithms (AES256, AES192, AES128)
        WriteSubpacket(hashedSubpackets, 11, new byte[] { 9, 8, 7 });

        // Subpacket 21: Preferred Hash Algorithms (SHA256, SHA384, SHA512)
        WriteSubpacket(hashedSubpackets, 21, new byte[] { 8, 9, 10 });

        // Subpacket 22: Preferred Compression Algorithms (ZLIB, BZIP2, ZIP)
        WriteSubpacket(hashedSubpackets, 22, new byte[] { 2, 3, 1 });

        // Subpacket 30: Features (0x01 MDC)
        WriteSubpacket(hashedSubpackets, 30, new byte[] { 0x01 });

        // Subpacket 23: Key Server Preferences (0x80 No-modify)
        WriteSubpacket(hashedSubpackets, 23, new byte[] { 0x80 });

        byte[] hashedBytes = hashedSubpackets.ToArray();

        using var sigHeader = new MemoryStream();
        sigHeader.WriteByte(0x04); // v4
        sigHeader.WriteByte(0x13); // Positive certification of a User ID and Public-Key packet
        sigHeader.WriteByte(AlgoEdDsaLegacy);
        sigHeader.WriteByte(HashSha256);
        WriteUInt16BigEndian(sigHeader, (ushort)hashedBytes.Length);
        sigHeader.Write(hashedBytes);
        byte[] sigHeaderBytes = sigHeader.ToArray();

        // RFC 4880 Section 5.2.4 hash input for 0x13 certification signature
        using var hashInput = new MemoryStream();
        hashInput.WriteByte(0x99);
        WriteUInt16BigEndian(hashInput, (ushort)pubKeyBody.Length);
        hashInput.Write(pubKeyBody);

        hashInput.WriteByte(0xB4);
        WriteUInt32BigEndian(hashInput, (uint)uidBody.Length);
        hashInput.Write(uidBody);

        hashInput.Write(sigHeaderBytes);
        hashInput.WriteByte(0x04);
        hashInput.WriteByte(0xFF);
        WriteUInt32BigEndian(hashInput, (uint)sigHeaderBytes.Length);

        byte[] digest = SHA256.HashData(hashInput.ToArray());

        // OpenPGP EdDSA Legacy signs the 32-byte digest directly with PureEd25519
        byte[] rawSig = Ed25519KeyGenerator.Sign(privateSeed, digest);
        byte[] rMpi = EncodeMpi(rawSig.AsSpan(0, 32));
        byte[] sMpi = EncodeMpi(rawSig.AsSpan(32, 32));

        using var unhashedSubpackets = new MemoryStream();
        // Subpacket 16: Issuer Key ID (last 8 bytes of fingerprint)
        WriteSubpacket(unhashedSubpackets, 16, fingerprintBytes.AsSpan(12, 8));
        byte[] unhashedBytes = unhashedSubpackets.ToArray();

        using var sigBody = new MemoryStream();
        sigBody.Write(sigHeaderBytes);
        WriteUInt16BigEndian(sigBody, (ushort)unhashedBytes.Length);
        sigBody.Write(unhashedBytes);
        sigBody.WriteByte(digest[0]);
        sigBody.WriteByte(digest[1]);
        sigBody.Write(rMpi);
        sigBody.Write(sMpi);
        return sigBody.ToArray();
    }

    private static void WriteSubpacket(Stream stream, byte type, ReadOnlySpan<byte> payload)
    {
        int totalLength = 1 + payload.Length;
        WriteNewFormatLength(stream, totalLength);
        stream.WriteByte(type);
        stream.Write(payload);
    }

    private static byte[] BuildNewFormatPacket(byte tag, byte[] body)
    {
        using var ms = new MemoryStream(body.Length + 6);
        ms.WriteByte((byte)(0xC0 | (tag & 0x3F)));
        WriteNewFormatLength(ms, body.Length);
        ms.Write(body);
        return ms.ToArray();
    }

    private static void WriteNewFormatLength(Stream stream, int length)
    {
        if (length < 192)
        {
            stream.WriteByte((byte)length);
        }
        else if (length <= 8383)
        {
            int adjusted = length - 192;
            stream.WriteByte((byte)((adjusted >> 8) + 192));
            stream.WriteByte((byte)(adjusted & 0xFF));
        }
        else
        {
            stream.WriteByte(0xFF);
            WriteUInt32BigEndian(stream, (uint)length);
        }
    }

    private static byte[] EncodeMpi(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && value[start] == 0)
        {
            start++;
        }

        if (start == value.Length)
        {
            return new byte[] { 0x00, 0x00 };
        }

        ReadOnlySpan<byte> trimmed = value[start..];
        int leadingZeroBits = BitOperations.LeadingZeroCount((uint)trimmed[0]) - 24;
        int bitCount = (8 - leadingZeroBits) + (trimmed.Length - 1) * 8;

        byte[] result = new byte[2 + trimmed.Length];
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(0, 2), (ushort)bitCount);
        trimmed.CopyTo(result.AsSpan(2));
        return result;
    }

    private static string EncodeAsciiArmor(string label, byte[] data)
    {
        string base64 = Convert.ToBase64String(data);
        var sb = new StringBuilder(base64.Length + 128);
        sb.Append("-----BEGIN ").Append(label).Append("-----\n\n");

        for (int i = 0; i < base64.Length; i += 64)
        {
            int len = Math.Min(64, base64.Length - i);
            sb.Append(base64, i, len).Append('\n');
        }

        uint crc = ComputeCrc24(data);
        byte[] crcBytes = { (byte)((crc >> 16) & 0xFF), (byte)((crc >> 8) & 0xFF), (byte)(crc & 0xFF) };
        sb.Append('=').Append(Convert.ToBase64String(crcBytes)).Append('\n');
        sb.Append("-----END ").Append(label).Append("-----\n");
        return sb.ToString();
    }

    private static uint ComputeCrc24(ReadOnlySpan<byte> data)
    {
        uint crc = 0xB704CEu;
        foreach (byte b in data)
        {
            crc ^= (uint)b << 16;
            for (int i = 0; i < 8; i++)
            {
                crc <<= 1;
                if ((crc & 0x1000000u) != 0)
                {
                    crc ^= 0x1864CFBu;
                }
            }
        }

        return crc & 0xFFFFFFu;
    }

    private static void WriteUInt16BigEndian(Stream stream, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        stream.Write(buf);
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        stream.Write(buf);
    }

    private static string SanitizeUidComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }
        return sb.ToString().Trim();
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        int total = 0;
        foreach (byte[] arr in arrays)
        {
            total += arr.Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        foreach (byte[] arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }

        return result;
    }
}
