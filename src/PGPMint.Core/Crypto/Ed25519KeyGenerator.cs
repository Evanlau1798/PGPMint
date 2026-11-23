using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace PGPMint.Core.Crypto;

public readonly record struct Ed25519KeyPair(byte[] PrivateSeed, byte[] PublicKey);

public static class Ed25519KeyGenerator
{
    public const int KeyByteLength = 32;

    public static Ed25519KeyPair Generate()
    {
        byte[] seed = RandomNumberGenerator.GetBytes(KeyByteLength);
        byte[] publicKey = DerivePublicKey(seed);
        return new Ed25519KeyPair(seed, publicKey);
    }

    public static byte[] DerivePublicKey(ReadOnlySpan<byte> privateSeed)
    {
        if (privateSeed.Length != KeyByteLength)
        {
            throw new ArgumentException($"Ed25519 private seed must be {KeyByteLength} bytes.", nameof(privateSeed));
        }

        var priv = new Ed25519PrivateKeyParameters(privateSeed);
        return priv.GeneratePublicKey().GetEncoded();
    }

    public static byte[] Sign(ReadOnlySpan<byte> privateSeed, ReadOnlySpan<byte> message)
    {
        if (privateSeed.Length != KeyByteLength)
        {
            throw new ArgumentException($"Ed25519 private seed must be {KeyByteLength} bytes.", nameof(privateSeed));
        }

        var priv = new Ed25519PrivateKeyParameters(privateSeed);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, priv);
        signer.BlockUpdate(message);
        return signer.GenerateSignature();
    }
}
