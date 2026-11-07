using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.JsonRpc;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence.Model;
using Miningcore.Rpc;
using Newtonsoft.Json;
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
    [InlineData("wallet", "http")]
    [InlineData("wallet", "decode")]
    [InlineData("wallet", "timeout")]
    [InlineData("wallet", "batch")]
    [InlineData("chain", "http")]
    [InlineData("chain", "decode")]
    [InlineData("chain", "timeout")]
    [InlineData("missing", "http")]
    [InlineData("orphan", "decode")]
    [InlineData("auxiliary", "http")]
    [InlineData("auxiliary", "wrapped")]
    public async Task CustodialTransportFailures_StayPendingAndAlert(string path, string failure)
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock(path == "auxiliary" ? "auxpow-block:doge-block" : "coinbase-txid");
        block.Reward = 25m;
        block.ConfirmationProgress = 0.75;
        Exception Fault() => failure switch
        {
            "http" => new HttpRequestException("secret-transport"),
            "decode" => new JsonReaderException("secret-response"),
            _ => new TaskCanceledException("secret-timeout"),
        };
        for(var minute = 0; minute <= 30; minute += 30)
        {
            var fixture = await CreateFixtureAsync(tracker, start.AddMinutes(minute));
            fixture.Handler.TransactionResponse = path == "missing"
                ? new[] { new RpcResponse<JToken>(null, new JsonRpcError(-5, "secret-missing", null)) }
                : SuccessTransaction(path == "orphan" ? "orphan" : "generate", 50m, 251);
            if(path == "wallet")
                fixture.Handler.WalletLookupOverride = _ => failure == "batch"
                    ? Task.FromResult(Array.Empty<RpcResponse<JToken>>())
                    : Task.FromException<RpcResponse<JToken>[]>(Fault());
            else if(failure == "wrapped")
                fixture.Handler.BlockLookupOverride = _ => Task.FromResult(new RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>(
                    null, new JsonRpcError(-500, "secret-wrapped", null, new HttpRequestException("secret-transport"))));
            else
                fixture.Handler.BlockLookupOverride = _ => Task.FromException<RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>>(Fault());
            Assert.Single(await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None));
            Assert.Equal(BlockStatus.Pending, block.Status);
            Assert.False(block.NotifyBlockUnlockedOnUpdate);
            if(path == "wallet")
            {
                Assert.Equal(25m, block.Reward);
                Assert.Equal(0.75, block.ConfirmationProgress);
                Assert.False(block.NotifyBlockConfirmationProgressOnUpdate);
            }
            if(minute == 0)
                fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
            else
                fixture.MessageBus.Received(1).SendMessage(Arg.Is<AdminNotification>(x =>
                    !x.Message.Contains("secret-")), Arg.Any<string>());
        }
        var recovered = await CreateFixtureAsync(tracker, start.AddMinutes(31));
        recovered.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        await recovered.Handler.ClassifyBlocksAsync(recovered.Pool, new[] { block }, CancellationToken.None);
        if(path == "auxiliary")
            await recovered.Handler.ClassifyBlocksAsync(recovered.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CustodialRpc_ShutdownAndProgrammingErrorsPropagate(bool chain, bool wrapped)
    {
        foreach(var cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            var fixture = await CreateFixtureAsync();
            fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
            Exception Fault()
            {
                if(!cancel) return new InvalidOperationException("unexpected-bug");
                cts.Cancel();
                return new OperationCanceledException(cts.Token);
            }
            if(chain)
                fixture.Handler.BlockLookupOverride = _ =>
                {
                    var error = Fault();
                    return wrapped ? Task.FromResult(new RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>(
                        null, new JsonRpcError(-500, "withheld", null, error)))
                        : Task.FromException<RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>>(error);
                };
            else
                fixture.Handler.WalletLookupOverride = _ =>
                {
                    var error = Fault();
                    return wrapped ? Task.FromResult(new[] { new RpcResponse<JToken>(
                        null, new JsonRpcError(-500, "withheld", null, error)) })
                        : Task.FromException<RpcResponse<JToken>[]>(error);
                };
            var block = PendingBlock("coinbase-txid");
            Task Run() => fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, cts.Token);
            if(cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.NotEqual(BlockStatus.Confirmed, block.Status);
            fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        }
    }

    [Fact]
    public void CustodialDailyReminder_ConcurrentObserversAcquireOnlyOneNotification()
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        Assert.True(tracker.TryAcquireNotification("pool", 1, null, "custodial", start,
            TimeSpan.Zero, repeatAfter: TimeSpan.FromDays(1)));
        tracker.MarkNotificationSent("pool", 1, null, "custodial");
        var acquired = 0;
        Parallel.For(0, 32, _ =>
        {
            if(tracker.TryAcquireNotification("pool", 1, null, "custodial", start.AddDays(1),
                TimeSpan.Zero, repeatAfter: TimeSpan.FromDays(1)))
            {
                Interlocked.Increment(ref acquired);
                tracker.MarkNotificationSent("pool", 1, null, "custodial");
            }
        });
        Assert.Equal(1, acquired);
    }

    [Fact]
    public async Task CustodialWarnings_ReasonChangesAndDailyRemindersRecoverAndRetry()
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        var target = new MemoryTarget { Layout = "${message}" };
        using var logs = new LogFactory();
        var config = new LoggingConfiguration();
        config.AddRule(LogLevel.Warn, LogLevel.Fatal, target);
        logs.Configuration = config;
        async Task<HandlerFixture> Observe(int minute, bool chain = false, bool failNotification = false, bool healthy = false)
        {
            var fixture = await CreateFixtureAsync(tracker, start.AddMinutes(minute));
            fixture.Handler.SetLogger(logs.GetLogger("custodial"));
            fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
            if(!healthy)
            {
                if(chain) fixture.Handler.BlockResponse.Response.Transactions = new[] { "other" };
                else fixture.Handler.TransactionResponse[0].Response["generated"] = false;
            }
            if(failNotification)
                fixture.MessageBus.When(x => x.SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>()))
                    .Do(_ => throw new IOException("secret-notification"));
            await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
            return fixture;
        }
        await Observe(0);
        await Observe(1);
        Assert.Single(target.Logs);
        await Observe(10, chain: true);
        await Observe(11, chain: true);
        Assert.Equal(2, target.Logs.Count);
        Assert.Contains("chain-coinbase-mismatch", target.Logs[1]);
        for(var minute = 12; minute < 20; minute++)
            await Observe(minute, chain: minute % 2 == 0);
        Assert.Equal(2, target.Logs.Count); // Alternating already-seen reasons do not flood logs.
        var firstAlert = await Observe(30, chain: true);
        firstAlert.MessageBus.Received(1).SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        var early = await Observe(1469, chain: true);
        early.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        await Observe(1470, chain: true, failNotification: true);
        var retry = await Observe(1471, chain: true);
        retry.MessageBus.Received(1).SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        var repeated = await Observe(1472, chain: true);
        repeated.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.DoesNotContain(target.Logs, x => x.Contains("secret-"));
        await Observe(1500, healthy: true);
        block.Status = BlockStatus.Pending;
        var newEpisode = await Observe(1501);
        newEpisode.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
        Assert.Contains("wallet-generated-invalid", target.Logs[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustodialEpisode_SurvivesMarkerTypeChangeAndClearsOnRecovery(bool parent)
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock(parent ? AuxPowBlockConfirmation.CreateParentUncertain("doge-block")
            : AuxPowBlockConfirmation.CreateClaim("doge-block", "parent-header"));
        block.Type = parent ? "parent-uncertain" : "auxpow-claim";
        var first = await CreateFixtureAsync(tracker, start);
        first.Handler.BlockLookupOverride = _ => Task.FromException<RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>>(new IOException());
        await first.Handler.ClassifyBlocksAsync(first.Pool, new[] { block }, CancellationToken.None);
        var resolved = await CreateFixtureAsync(tracker, start.AddMinutes(10));
        resolved.Handler.BlockResponse.Response.AuxPow = new AuxPow { ParentBlock = "parent-header" };
        await resolved.Handler.ClassifyBlocksAsync(resolved.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(parent ? "merged-parent" : "auxpow", block.Type);
        Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
        Assert.Equal(0, resolved.Handler.TransactionCalls);

        var stalled = await CreateFixtureAsync(tracker, start.AddMinutes(31));
        stalled.Handler.WalletLookupOverride = _ => Task.FromException<RpcResponse<JToken>[]>(new HttpRequestException());
        await stalled.Handler.ClassifyBlocksAsync(stalled.Pool, new[] { block }, CancellationToken.None);
        stalled.MessageBus.Received(1).SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());

        var recovered = await CreateFixtureAsync(tracker, start.AddMinutes(32));
        recovered.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        await recovered.Handler.ClassifyBlocksAsync(recovered.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        // Returning to the original type must not find an abandoned pre-normalization episode.
        block.Type = parent ? "parent-uncertain" : "auxpow-claim";
        block.Status = BlockStatus.Pending;
        var next = await CreateFixtureAsync(tracker, start.AddMinutes(33));
        next.Handler.WalletLookupOverride = _ => Task.FromException<RpcResponse<JToken>[]>(new HttpRequestException());
        await next.Handler.ClassifyBlocksAsync(next.Pool, new[] { block }, CancellationToken.None);
        next.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CustodialClassification_LogsEligibilityWithoutClaimingCommittedUnlock()
    {
        var fixture = await CreateFixtureAsync();
        var target = new MemoryTarget { Layout = "${message}" };
        using var logs = new LogFactory();
        var config = new LoggingConfiguration();
        config.AddRule(LogLevel.Info, LogLevel.Fatal, target);
        logs.Configuration = config;
        fixture.Handler.SetLogger(logs.GetLogger("custodial"));
        fixture.Handler.TransactionResponse = SuccessTransaction("generate", 50m, 251);
        var block = PendingBlock("coinbase-txid");
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        Assert.Contains(target.Logs, x => x.Contains("eligible for custodial settlement; awaiting database commit"));
        Assert.DoesNotContain(target.Logs, x => x.Contains("Unlocked") || x.Contains("Committed"));
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>());
    }
}
