using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Gpu;
using Xunit;

namespace PGPMint.Tests;

public class OpenPgpVectorTests
{
    private const string Rfc8032SeedHex = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
    private const string Rfc8032PublicHex = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
    private const uint Rfc8032Timestamp = 1700000000u;
    private const string ExpectedFingerprint = "7629413EDCE0D2380410F05599933B470FD7C498";
    private const string ExpectedKeyId = "99933B470FD7C498";

    [Fact]
    public void Ed25519SeedDerivesRfc8032TestVector1PublicKey()
    {
        byte[] seed = Convert.FromHexString(Rfc8032SeedHex);
        byte[] publicKey = Ed25519KeyGenerator.DerivePublicKey(seed);

        Assert.Equal(Rfc8032PublicHex, Convert.ToHexString(publicKey).ToLowerInvariant());
    }

    [Fact]
    public void OpenPgpV4PacketMatchesRfc8032AcceptanceVector()
    {
        byte[] publicKey = Convert.FromHexString(Rfc8032PublicHex);
        byte[] packet = OpenPgpV4Packet.BuildPublicKeyHashInput(publicKey, Rfc8032Timestamp);

        Assert.Equal(54, packet.Length);
        Assert.Equal(new byte[] { 0x99, 0x00, 0x33, 0x04 }, packet[..4]);
        Assert.Equal(new byte[] { 0x65, 0x53, 0xF1, 0x00 }, packet[4..8]);

        string fingerprint = OpenPgpV4Packet.ComputeFingerprint(publicKey, Rfc8032Timestamp);
        string keyId = OpenPgpV4Packet.ComputeKeyId(publicKey, Rfc8032Timestamp);

        Assert.Equal(ExpectedFingerprint, fingerprint);
        Assert.Equal(ExpectedKeyId, keyId);
        Assert.EndsWith("33B470FD7C498", keyId);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(1700000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void LinearSha1ScheduleMatchesReferenceSha1AcrossBoundaryTimestamps(uint timestamp)
    {
        byte[] publicKey = Convert.FromHexString(Rfc8032PublicHex);
        uint[] fixedSchedule = Sha1ScheduleHelper.ComputeFixedSchedule(publicKey);

        ulong computedKeyId = Sha1ScheduleHelper.ComputeKeyIdFromFixedSchedule(fixedSchedule, timestamp);
        string expectedKeyId = OpenPgpFingerprintOracle.Compute(publicKey, timestamp)[^16..];

        Assert.Equal(expectedKeyId, computedKeyId.ToString("X16"));
    }

    [Fact]
    public void OpenPgpV4PacketMatchesRfc9580AppendixA1()
    {
        byte[] publicKey = Convert.FromHexString("3F098994BDD916ED4053197934E4A87C80733A1280D62F8010992E43EE3B2406");
        const uint timestamp = 0x53F35F0B;
        const string fingerprint = "C959BDBAFA32A2F89A153B678CFDE12197965A9A";
        byte[] hashInput = OpenPgpV4Packet.BuildPublicKeyHashInput(publicKey, timestamp);

        Assert.Equal(54, hashInput.Length);
        Assert.Equal(fingerprint, Convert.ToHexString(SHA1.HashData(hashInput)));
        Assert.Equal(fingerprint, OpenPgpV4Packet.ComputeFingerprint(publicKey, timestamp));
        Assert.Equal(fingerprint, OpenPgpFingerprintOracle.Compute(publicKey, timestamp));
        Assert.Equal(fingerprint[^16..], OpenPgpV4Packet.ComputeKeyId(publicKey, timestamp));
    }

    [Fact]
    public void GeneratedKeyPairHasValidLengthAndDeterministicPublicKey()
    {
        Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();

        Assert.Equal(32, pair.PrivateSeed.Length);
        Assert.Equal(32, pair.PublicKey.Length);
        Assert.Equal(pair.PublicKey, Ed25519KeyGenerator.DerivePublicKey(pair.PrivateSeed));
    }

    [Fact]
    public void GpuKernelSourcesAdvertiseAndEmitV5Contracts()
    {
        Assert.Equal("opencl-sha1-four-v5", OpenClKernelSource.KernelVersion);
        Assert.Equal("d3d11-sha1-four-v5", DirectComputeShaderSource.KernelVersion);
        Assert.Equal("metal-sha1-four-v2", MetalKernelSource.KernelVersion);

        string openCl = OpenClKernelSource.BuildFourKeyOpenCl();
        Assert.Contains("ScheduleParam", openCl, StringComparison.Ordinal);
        Assert.Contains("targetLowNoK3", openCl, StringComparison.Ordinal);
        Assert.Contains("0xc3d2e1f0u", openCl, StringComparison.Ordinal);

        string hlsl = DirectComputeShaderSource.BuildFourKeyHlsl();
        Assert.Contains("register(b1)", hlsl, StringComparison.Ordinal);
        Assert.Contains("targetLowNoK3", hlsl, StringComparison.Ordinal);
        Assert.Contains("0xc3d2e1f0u", hlsl, StringComparison.Ordinal);

        Assert.Equal("vulkan-sha1-four-v5", VulkanSpirvSource.KernelVersion);
        byte[] spirv = VulkanSpirvSource.BuildFourKeySpirv();
        Assert.True(spirv.Length > 100 && spirv.Length % 4 == 0);
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(spirv, 0));
        Assert.Equal(0x00010300u, BitConverter.ToUInt32(spirv, 4));
    }
}
