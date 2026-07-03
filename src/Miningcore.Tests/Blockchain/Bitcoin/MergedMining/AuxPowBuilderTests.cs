using System.Buffers.Binary;
using System.Security.Cryptography;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Extensions;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxPowBuilderTests
{
    [Fact]
    public void BuildCoinbaseCommitment_UsesDogecoinAuxPowLayout()
    {
        const string auxiliaryHash = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

        var result = AuxPowBuilder.BuildCoinbaseCommitment(auxiliaryHash);

        Assert.Equal(
            "fabe6d6d" + auxiliaryHash + "0100000000000000",
            result.ToHexString());
    }

    [Fact]
    public void BuildCoinbaseCommitment_RejectsInvalidHashLength()
    {
        Assert.Throws<ArgumentException>(() => AuxPowBuilder.BuildCoinbaseCommitment("abcd"));
    }

    [Fact]
    public void BuildAuxPow_SerializesCanonicalSingleChainProof()
    {
        var coinbase = new byte[] { 0x01, 0x02, 0x03 };
        var branchStep = Enumerable.Repeat((byte) 0x11, 32).ToArray();
        var header = Enumerable.Range(0, 80).Select(x => (byte) x).ToArray();

        var result = AuxPowBuilder.BuildAuxPow(coinbase, new[] { branchStep }, header);

        var offset = 0;
        Assert.Equal(coinbase, result.AsSpan(offset, coinbase.Length).ToArray());
        offset += coinbase.Length;

        using var sha256 = SHA256.Create();
        var expectedParentHash = sha256.ComputeHash(sha256.ComputeHash(header));
        Assert.Equal(expectedParentHash, result.AsSpan(offset, 32).ToArray());
        offset += 32;

        Assert.Equal(1, result[offset++]);
        Assert.Equal(branchStep, result.AsSpan(offset, 32).ToArray());
        offset += 32;

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(offset, 4)));
        offset += 4;

        Assert.Equal(0, result[offset++]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(offset, 4)));
        offset += 4;

        Assert.Equal(header, result.AsSpan(offset, 80).ToArray());
        offset += 80;

        Assert.Equal(result.Length, offset);
    }

    [Fact]
    public void BuildAuxPow_RejectsNonHeaderInput()
    {
        Assert.Throws<ArgumentException>(() =>
            AuxPowBuilder.BuildAuxPow(Array.Empty<byte>(), Array.Empty<byte[]>(), new byte[79]));
    }
}
