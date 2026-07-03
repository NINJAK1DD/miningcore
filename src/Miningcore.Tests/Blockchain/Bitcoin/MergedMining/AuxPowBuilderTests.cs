using System.Security.Cryptography;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Extensions;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxPowBuilderTests
{
    [Fact]
    public void BuildCoinbaseCommitment_UsesCanonicalSingleChainLayout()
    {
        var hash = string.Concat(Enumerable.Range(0, 32).Select(x => x.ToString("x2")));

        var result = AuxPowBuilder.BuildCoinbaseCommitment(hash);

        Assert.Equal(44, result.Length);
        Assert.Equal($"fabe6d6d{hash}0100000000000000", result.ToHexString());
    }

    [Fact]
    public void BuildAuxPow_SerializesDogecoinAuxPowFieldsInWireOrder()
    {
        var coinbase = new byte[] { 0x01, 0x02, 0x03 };
        var branchStep = Enumerable.Repeat((byte) 0xaa, 32).ToArray();
        var header = Enumerable.Repeat((byte) 0xbb, 80).ToArray();

        var result = AuxPowBuilder.BuildAuxPow(coinbase, new[] { branchStep }, header);

        Assert.Equal(157, result.Length);
        Assert.Equal(coinbase, result[..3]);

        using var sha256 = SHA256.Create();
        var expectedParentHash = sha256.ComputeHash(sha256.ComputeHash(header));
        Assert.Equal(expectedParentHash, result[3..35]);

        Assert.Equal(1, result[35]);
        Assert.Equal(branchStep, result[36..68]);
        Assert.Equal(new byte[4], result[68..72]);
        Assert.Equal(0, result[72]);
        Assert.Equal(new byte[4], result[73..77]);
        Assert.Equal(header, result[77..157]);
    }

    [Fact]
    public void BuildAuxPow_RejectsInvalidHeaderLength()
    {
        Assert.Throws<ArgumentException>(() =>
            AuxPowBuilder.BuildAuxPow(new byte[] { 0x01 }, Array.Empty<byte[]>(), new byte[79]));
    }
}
