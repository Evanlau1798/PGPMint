global using DpapiVaultStore = PGPMint.Tests.TestVaultStore;

using System.Security.Cryptography;
using PGPMint.Core.Storage;

namespace PGPMint.Tests;

// Coordinator tests exercise persistence with DPAPI on Windows and a process-local test key elsewhere.
public sealed class TestVaultStore(string? directory = null, IPrivateSeedProtector? protector = null)
    : VaultStore(directory, protector ?? CreateProtector())
{
    internal static IPrivateSeedProtector CreateProtector()
    {
#if WINDOWS
        return new DpapiSeedProtector();
#else
        return new TestProtector();
#endif
    }
    private sealed class TestProtector : IPrivateSeedProtector
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> context)
        {
            byte[] data = new byte[28 + seed.Length];
            RandomNumberGenerator.Fill(data.AsSpan(0, 12));
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(data.AsSpan(0, 12), seed, data.AsSpan(28), data.AsSpan(12, 16), context);
            return data;
        }
        public byte[] Unprotect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> context)
        {
            byte[] seed = new byte[data.Length - 28];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(data[..12], data[28..], data.Slice(12, 16), seed, context);
            return seed;
        }
    }
}
