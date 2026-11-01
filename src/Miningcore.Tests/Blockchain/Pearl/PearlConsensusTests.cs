using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlConsensusTests
{
    private static byte[] Header() => PearlConsensus.SerializeIncompleteHeader(1,
        Convert.FromHexString("6fe28c0ab6f1b372c1a6a246ae63f74f931e8365e15a089c68d6190000000000"),
        Convert.FromHexString("3ba3edfd7a7b12b27ac72c3e67768f617fc81bc3888a51323a9fb8aa4b1e5e4a"),
        0x495fab29, 0x1d00ffff);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(71934, 1)]
    [InlineData(71935, 2)]
    [InlineData(71936, 2)]
    [InlineData(98999, 2)]
    [InlineData(99000, 3)]
    [InlineData(99001, 3)]
    public void CertificateActivation(long height, uint expected) =>
        Assert.Equal(expected, PearlConsensus.MainnetCertificateVersion(height));

    [Fact]
    public void NegativeHeightIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlConsensus.MainnetCertificateVersion(-1));

    [Theory]
    [InlineData(0u)]
    [InlineData(0x1d80ffffu)]
    [InlineData(0x23000001u)]
    [InlineData(0x01000001u)]
    public void InvalidCompactTargetIsRejected(uint bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlConsensus.DecodeTarget(bits));

    [Theory]
    [InlineData(0x01010000u)]
    [InlineData(0x02008000u)]
    [InlineData(0x1d00ffffu)]
    [InlineData(0x207fffffu)]
    [InlineData(0x2100ffffu)]
    public void CompactTargetRoundTrips(uint bits) =>
        Assert.Equal(bits, PearlConsensus.EncodeTarget(PearlConsensus.DecodeTarget(bits)));

    [Fact]
    public void CompactTargetRoundsTowardHarderWork()
    {
        var exact = (BigInteger.One << 220) + 12345;
        var decoded = PearlConsensus.DecodeTarget(PearlConsensus.EncodeTarget(exact));
        Assert.True(decoded <= exact);
        Assert.Equal((BigInteger.One << 220), decoded);
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlConsensus.EncodeTarget(BigInteger.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlConsensus.EncodeTarget(BigInteger.One << 256));
    }

    [Fact]
    public void ConsensusSaturationDoesNotMakePoolAssignmentTrivial()
    {
        Assert.Equal(new BigInteger(640), PearlConsensus.ScaleTarget(10, 64, false));
        Assert.Equal(PearlConsensus.MaximumTarget,
            PearlConsensus.ScaleTarget(PearlConsensus.MaximumTarget, 2, true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PearlConsensus.ScaleTarget(PearlConsensus.MaximumTarget, 2, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PearlConsensus.ScaleTarget(1, 0, true));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void CertificateAndHeaderMatchIndependentGoSerializer(uint version)
    {
        using var stream = typeof(PearlConsensusTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Pearl.wire-vectors.json");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var vector = JArray.Parse(reader.ReadToEnd()).Single(x => x.Value<uint>("version") == version);
        var publicData = Enumerable.Range(0, 164).Select(x => (byte) x).ToArray();
        var actual = PearlConsensus.SerializeCertifiedHeader(Header(), version, publicData,
            Convert.FromHexString("deadbeef"));
        Assert.Equal(vector.Value<string>("certifiedHeader"), Convert.ToHexString(actual).ToLowerInvariant());
        var header = actual[^108..];
        Assert.Equal(vector.Value<string>("header"), Convert.ToHexString(header).ToLowerInvariant());
        var hash = SHA256.HashData(SHA256.HashData(header));
        Array.Reverse(hash);
        Assert.Equal(vector.Value<string>("blockHash"), Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Theory]
    [InlineData(0u, 164, 4)]
    [InlineData(4u, 164, 4)]
    [InlineData(3u, 163, 4)]
    [InlineData(3u, 165, 4)]
    [InlineData(3u, 164, 0)]
    [InlineData(3u, 164, 60001)]
    public void UnsupportedOrMalformedCertificateIsRejected(uint version, int dataSize, int proofSize) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            PearlConsensus.SerializeCertifiedHeader(Header(), version, new byte[dataSize], new byte[proofSize]));

    [Fact]
    public void MalformedHeaderIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PearlConsensus.SerializeIncompleteHeader(1,
            new byte[31], new byte[32], 0, 0x1d00ffff));
        Assert.Throws<ArgumentException>(() => PearlConsensus.SerializeCertifiedHeader(
            new byte[80], 3, new byte[164], new byte[1]));
    }
}
