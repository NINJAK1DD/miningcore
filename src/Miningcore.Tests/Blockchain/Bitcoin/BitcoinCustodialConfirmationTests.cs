using System;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Persistence.Model;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPayoutHandlerTests
{
    [Theory]
    [InlineData("litecoin", 102, "immature", 101, false)]
    [InlineData("litecoin", 102, "generate", 101, false)]
    [InlineData("litecoin", 102, "generate", 102, true)]
    [InlineData("litecoin", 102, "generate", 103, true)]
    [InlineData("dogecoin", 251, "generate", 61, false)]
    [InlineData("dogecoin", 251, "generate", 250, false)]
    [InlineData("dogecoin", 251, "generate", 251, true)]
    [InlineData("dogecoin", 251, "generate", 252, true)]
    [InlineData("dogecoin", 10, "generate", 10, true)]
    [InlineData("dogecoin", 500, "generate", 499, false)]
    [InlineData("dogecoin", 500, "generate", 500, true)]
    [InlineData("dogecoin", 10, "immature", 1000, false)]
    [InlineData("dogecoin", 0, "generate", 1000, false)]
    [InlineData("dogecoin", -1, "generate", 1000, false)]
    public async Task CustodialConfirmation_RequiresWalletMaturityAndOperatorThreshold(
        string coin, int threshold, string category, int confirmations, bool confirmed)
    {
        var fixture = await CreateFixtureAsync(coinTemplate: coin, minimumConfirmations: threshold);
        var block = PendingBlock("coinbase-txid");
        block.Type = coin == "litecoin" ? "merged-parent" : "auxpow";
        fixture.Handler.TransactionResponse = SuccessTransaction(category, 75m, confirmations);
        for(var cycle = 0; cycle < 2; cycle++)
        {
            Assert.Single(await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
                new[] { block }, CancellationToken.None));
            Assert.Equal(confirmed ? BlockStatus.Confirmed : BlockStatus.Pending, block.Status);
            Assert.Equal(confirmed, block.NotifyBlockUnlockedOnUpdate);
            Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
            if(threshold > 0)
                Assert.Equal(75m, block.Reward);
            Assert.Equal(confirmed ? 1 : threshold > 0
                ? Math.Min(0.999d, (double) confirmations / threshold) : 0,
                block.ConfirmationProgress);
        }
    }

    [Theory]
    [InlineData(false, 250, false)]
    [InlineData(false, 251, true)]
    [InlineData(true, 101, false)]
    [InlineData(true, 102, true)]
    public async Task CustodialConfirmation_DefaultsToTemplateThenFamily(
        bool familyFallback, int confirmations, bool confirmed)
    {
        Assert.Equal(102, BitcoinConstants.CoinbaseMinConfimations);
        var fixture = await CreateFixtureAsync(useFamilyFallback: familyFallback);
        fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, confirmations);
        var block = PendingBlock("coinbase-txid");
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(confirmed ? BlockStatus.Confirmed : BlockStatus.Pending, block.Status);
    }

    [Theory]
    [InlineData("confirmations", "null")]
    [InlineData("confirmations", "0")]
    [InlineData("confirmations", "-1")]
    [InlineData("confirmations", "251.1")]
    [InlineData("confirmations", "\"251\"")]
    [InlineData("confirmations", "\"invalid\"")]
    [InlineData("confirmations", "true")]
    [InlineData("confirmations", "2147483648")]
    [InlineData("confirmations", "{}")]
    [InlineData("confirmations", null)]
    [InlineData("generated", "false")]
    [InlineData("generated", "\"true\"")]
    [InlineData("walletconflicts", "[\"conflict\"]")]
    [InlineData("blockindex", null)]
    [InlineData("blockindex", "1")]
    [InlineData("txid", "\"other\"")]
    [InlineData("blockhash", "\"other\"")]
    [InlineData("amount", "-1")]
    [InlineData("details", "[null]")]
    [InlineData("details", "[{\"category\":\"orphan\"}]")]
    [InlineData("details", "[{\"category\":\"unknown\"}]")]
    [InlineData("details", "[{\"category\":\"generate\"},{\"category\":\"immature\"}]")]
    public async Task CustodialConfirmation_InvalidEvidencePreservesPendingReward(string field, string json)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        var evidence = (JObject) fixture.Handler.TransactionResponse[0].Response;
        if(json == null) evidence.Remove(field);
        else evidence[field] = JToken.Parse(json);
        var block = PendingBlock("coinbase-txid");
        block.Reward = 25m;
        block.NotifyBlockUnlockedOnUpdate = true; // stale transient flags must be cleared
        block.ConfirmationProgress = 1;
        for(var cycle = 0; cycle < 2; cycle++)
        {
            await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
            Assert.Equal(BlockStatus.Pending, block.Status);
            Assert.Equal(25m, block.Reward);
            Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
            Assert.Equal(0, block.ConfirmationProgress);
            Assert.False(block.NotifyBlockUnlockedOnUpdate);
        }
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("hash")]
    [InlineData("coinbase")]
    [InlineData("height")]
    [InlineData("confirmations")]
    [InlineData("reorg")]
    public async Task CustodialConfirmation_RequiresIndependentActiveCoinbaseAndRecovers(string fault)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        var original = fixture.Handler.BlockResponse;
        var evidence = new Miningcore.Blockchain.Bitcoin.DaemonResponses.Block
        {
            Hash = fault == "hash" ? "other" : "doge-block",
            Height = fault == "height" ? 101u : 100u,
            Confirmations = fault == "reorg" ? -1 : fault == "confirmations" ? 250 : 251,
            Transactions = new[] { fault == "coinbase" ? "other" : "coinbase-txid" },
        };
        fixture.Handler.BlockResponse = new RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>(
            fault == "unavailable" ? null : evidence);
        var block = PendingBlock("coinbase-txid");
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        fixture.Handler.BlockResponse = original;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
    }
}
