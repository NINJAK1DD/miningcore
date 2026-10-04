using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using Block = Miningcore.Persistence.Model.Block;
using BlockStatus = Miningcore.Persistence.Model.BlockStatus;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bPayoutTests : TestBase
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TxId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static Block Reward() => new()
    {
        Id = 1, PoolId = "review", BlockHeight = 30, Hash = Hash,
        TransactionConfirmationData = TxId, Status = BlockStatus.Pending, Reward = 49,
    };

    private sealed class Handler : BitcoinBlake2bPayoutHandler
    {
        internal JObject Wallet = JObject.FromObject(new Transaction
        {
            Generated = true, TxId = TxId, BlockHash = Hash, Confirmations = 101, Amount = 50,
            Details = new[] { new TransactionDetails { Category = "generate", Vout = 0, Amount = 50 } },
        }, JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        }));
        internal JsonRpcError WalletError;
        internal JObject Header = new() { ["hash"] = Hash, ["height"] = 30, ["confirmations"] = 101 };
        internal bool HeaderUnavailable;
        internal bool HeaderMalformed;
        internal string Chain = "regtest";
        internal int WalletRequests;
        internal string AttestationFailureKind;
        internal bool UseRealContractRpc;
        internal JsonRpcError ObservedContractError;
        internal void SetLogger(NLog.ILogger value) => logger = value;

        internal Handler(IComponentContext context, IMasterClock clock, IMessageBus messages,
            IActiveBlockGracePeriodTracker grace, BitcoinBlake2bPayoutContractTracker contracts)
            : base(context, Substitute.For<IConnectionFactory>(), AutoMapperFactory.CreateMapper(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>(),
                Substitute.For<IPaymentRepository>(), clock, messages, grace, contracts) { }

        protected override Task<RpcResponse<JObject>> ReadBlake2bPayoutContractAsync(string method, CancellationToken ct)
        {
            if(UseRealContractRpc) return ReadRealContract(method, ct);
            ct.ThrowIfCancellationRequested();
            if(AttestationFailureKind == "transport") throw new HttpRequestException("private daemon credential");
            if(AttestationFailureKind == "malformed") throw new JsonSerializationException("private daemon credential");
            if(AttestationFailureKind == "rpc") return Task.FromResult(new RpcResponse<JObject>(null, new JsonRpcError(-500, "private daemon credential", null)));
            var coin = (BitcoinBlake2bTemplate) poolConfig.Template;
            var deployment = Chain == "main" ? BitcoinBlake2bMaturityTests.Deployment(BitcoinBlake2bMaturity.Mainnet, 973439)
                : new JObject { ["height"] = 130, ["deployments"] = new JObject() };
            deployment["blake2b"] = new JObject { ["height"] = coin.Networks[Chain].Blake2bActivationHeight!.Value, ["active"] = true };
            if(AttestationFailureKind == "deployment") deployment["blake2b"]["height"] = 999;
            return Task.FromResult(new RpcResponse<JObject>(method switch
            {
                "getnetworkinfo" => new JObject { ["version"] = 290402, ["subversion"] = AttestationFailureKind == "identity" ? "private daemon credential" : "/Satoshi:29.4.2(operator)/Knots:20260508/Miningcore:1/" },
                "getblockchaininfo" => new JObject { ["chain"] = Chain, ["initialblockdownload"] = AttestationFailureKind == "syncing" },
                "getdeploymentinfo" => deployment,
                _ => throw new InvalidOperationException(method),
            }));
        }

        private async Task<RpcResponse<JObject>> ReadRealContract(string method, CancellationToken ct)
        {
            var result = await base.ReadBlake2bPayoutContractAsync(method, ct);
            if(result.Error != null) ObservedContractError = result.Error;
            return result;
        }

        protected override Task<RpcResponse<JObject>> GetBlockHeaderAsync(string hash, CancellationToken ct) =>
            HeaderMalformed ? throw new JsonSerializationException("Malformed header") : Task.FromResult(HeaderUnavailable
                ? new RpcResponse<JObject>(null, new JsonRpcError(-500, "Unavailable", null)) : new(Header));

        protected override Task<RpcResponse<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>> GetBlockAsync(string hash, CancellationToken ct) =>
            throw new InvalidOperationException("BLAKE2b reconciliation must not require block bodies");

        protected override Task<RpcResponse<JToken>[]> GetTransactionsAsync(Block[] blocks, CancellationToken ct)
        {
            WalletRequests += blocks.Length;
            return Task.FromResult(blocks.Select(_ => new RpcResponse<JToken>(Wallet, WalletError)).ToArray());
        }
    }

    private static PoolConfig Config() => new()
    {
        Id = "review", Coin = "bitcoin-blake2b", Template = ModuleInitializer.CoinTemplates["bitcoin-blake2b"],
        Address = "unused", Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 1 } },
        Extra = new Dictionary<string, object> { ["minimumConfirmations"] = 1 },
        PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true },
    };

    private async Task<(Handler Handler, IMiningPool Pool, IMessageBus Messages)> Fixture(
        IMasterClock clock = null, IActiveBlockGracePeriodTracker grace = null, BitcoinBlake2bPayoutContractTracker contracts = null,
        IMessageBus messages = null)
    {
        clock ??= Substitute.For<IMasterClock>();
        messages ??= Substitute.For<IMessageBus>();
        var handler = new Handler(container, clock, messages, grace ?? new ActiveBlockGracePeriodTracker(),
            contracts ?? new BitcoinBlake2bPayoutContractTracker());
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(Config());
        await handler.ConfigureAsync(new ClusterConfig(), pool.Config, CancellationToken.None);
        return (handler, pool, messages);
    }

    [Theory]
    [InlineData("wallet-error", 1, BlockStatus.Pending)]
    [InlineData("wallet-error", -1, BlockStatus.Orphaned)]
    [InlineData("wallet-error", 0, BlockStatus.Pending)]
    [InlineData("missing-details", 1, BlockStatus.Pending)]
    [InlineData("missing-details", -1, BlockStatus.Orphaned)]
    [InlineData("missing-details", 0, BlockStatus.Pending)]
    public async Task MissingWalletEvidence_RequiresHeaderProofBeforeOrphaning(string failure, int activity, BlockStatus expected)
    {
        var fixture = await Fixture();
        if(failure == "wallet-error") fixture.Handler.WalletError = new(-5, "Not indexed", null);
        else fixture.Handler.Wallet["details"] = new JArray();
        fixture.Handler.Header["confirmations"] = activity;
        var reward = Reward();
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(expected, reward.Status);
        Assert.Equal(expected == BlockStatus.Orphaned ? 0m : 49m, reward.Reward);
        Assert.Empty(fixture.Messages.ReceivedCalls());
    }

    [Theory]
    [InlineData("height")]
    [InlineData("hash")]
    [InlineData("txid")]
    [InlineData("blockhash")]
    [InlineData("generated")]
    [InlineData("category")]
    [InlineData("missing-confirmations")]
    [InlineData("negative-confirmations")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    [InlineData("malformed-wallet")]
    [InlineData("negative-credit")]
    [InlineData("duplicate-output")]
    [InlineData("mixed-category")]
    [InlineData("overflow-credit")]
    public async Task ContradictionsAndUnavailableEvidence_HoldRewardAndEmitOneBoundedFamilyAlert(string failure)
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var fixture = await Fixture(clock);
        switch(failure)
        {
            case "height": fixture.Handler.Header["height"] = 31; break;
            case "hash": fixture.Handler.Header["hash"] = new string('c', 64); break;
            case "txid": fixture.Handler.Wallet["txId"] = "wrong"; break;
            case "blockhash": fixture.Handler.Wallet["blockHash"] = "wrong"; break;
            case "generated": fixture.Handler.Wallet["generated"] = false; break;
            case "category": fixture.Handler.Wallet["details"][0]["category"] = "orphan"; break;
            case "missing-confirmations": fixture.Handler.Header.Property("confirmations").Remove(); break;
            case "negative-confirmations": fixture.Handler.Header["confirmations"] = -2; break;
            case "unavailable": fixture.Handler.HeaderUnavailable = true; break;
            case "malformed": fixture.Handler.HeaderMalformed = true; break;
            case "malformed-wallet": fixture.Handler.Wallet["details"] = "bad"; break;
            case "negative-credit": fixture.Handler.Wallet["details"][0]["amount"] = -1m; break;
            case "duplicate-output": fixture.Handler.Wallet["details"] = new JArray(
                new JObject { ["category"] = "generate", ["amount"] = 25m, ["vout"] = 0 },
                new JObject { ["category"] = "generate", ["amount"] = 25m, ["vout"] = 0 }); break;
            case "mixed-category": fixture.Handler.Wallet["details"] = new JArray(
                new JObject { ["category"] = "generate", ["amount"] = 25m, ["vout"] = 0 },
                new JObject { ["category"] = "immature", ["amount"] = 25m, ["vout"] = 1 }); break;
            case "overflow-credit": fixture.Handler.Wallet["details"] = new JArray(
                new JObject { ["category"] = "generate", ["amount"] = decimal.MaxValue, ["vout"] = 0 },
                new JObject { ["category"] = "generate", ["amount"] = decimal.MaxValue, ["vout"] = 1 }); break;
        }
        var reward = Reward();
        async Task Classify() => await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        await Classify();
        Assert.Empty(fixture.Messages.ReceivedCalls());
        now = now.AddMinutes(31);
        await Classify();
        await Classify();
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.Equal(49m, reward.Reward);
        Assert.False(reward.NotifyBlockUnlockedOnUpdate);
        var alert = Assert.Single(fixture.Messages.ReceivedCalls()).GetArguments()[0] as AdminNotification;
        Assert.Contains("Bitcoin BLAKE2b", alert.Subject);
        Assert.DoesNotContain("merged-mining", alert.Subject);
        Assert.Contains("getblockheader/gettransaction", alert.Message);
        Assert.DoesNotContain("wrong", alert.Message);
    }

    [Fact]
    public async Task VerifiedRecovery_ClearsEpisodeAcrossHandlerRecreationWithoutAlertSpam()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var grace = new ActiveBlockGracePeriodTracker();
        var fixture = await Fixture(clock, grace);
        var reward = Reward();
        fixture.Handler.HeaderUnavailable = true;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        now = now.AddMinutes(31);
        var nextCycle = await Fixture(clock, grace, messages: fixture.Messages);
        nextCycle.Handler.HeaderUnavailable = true;
        await nextCycle.Handler.ClassifyBlocksAsync(nextCycle.Pool, new[] { reward }, CancellationToken.None);
        Assert.Single(fixture.Messages.ReceivedCalls());
        nextCycle.Handler.HeaderUnavailable = false;
        await nextCycle.Handler.ClassifyBlocksAsync(nextCycle.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, reward.Status);
        nextCycle.Handler.HeaderUnavailable = true;
        await nextCycle.Handler.ClassifyBlocksAsync(nextCycle.Pool, new[] { reward }, CancellationToken.None);
        Assert.Single(fixture.Messages.ReceivedCalls());
        now = now.AddMinutes(31);
        await nextCycle.Handler.ClassifyBlocksAsync(nextCycle.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(2, fixture.Messages.ReceivedCalls().Count());
    }

    [Fact]
    public async Task ImmatureCredit_PreservesOwnedOutputValueAndCannotUnlockOnProgressOrRelease()
    {
        var fixture = await Fixture();
        fixture.Handler.Wallet["amount"] = 0;
        fixture.Handler.Wallet["details"][0]["category"] = "immature";
        fixture.Handler.Wallet["details"] = new JArray(
            new JObject { ["category"] = "immature", ["amount"] = 30m, ["vout"] = 0 },
            new JObject { ["category"] = "immature", ["amount"] = 20m, ["vout"] = 1 });
        var reward = Reward();
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { reward }, CancellationToken.None);
        Assert.Equal(50m, reward.Reward);
        Assert.Equal(BlockStatus.Pending, reward.Status);
        Assert.InRange(reward.ConfirmationProgress, 0, Math.BitDecrement(1));
        Assert.False(reward.NotifyBlockUnlockedOnUpdate);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("legacy-observed")]
    public async Task QuarantinedDirectRow_DoesNotPreventHealthyCustodialRewardClassification(string submissionState)
    {
        var fixture = await Fixture();
        var unsupported = Reward();
        unsupported.SettlementMode = BitcoinDirectCoinbaseSettlement.Mode;
        unsupported.DirectSubmissionState = submissionState;
        unsupported.NotifyBlockFoundOnUpdate = true;
        var healthy = Reward();
        healthy.Id = 2;
        var classified = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { unsupported, healthy }, CancellationToken.None);
        Assert.Equal(2, classified.Length);
        Assert.Equal(BlockStatus.Quarantined, unsupported.Status);
        Assert.Equal(submissionState == "legacy-observed" ? "legacy-observed" : "quarantined", unsupported.DirectSubmissionState);
        Assert.False(unsupported.NotifyBlockFoundOnUpdate);
        Assert.False(unsupported.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(BlockStatus.Confirmed, healthy.Status);
        Assert.Equal(1, fixture.Handler.WalletRequests);
    }

    [Fact]
    public async Task ProcessContractBinding_SurvivesConfigureAndHandlerRecreation()
    {
        var contracts = new BitcoinBlake2bPayoutContractTracker();
        var fixture = await Fixture(contracts: contracts);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, CancellationToken.None);
        await fixture.Handler.ConfigureAsync(new ClusterConfig(), fixture.Pool.Config, CancellationToken.None);
        fixture.Handler.Chain = "main";
        var changed = await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(() => fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, CancellationToken.None));
        Assert.Equal(BitcoinBlake2bPayoutAttestationReason.BindingMismatch, changed.Reason);
        var replacement = await Fixture(contracts: contracts);
        replacement.Handler.Chain = "main";
        await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(() => replacement.Handler.ClassifyBlocksAsync(replacement.Pool, new[] { Reward() }, CancellationToken.None));
    }

    [Theory]
    [InlineData("rpc", 701, false)]
    [InlineData("transport", 701, false)]
    [InlineData("syncing", 702, false)]
    [InlineData("identity", 703, true)]
    [InlineData("malformed", 703, true)]
    [InlineData("deployment", 703, true)]
    [InlineData("binding", 704, true)]
    public async Task PayoutAttestation_FixedReasonsAreRedactedAndBoundedAcrossHandlerRecreation(string kind, int code, bool immediate)
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var grace = new ActiveBlockGracePeriodTracker();
        var contracts = new BitcoinBlake2bPayoutContractTracker();
        var fixture = await Fixture(clock, grace, contracts);
        if(kind == "binding")
        {
            await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, CancellationToken.None);
            fixture.Handler.Chain = "main";
        }
        fixture.Handler.AttestationFailureKind = kind;
        using var logs = new Miningcore.Tests.Rpc.RpcDiagnosticTests.CapturedLogs();
        fixture.Handler.SetLogger(logs.Logger);
        async Task Fail(Handler handler) => Assert.Equal(code, (int)
            (await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(() =>
                handler.PayoutAsync(fixture.Pool, Array.Empty<Miningcore.Persistence.Model.Balance>(), CancellationToken.None))).Reason);
        await Fail(fixture.Handler);
        Assert.Equal(immediate ? 1 : 0, fixture.Messages.ReceivedCalls().Count());
        now = now.AddMinutes(31);
        var replacement = await Fixture(clock, grace, contracts, fixture.Messages);
        replacement.Handler.SetLogger(logs.Logger);
        replacement.Handler.AttestationFailureKind = kind;
        if(kind == "binding") replacement.Handler.Chain = "main";
        await Fail(replacement.Handler);
        await Fail(replacement.Handler);
        var alert = Assert.Single(fixture.Messages.ReceivedCalls()).GetArguments()[0] as AdminNotification;
        Assert.Contains(code.ToString(), alert.Message);
        Assert.DoesNotContain("private daemon credential", alert.Message);
        Assert.Contains(logs.Messages, x => x.Contains("BitcoinBlake2bPayoutHandler.AttestAsync", StringComparison.Ordinal) && x.Contains(code.ToString(), StringComparison.Ordinal));
        Assert.All(logs.Messages, x => Assert.DoesNotContain("private daemon credential", x));
        replacement.Handler.AttestationFailureKind = null;
        replacement.Handler.Chain = "regtest";
        await replacement.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, CancellationToken.None);
        replacement.Handler.AttestationFailureKind = kind;
        if(kind == "binding") replacement.Handler.Chain = "main";
        await Fail(replacement.Handler);
        now = now.AddMinutes(31);
        await Fail(replacement.Handler);
        Assert.Equal(2, fixture.Messages.ReceivedCalls().Count());
    }

    [Fact]
    public async Task UnknownOrphanEvidence_DoesNotReopenOrReannounceStoredOrphan()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var grace = new ActiveBlockGracePeriodTracker();
        var fixture = await Fixture(clock, grace);
        var orphan = Reward();
        orphan.Status = BlockStatus.Orphaned;
        orphan.Reward = 0;
        fixture.Handler.HeaderUnavailable = true;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        now = now.AddMinutes(31);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        var replacement = await Fixture(clock, grace, messages: fixture.Messages);
        replacement.Handler.HeaderUnavailable = true;
        await replacement.Handler.ClassifyBlocksAsync(replacement.Pool, new[] { orphan }, CancellationToken.None);
        Assert.Empty(fixture.Messages.ReceivedCalls());
        Assert.Equal(BlockStatus.Orphaned, orphan.Status);
        Assert.False(orphan.BitcoinBlake2bCustodialEvidenceVerified);
        fixture.Handler.HeaderUnavailable = false;
        fixture.Handler.Header["confirmations"] = -1;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        Assert.Equal(BlockStatus.Orphaned, orphan.Status);
        Assert.False(orphan.NotifyBlockUnlockedOnUpdate);
        fixture.Handler.Header["confirmations"] = 101;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        Assert.Equal(BlockStatus.Confirmed, orphan.Status);
        Assert.True(orphan.BitcoinBlake2bCustodialEvidenceVerified);
    }

    [Fact]
    public async Task ActiveStoredOrphanWithoutWalletProof_StillAlertsAfterGrace()
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var fixture = await Fixture(clock);
        fixture.Handler.WalletError = new(-5, "private daemon credential", null);
        var orphan = Reward();
        orphan.Status = BlockStatus.Orphaned;
        orphan.Reward = 0;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        now = now.AddMinutes(31);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { orphan }, CancellationToken.None);
        var alert = Assert.IsType<AdminNotification>(Assert.Single(fixture.Messages.ReceivedCalls()).GetArguments()[0]);
        Assert.Contains("unresolved", alert.Message);
        Assert.DoesNotContain("remains pending", alert.Message);
        Assert.DoesNotContain("private daemon credential", alert.Message);
        Assert.Equal(BlockStatus.Orphaned, orphan.Status);
    }

    [Theory]
    [InlineData("getnetworkinfo", "wrong-result", 703)]
    [InlineData("getblockchaininfo", "wrong-result", 703)]
    [InlineData("getdeploymentinfo", "wrong-result", 703)]
    [InlineData("getnetworkinfo", "wrong-envelope", 703)]
    [InlineData("getnetworkinfo", "null-envelope", 703)]
    [InlineData("getnetworkinfo", "scalar-result", 703)]
    [InlineData("getdeploymentinfo", "null-result", 703)]
    [InlineData("getnetworkinfo", "wrong-error-code", 703)]
    [InlineData("getdeploymentinfo", "missing-method", 703)]
    [InlineData("getblockchaininfo", "missing-method", 703)]
    [InlineData("getnetworkinfo", "missing-method", 703)]
    [InlineData("getdeploymentinfo", "truncated", 701)]
    [InlineData("getnetworkinfo", "malformed", 701)]
    [InlineData("getblockchaininfo", "rpc-outage", 701)]
    public async Task RealRpcClient_AttestationClassifiesStructuralErrorsWithoutLeakingPayload(string method, string fault, int code)
    {
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(_ => now);
        var grace = new ActiveBlockGracePeriodTracker();
        var fixture = await Fixture(clock, grace);
        var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var server = await Miningcore.Tests.Rpc.RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new System.IO.StreamReader(context.Request.Body);
            var request = JObject.Parse(await reader.ReadToEndAsync());
            var current = request["method"].Value<string>();
            requests.Add(current);
            var coin = (BitcoinBlake2bTemplate) fixture.Pool.Config.Template;
            JToken result = current switch
            {
                "getnetworkinfo" => new JObject { ["version"] = 290402, ["subversion"] = "/Satoshi:29.4.2/Knots:20260508/" },
                "getblockchaininfo" => new JObject { ["chain"] = "regtest", ["initialblockdownload"] = false },
                "getdeploymentinfo" => new JObject { ["height"] = 130, ["deployments"] = new JObject(),
                    ["blake2b"] = new JObject { ["height"] = coin.Networks["regtest"].Blake2bActivationHeight!.Value, ["active"] = true } },
                _ => throw new InvalidOperationException("Unexpected wallet operation during attestation"),
            };
            var response = new JObject { ["result"] = result, ["error"] = null, ["id"] = request["id"] };
            var text = response.ToString(Formatting.None);
            if(current == method)
            {
                switch(fault)
                {
                    case "wrong-result": response["result"] = new JArray("private daemon credential"); text = response.ToString(Formatting.None); break;
                    case "scalar-result": response["result"] = "private daemon credential"; text = response.ToString(Formatting.None); break;
                    case "null-result": response["result"] = null; text = response.ToString(Formatting.None); break;
                    case "wrong-error-code": response["result"] = null; response["error"] = new JObject { ["code"] = "private daemon credential", ["message"] = "private daemon credential" }; text = response.ToString(Formatting.None); break;
                    case "wrong-envelope": text = "[\"private daemon credential\"]"; break;
                    case "null-envelope": text = "null"; break;
                    case "missing-method":
                    case "rpc-outage": response["result"] = null; response["error"] = new JObject { ["code"] = fault == "missing-method" ? -32601 : -28, ["message"] = "private daemon credential" }; text = response.ToString(Formatting.None); break;
                    case "truncated": text = "{\"result\":{\"private daemon credential\":"; break;
                    case "malformed": text = "private daemon credential"; break;
                }
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(text);
        });
        fixture.Pool.Config.Daemons = new[] { server.Endpoint() };
        await fixture.Handler.ConfigureAsync(new ClusterConfig(), fixture.Pool.Config, CancellationToken.None);
        fixture.Handler.UseRealContractRpc = true;
        using var logs = new Miningcore.Tests.Rpc.RpcDiagnosticTests.CapturedLogs();
        fixture.Handler.SetLogger(logs.Logger);
        async Task Fail() => Assert.Equal(code, (int) (await Assert.ThrowsAsync<BitcoinBlake2bPayoutAttestationException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, Array.Empty<Miningcore.Persistence.Model.Balance>(), CancellationToken.None))).Reason);
        int Alerts() => fixture.Messages.ReceivedCalls().Count(x => x.GetArguments()[0] is AdminNotification);
        await Fail();
        Assert.Equal(code == 703 ? 1 : 0, Alerts());
        if(fault is "wrong-result" or "wrong-envelope" or "null-envelope" or "scalar-result" or "wrong-error-code") Assert.IsType<JsonSerializationException>(fixture.Handler.ObservedContractError.InnerException);
        if(fault is "truncated" or "malformed") Assert.IsType<JsonReaderException>(fixture.Handler.ObservedContractError.InnerException);
        now = now.AddMinutes(31);
        await Fail();
        await Fail();
        Assert.Equal(1, Alerts());
        Assert.All(requests, value => Assert.Contains(value, new[] { "getnetworkinfo", "getblockchaininfo", "getdeploymentinfo" }));
        Assert.All(logs.Messages, value => Assert.DoesNotContain("private daemon credential", value));
        Assert.All(fixture.Messages.ReceivedCalls().Select(x => x.GetArguments()[0]).OfType<AdminNotification>(),
            value => Assert.DoesNotContain("private daemon credential", value.Message));
    }

    [Fact]
    public async Task AllocationHold_AlertsOnceAcrossHandlerRecreation()
    {
        var grace = new ActiveBlockGracePeriodTracker();
        var fixture = await Fixture(grace: grace);
        fixture.Handler.NotifyAllocationHold(Reward());
        var replacement = await Fixture(grace: grace, messages: fixture.Messages);
        replacement.Handler.NotifyAllocationHold(Reward());
        var alert = Assert.IsType<AdminNotification>(Assert.Single(fixture.Messages.ReceivedCalls()).GetArguments()[0]);
        Assert.Contains("PROP/PPLNS", alert.Message);
        Assert.Contains("audit original shares", alert.Message);
    }

    [Fact]
    public async Task PayoutAttestation_CancellationDoesNotRaiseAvailabilityAlert()
    {
        var fixture = await Fixture();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, cancel.Token));
        Assert.Empty(fixture.Messages.ReceivedCalls());
    }

    [Theory]
    [InlineData("immature")]
    [InlineData("generate")]
    public async Task VerifiedClassification_LogsReviewedWalletCategory(string category)
    {
        var fixture = await Fixture();
        using var logs = new Miningcore.Tests.Rpc.RpcDiagnosticTests.CapturedLogs();
        fixture.Handler.SetLogger(logs.Logger);
        fixture.Handler.Wallet["details"][0]["category"] = category;
        if(category == "immature") fixture.Handler.Wallet["amount"] = 0;
        await fixture.Handler.ClassifyBlocksAsync(fixture.Pool, new[] { Reward() }, CancellationToken.None);
        Assert.Contains(logs.Messages, value => value.Contains("wallet category " + category, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"invalid\"")]
    public void SharedLegacyRpcTypes_IgnoreUnrelatedExpectedWorkExtensions(string value)
    {
        var json = "{\"difficulty\":123,\"difficulty_blake2b\":" + value + "}";
        Assert.Equal(123, JsonConvert.DeserializeObject<Miningcore.Blockchain.Bitcoin.DaemonResponses.Block>(json).Difficulty);
        Assert.Equal(123, JsonConvert.DeserializeObject<BlockchainInfo>(json).Difficulty);
        Assert.Equal(123, JsonConvert.DeserializeObject<MiningInfo>(json).Difficulty);
    }
}
