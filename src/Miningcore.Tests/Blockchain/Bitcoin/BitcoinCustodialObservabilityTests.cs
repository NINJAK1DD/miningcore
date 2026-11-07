using System;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Model;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPayoutHandlerTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task CustodialConfiguration_RejectsNonpositiveEffectiveThreshold(int threshold, bool template)
    {
        if(!template)
        {
            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                CreateFixtureAsync(minimumConfirmations: threshold));
            Assert.Contains("minimumConfirmations must be positive", error.Message);
            return;
        }
        var fixture = await CreateFixtureAsync();
        fixture.Config.Template = new BitcoinTemplate { Symbol = "BTC", Family = CoinFamily.Bitcoin,
            CoinbaseMinConfimations = threshold };
        await Assert.ThrowsAsync<PoolStartupException>(() => fixture.Handler.ConfigureAsync(
            new ClusterConfig(), fixture.Config, CancellationToken.None));
    }

    [Theory]
    [InlineData("generated", "wallet-generated-invalid")]
    [InlineData("malformed", "wallet-response-malformed")]
    [InlineData("category", "wallet-category-unsupported")]
    [InlineData("orphan", "wallet-orphan-chain-unverified")]
    [InlineData("wallet-missing", "active-block-wallet-transaction-missing")]
    [InlineData("wallet-details", "active-block-wallet-details-missing")]
    [InlineData("wallet-rpc", "wallet-rpc-error")]
    [InlineData("unknown-block", "wallet-missing-chain-unavailable")]
    [InlineData("chain-rpc", "chain-activity-unverified")]
    [InlineData("chain-height", "chain-height-mismatch")]
    [InlineData("chain-coinbase", "chain-coinbase-mismatch")]
    [InlineData("chain-depth", "chain-depth-below-policy")]
    public async Task CustodialStall_LogsOnceAlertsAfterGraceAndClearsOnlyOnRecovery(string fault, string reason)
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        block.ConfirmationProgress = 0.75;
        block.Reward = 25m;
        var target = new MemoryTarget { Layout = "${message}" };
        using var logs = new LogFactory();
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
        logs.Configuration = logging;
        async Task<HandlerFixture> Observe(int minute, bool healthy = false)
        {
            var fixture = await CreateFixtureAsync(tracker, start.AddMinutes(minute));
            fixture.Handler.SetLogger(logs.GetLogger("custodial-test"));
            fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
            if(!healthy)
            {
                var wallet = fixture.Handler.TransactionResponse[0].Response;
                switch(fault)
                {
                    case "generated": wallet["generated"] = false; break;
                    case "malformed": wallet["confirmations"] = "secret-invalid-confirmations"; break;
                    case "category": wallet["details"][0]["category"] = "secret-unknown-category"; break;
                    case "orphan": wallet["details"][0]["category"] = "orphan"; break;
                    case "wallet-details": wallet["details"] = new JArray(); break;
                    case "wallet-missing":
                    case "unknown-block":
                    case "wallet-rpc":
                        fixture.Handler.TransactionResponse = new[] { new RpcResponse<JToken>(null,
                            new JsonRpcError(fault == "wallet-rpc" ? -500 : -5, "secret-daemon-error", null)) };
                        break;
                }
                if(fault is "unknown-block" or "chain-rpc")
                    fixture.Handler.BlockResponse = new(null, new JsonRpcError(-5, "secret-block-error", null));
                if(fault == "chain-height") fixture.Handler.BlockResponse.Response.Height++;
                if(fault == "chain-coinbase") fixture.Handler.BlockResponse.Response.Transactions = new[] { "other" };
                if(fault == "chain-depth") fixture.Handler.BlockResponse.Response.Confirmations = 250;
            }
            await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
            return fixture;
        }
        var first = await Observe(0);
        first.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        Assert.Contains(reason, Assert.Single(target.Logs));
        Assert.DoesNotContain("secret-", target.Logs[0]);
        var progress = block.ConfirmationProgress;
        var repeat = await Observe(29);
        Assert.Equal(progress, block.ConfirmationProgress);
        Assert.False(block.NotifyBlockConfirmationProgressOnUpdate);
        repeat.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        var delayed = await Observe(30);
        delayed.MessageBus.Received(1).SendMessage(Arg.Is<AdminNotification>(x =>
            x.Subject.Contains("block reconciliation delayed") && x.Message.Contains(reason) &&
            !x.Message.Contains("secret-")), Arg.Any<string>());
        var stillDelayed = await Observe(90);
        stillDelayed.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.Single(target.Logs);
        await Observe(91, healthy: true);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        // A fresh pending observation is a new episode, including across handler instances.
        block.Status = BlockStatus.Pending;
        var fresh = await Observe(92);
        fresh.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.Equal(2, target.Logs.Count);
        var freshDelayed = await Observe(122);
        freshDelayed.MessageBus.Received(1).SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("wallet-missing")]
    [InlineData("details-missing")]
    [InlineData("orphan")]
    public async Task CustodialOrdinaryBlock_IndependentInactiveEvidenceOrphans(string fault)
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Reward = 25m;
        fixture.Handler.TransactionResponse = fault == "wallet-missing"
            ? new[] { new RpcResponse<JToken>(null, new JsonRpcError(-5, "missing", null)) }
            : SuccessTransaction("orphan", 50m, -1);
        if(fault == "details-missing") fixture.Handler.TransactionResponse[0].Response["details"] = new JArray();
        fixture.Handler.BlockResponse.Response.Confirmations = -1;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task CustodialLegacyRow_HashRecoveryClearsTheOriginalEpisode()
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        block.Hash = null;
        var first = await CreateFixtureAsync(tracker, start);
        first.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        await first.Handler.ClassifyBlocksAsync(first.Pool, new[] { block }, CancellationToken.None);
        block.Hash = "doge-block";
        var recovered = await CreateFixtureAsync(tracker, start.AddMinutes(10));
        recovered.Handler.TransactionResponse = SuccessTransaction("immature", 50m, 10);
        await recovered.Handler.ClassifyBlocksAsync(recovered.Pool, new[] { block }, CancellationToken.None);
        // If the missing-hash episode leaked, it would already be old enough to alert.
        Assert.False(tracker.TryAcquireNotification(block.PoolId, block.Id, null,
            "custodial-evidence", start.AddMinutes(31), TimeSpan.FromMinutes(30)));
        Assert.True(tracker.TryAcquireNotification(block.PoolId, block.Id, null,
            "custodial-evidence:warning", start.AddMinutes(31), TimeSpan.Zero));
    }

    [Fact]
    public async Task CustodialProgress_OnlyChangedValidObservationsNotify()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        fixture.Handler.TransactionResponse = SuccessTransaction("immature", 50m, 100);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.True(block.NotifyBlockConfirmationProgressOnUpdate);
        var progress = block.ConfirmationProgress;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.False(block.NotifyBlockConfirmationProgressOnUpdate);
        fixture.Handler.TransactionResponse[0].Response["generated"] = false;
        for(var i = 0; i < 3; i++)
        {
            await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
            Assert.Equal(progress, block.ConfirmationProgress);
            Assert.False(block.NotifyBlockConfirmationProgressOnUpdate);
        }
        fixture.Handler.TransactionResponse = SuccessTransaction("immature", 50m, 101);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.True(block.NotifyBlockConfirmationProgressOnUpdate);
    }
}
