using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxPowBlockConfirmationTests
{
    [Fact]
    public void PendingMarker_RoundTripsBlockHash()
    {
        const string blockHash = "0123456789abcdef";

        var marker = AuxPowBlockConfirmation.CreatePending(blockHash);
        var parsed = AuxPowBlockConfirmation.TryGetPendingBlockHash(marker, out var result);

        Assert.True(parsed);
        Assert.Equal(blockHash, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("regular-coinbase-txid")]
    [InlineData("auxpow-block:")]
    public void PendingMarker_RejectsNonMarkers(string value)
    {
        Assert.False(AuxPowBlockConfirmation.TryGetPendingBlockHash(value, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void AuxiliaryTemplateChange_DistinguishesTipFromSameHeightRefresh()
    {
        var previous = Template(100, "previous", "hash-one");

        Assert.Equal(AuxiliaryTemplateChange.None,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "previous", "hash-one")));
        Assert.Equal(AuxiliaryTemplateChange.Template,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "previous", "hash-two")));
        Assert.Equal(AuxiliaryTemplateChange.ChainTip,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(101, "hash-one", "hash-three")));
        Assert.Equal(AuxiliaryTemplateChange.ChainTip,
            MergedMiningBitcoinJobManager.ClassifyAuxiliaryTemplateChange(
                previous, Template(100, "different-previous", "hash-four")));
    }

    private static AuxBlockTemplate Template(uint height, string previousBlockHash, string hash)
    {
        return new AuxBlockTemplate
        {
            Height = height,
            PreviousBlockhash = previousBlockHash,
            Hash = hash,
        };
    }
}
