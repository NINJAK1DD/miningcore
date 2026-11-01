using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Miningcore.Blockchain.Pearl;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlPlainProofTests
{
    internal static byte[] RealProof()
    {
        using var stream = typeof(PearlPlainProofTests).Assembly.GetManifestResourceStream(
            "Miningcore.Tests.Fixtures.Pearl.srb-proof.bin.gz");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var result = new MemoryStream();
        gzip.CopyTo(result);
        return result.ToArray();
    }

    [Fact]
    public void RealMinerFixtureMatchesPinnedDimensionsAndDigest()
    {
        var proof = RealProof();
        Assert.Equal("59a12f6baa3c4b22baba3072950375c073882b0cf0a0f645040218202d3615ce",
            Convert.ToHexString(SHA256.HashData(proof)).ToLowerInvariant());
        var dimensions = PearlPlainProof.Inspect(proof);
        Assert.Equal(32772u, dimensions.M);
        Assert.Equal(15360u, dimensions.N);
        Assert.Equal(32768u, dimensions.K);
        Assert.Equal((ushort) 128, dimensions.Rank);
        Assert.Equal(8, dimensions.Rows);
        Assert.Equal(16, dimensions.Columns);
        Assert.Equal(4194304ul, dimensions.WorkFactor);
        Assert.Equal(dimensions.WorkFactor, dimensions.RankAdjustedWorkFactor);
    }

    [Theory]
    [InlineData(0, ulong.MaxValue)]
    [InlineData(8, 0ul)]
    [InlineData(16, 65537ul)]
    [InlineData(24, 0ul)]
    [InlineData(24, 129ul)]
    [InlineData(32, ulong.MaxValue)]
    [InlineData(40, ulong.MaxValue)]
    public void MaliciousDimensionsAndNestedCountsFailBeforeAllocation(int offset, ulong value)
    {
        var proof = RealProof();
        BinaryPrimitives.WriteUInt64LittleEndian(proof.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => PearlPlainProof.Inspect(proof));
    }

    [Fact]
    public void TruncatedAndOversizedDataFail()
    {
        var proof = RealProof();
        foreach(var length in new[] { 0, 31, 32, 40, 48, 100, proof.Length / 2, proof.Length - 9 })
            Assert.Throws<InvalidDataException>(() => PearlPlainProof.Inspect(proof.AsSpan(0, length)));
        Assert.Throws<InvalidDataException>(() => PearlPlainProof.Inspect(new byte[PearlPlainProof.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => PearlPlainProof.Inspect(proof.Concat(new byte[] {1, 2}).ToArray()));
    }

    [Fact]
    public void LegacyAndCurrentFormsHaveIdenticalCanonicalBytes()
    {
        var proof = RealProof();
        var dimensions = PearlPlainProof.Inspect(proof);
        var legacy = proof[..dimensions.CanonicalLength];
        var current = legacy.Concat(new byte[] {0}).ToArray();
        Assert.Equal(PearlProofEncoding.Decode(Convert.ToBase64String(legacy)),
            PearlProofEncoding.Decode(Convert.ToBase64String(current)));
        current[^1] = 1;
        Assert.Throws<InvalidDataException>(() => PearlPlainProof.Inspect(current));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressedAndRawFormsHaveIdenticalCanonicalBytes(bool gzip)
    {
        var proof = RealProof();
        using var compressed = new MemoryStream();
        using(Stream encoder = gzip
            ? new GZipStream(compressed, CompressionLevel.SmallestSize, true)
            : new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            encoder.Write(proof);
        Assert.Equal(PearlProofEncoding.Decode(Convert.ToBase64String(proof)),
            PearlProofEncoding.Decode(Convert.ToBase64String(compressed.ToArray())));
    }

    [Fact]
    public void DecompressionBombStopsAtBound()
    {
        using var compressed = new MemoryStream();
        using(var encoder = new GZipStream(compressed, CompressionLevel.SmallestSize, true))
            encoder.Write(new byte[PearlPlainProof.MaximumBytes + 1]);
        Assert.Throws<InvalidDataException>(() => PearlProofEncoding.Decode(Convert.ToBase64String(compressed.ToArray())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!!!")]
    [InlineData("AAAA\nAAA")]
    public void InvalidBase64Fails(string value) =>
        Assert.Throws<InvalidDataException>(() => PearlProofEncoding.Decode(value));
}
