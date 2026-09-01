using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NLog;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinDirectSettlementTests : TestBase
{
    [Fact]
    public void DecodedBlock_MustMatchPersistedExactOutputs()
    {
        var block = CreateBlock();
        var response = CreateResponse(block, 49m, 1m);

        BitcoinPayoutHandler.VerifyDirectCoinbaseTransaction(block,
            response);

        response["tx"][0]["vout"][1]["value"] = 48.99999999m;
        Assert.Throws<InvalidDataException>(() =>
            BitcoinPayoutHandler.VerifyDirectCoinbaseTransaction(block,
                response));
    }

    [Fact]
    public void PersistedEvidence_RejectsPartialOrDuplicateOutputs()
    {
        var block = CreateBlock();
        block.DirectMinerScriptPubKey = null;
        Assert.Throws<InvalidDataException>(() =>
            BitcoinPayoutHandler.ValidatePersistedDirectSettlement(block));

        block = CreateBlock();
        block.DirectRecipientOutputs = JsonConvert.SerializeObject(new[]
        {
            new BitcoinDirectCoinbaseOutput
            {
                Address = "fee",
                ScriptPubKey = block.DirectMinerScriptPubKey,
                AmountSatoshis = 100_000_000,
            },
        });
        Assert.Throws<InvalidDataException>(() =>
            BitcoinPayoutHandler.VerifyDirectCoinbaseTransaction(block,
                CreateResponse(block, 49m, 1m)));
    }

    [Fact]
    public void PersistedMarker_OwnsSettlementAcrossConfigurationChanges()
    {
        var direct = CreateBlock();
        var historicalCustodial = new Block
        {
            Type = "bitcoin-direct",
            TransactionConfirmationData = new string('c', 64),
        };

        Assert.True(BitcoinPayoutHandler.IsDirectCoinbaseSettlement(direct));
        Assert.False(BitcoinPayoutHandler.IsDirectCoinbaseSettlement(
            historicalCustodial));
    }

    [Fact]
    public void AcceptedCandidate_UsesDedicatedDirectCoinbaseIdentity()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        var share = new global::Miningcore.Blockchain.Share
        {
            PoolId = "btc-direct",
            IsBlockCandidate = true,
            BlockHash = submission.BlockHash,
            TransactionConfirmationData = submission.CoinbaseTxId,
            SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
            GrossRewardSatoshis = 5_000_000_000,
            DirectMinerRewardSatoshis = 4_900_000_000,
            DirectMinerScriptPubKey = "0014" + new string('1', 40),
            DirectRecipientOutputs = "[]",
        };

        var candidate = BitcoinJobManager
            .CreateCandidateWithoutAccounting(share,
                submission.BlockHex);

        Assert.Equal(BitcoinDirectCoinbaseSettlement.BlockType,
            candidate.BlockType);
        Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
            candidate.SettlementMode);
        Assert.Equal(BitcoinDirectSubmission.Prepared,
            candidate.DirectSubmissionState);
    }

    [Fact]
    public async Task HistoricalPpsCandidateType_UsesLegacyWalletClassification()
    {
        var ppsCandidate = new Block
        {
            BlockHeight = 100,
            Type = "bitcoin-direct",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = "coinbase-txid",
        };
        var legacyResponse = JObject.FromObject(new
        {
            amount = 6.25m,
            confirmations = 101,
            details = new[] { new { category = "generate" } },
        });
        var handler = new DirectResponsePayoutHandler(container,
            CreateResponse(CreateBlock(), 49m, 1m), legacyResponse:
            legacyResponse);
        await handler.ConfigureAsync(new ClusterConfig(), new PoolConfig
        {
            Id = "btc-pps",
            Template = new BitcoinTemplate { Symbol = "BTC" },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        }, CancellationToken.None);

        var result = await handler.ClassifyBlocksAsync(
            Substitute.For<IMiningPool>(), new[] { ppsCandidate },
            CancellationToken.None);

        Assert.Same(ppsCandidate, Assert.Single(result));
        Assert.Equal(BlockStatus.Confirmed, ppsCandidate.Status);
        Assert.Equal(6.25m, ppsCandidate.Reward);
    }

    [Fact]
    public async Task PreparedSubmission_DaemonMiss_RemainsPendingAndSilent()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        var block = CreateBlock();
        block.Hash = submission.BlockHash;
        block.TransactionConfirmationData = submission.CoinbaseTxId;
        block.Status = BlockStatus.Pending;
        block.DirectSubmissionState = BitcoinDirectSubmission.Prepared;
        block.DirectSubmissionBlock = submission.BlockHex;
        block.DirectSubmissionAttempts = 0;
        block.DirectSubmissionDefinitiveMisses = 0;
        var handler = new DirectResponsePayoutHandler(container, null,
            directRpcResponse: new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Block not found", null)),
            submitResponse: new RpcResponse<JToken>(null,
                new JsonRpcError(-500, "response unavailable", null)));

        Assert.True(await handler.ClassifyDirectCoinbaseBlockAsync(block,
            CancellationToken.None));

        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal(BitcoinDirectSubmission.SubmittedUncertain,
            block.DirectSubmissionState);
        Assert.Equal(1, block.DirectSubmissionAttempts);
        Assert.Equal(0, block.DirectSubmissionDefinitiveMisses);
        Assert.False(block.NotifyBlockFoundOnUpdate);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task NegativeConfirmation_OrphansWithoutWalletLookup()
    {
        var block = CreateBlock();
        var response = CreateResponse(block, 49m, 1m);
        response["confirmations"] = -1;
        var handler = new DirectResponsePayoutHandler(container,
            response);

        var classified = await handler.ClassifyDirectCoinbaseBlockAsync(
            block, CancellationToken.None);

        Assert.True(classified);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task ConfirmedReconciliation_RefreshesAuditWithoutDuplicateNotifications()
    {
        var block = CreateBlock();
        block.Status = BlockStatus.Confirmed;
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(now);
        var handler = new DirectResponsePayoutHandler(container,
            CreateResponse(block, 49m, 1m), clock);
        await handler.ConfigureAsync(new ClusterConfig(), new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Symbol = "BTC",
                CoinbaseMinConfimations = 1,
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        }, CancellationToken.None);

        var classified = await handler.ClassifyDirectCoinbaseBlockAsync(
            block, CancellationToken.None);

        Assert.True(classified);
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        Assert.Equal(now, block.DirectSettlementLastChecked);
        Assert.False(block.NotifyBlockFoundOnUpdate);
        Assert.False(block.NotifyBlockConfirmationProgressOnUpdate);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task ConfirmedReconciliation_NegativeConfirmationBecomesOrphaned()
    {
        var block = CreateBlock();
        block.Status = BlockStatus.Confirmed;
        var response = CreateResponse(block, 49m, 1m);
        response["confirmations"] = -1;
        var now = DateTime.UtcNow;
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(now);
        var handler = new DirectResponsePayoutHandler(container, response,
            clock);

        Assert.True(await handler.ClassifyDirectCoinbaseBlockAsync(block,
            CancellationToken.None));

        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(now, block.DirectSettlementLastChecked);
        Assert.Equal(0, block.Reward);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task OrphanedReconciliation_ReactivatesThenConfirms()
    {
        var block = CreateBlock();
        block.Status = BlockStatus.Orphaned;
        var response = CreateResponse(block, 49m, 1m);
        var handler = new DirectResponsePayoutHandler(container, response);
        await handler.ConfigureAsync(new ClusterConfig(), new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Symbol = "BTC",
                CoinbaseMinConfimations = 2,
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        }, CancellationToken.None);

        Assert.True(await handler.ClassifyDirectCoinbaseBlockAsync(block,
            CancellationToken.None));
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.True(block.NotifyBlockConfirmationProgressOnUpdate);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);

        response["confirmations"] = 2;
        Assert.True(await handler.ClassifyDirectCoinbaseBlockAsync(block,
            CancellationToken.None));
        Assert.Equal(BlockStatus.Confirmed, block.Status);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task MalformedDirectEvidence_IsQuarantinedWithoutBlockingPeer()
    {
        var valid = CreateBlock();
        valid.Id = 2;
        var malformed = CreateBlock();
        malformed.Id = 1;
        malformed.DirectRecipientOutputs = "[1]";
        var handler = new DirectResponsePayoutHandler(container,
            CreateResponse(valid, 49m, 1m));
        await handler.ConfigureAsync(new ClusterConfig(), new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Symbol = "BTC",
                CoinbaseMinConfimations = 1,
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
        }, CancellationToken.None);

        var result = await handler.ClassifyBlocksAsync(
            Substitute.For<IMiningPool>(), new[] { malformed, valid },
            CancellationToken.None);

        Assert.Equal(2, result.Length);
        Assert.Equal(BlockStatus.Quarantined, malformed.Status);
        Assert.NotNull(malformed.DirectSettlementLastChecked);
        Assert.Equal(BlockStatus.Confirmed, valid.Status);
    }

    private static Block CreateBlock() => new()
    {
        BlockHeight = 101,
        Hash = new string('a', 64),
        TransactionConfirmationData = new string('b', 64),
        Type = BitcoinDirectCoinbaseSettlement.BlockType,
        SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
        GrossRewardSatoshis = 5_000_000_000,
        DirectMinerRewardSatoshis = 4_900_000_000,
        DirectMinerScriptPubKey = "0014" + new string('1', 40),
        DirectRecipientOutputs = JsonConvert.SerializeObject(new[]
        {
            new BitcoinDirectCoinbaseOutput
            {
                Address = "fee",
                ScriptPubKey = "0014" + new string('2', 40),
                AmountSatoshis = 100_000_000,
            },
        }),
        DirectSubmissionState = BitcoinDirectSubmission.LegacyObserved,
        DirectSubmissionAttempts = 0,
        DirectSubmissionDefinitiveMisses = 0,
    };

    private static JObject CreateResponse(Block block, decimal miner,
        decimal fee) => JObject.Parse($$"""
        {
          "hash": "{{block.Hash}}",
          "confirmations": 1,
          "tx": [
            {
              "txid": "{{block.TransactionConfirmationData}}",
              "vout": [
                { "value": 0, "scriptPubKey": { "hex": "6a24aa21a9ed{{new string('0', 64)}}" } },
                { "value": {{miner}}, "scriptPubKey": { "hex": "{{block.DirectMinerScriptPubKey}}" } },
                { "value": {{fee}}, "scriptPubKey": { "hex": "0014{{new string('2', 40)}}" } }
              ]
            }
          ]
        }
        """);

    private sealed class DirectResponsePayoutHandler : BitcoinPayoutHandler
    {
        private readonly JToken response;
        private readonly JToken legacyResponse;
        private readonly RpcResponse<JToken> directRpcResponse;
        private readonly RpcResponse<JToken> submitResponse;

        public DirectResponsePayoutHandler(IComponentContext context,
            JToken response, IMasterClock clock = null,
            JToken legacyResponse = null,
            RpcResponse<JToken> directRpcResponse = null,
            RpcResponse<JToken> submitResponse = null) : base(context,
            Substitute.For<IConnectionFactory>(),
            context.Resolve<AutoMapper.IMapper>(),
            Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(),
            Substitute.For<IBalanceRepository>(),
            Substitute.For<IPaymentRepository>(),
            clock ?? Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(),
            new ActiveBlockGracePeriodTracker())
        {
            this.response = response;
            this.legacyResponse = legacyResponse;
            this.directRpcResponse = directRpcResponse;
            this.submitResponse = submitResponse;
            logger = LogManager.GetCurrentClassLogger();
        }

        protected override Task<RpcResponse<JToken>>
            GetDirectSettlementBlockAsync(string blockHash,
            CancellationToken ct) =>
            Task.FromResult(directRpcResponse ??
                new RpcResponse<JToken>(response));

        protected override Task<RpcResponse<JToken>>
            SubmitDirectSettlementBlockAsync(string blockHex,
                CancellationToken ct) => Task.FromResult(submitResponse ??
                new RpcResponse<JToken>((JToken) null));

        protected override Task<RpcResponse<JToken>[]>
            GetTransactionsAsync(Block[] blocks, CancellationToken ct) =>
            legacyResponse == null
                ? base.GetTransactionsAsync(blocks, ct)
                : Task.FromResult(blocks.Select(_ =>
                    new RpcResponse<JToken>(legacyResponse)).ToArray());
    }
}
