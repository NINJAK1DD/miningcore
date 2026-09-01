using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobManagerBaseTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EquihashConfigure_PreservesLegacyOverrideWithoutTemplateCast(
        bool? configuredOverride, bool expected)
    {
        using var container = BuildContainer();
        var manager = new TestEquihashJobManager(container,
            MockMasterClock.FromTicks(638010200200475015),
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var extra = configuredOverride.HasValue
            ? new Dictionary<string, object>
            {
                ["hasLegacyDaemon"] = configuredOverride.Value,
            }
            : null;
        var pool = new PoolConfig
        {
            Id = "equihash-test",
            Template = new EquihashCoinTemplate(),
            Daemons = new[]
            {
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = 8232,
                },
            },
            Extra = extra,
        };

        manager.Configure(pool, new ClusterConfig());

        Assert.Equal(expected, manager.LegacyDaemonEnabled);
    }

    [Fact]
    public void LegacyConnectionFailure_DoesNotDereferenceMissingResponse()
    {
        var response = new RpcResponse<DaemonInfo>(null,
            new JsonRpcError(-1, "getinfo unavailable", null));

        var connected = BitcoinJobManagerBase<BitcoinJob>
            .TryGetLegacyDaemonConnection(response, out var version);

        Assert.False(connected);
        Assert.Null(version);
    }

    [Fact]
    public void LegacyConnectionSuccess_ReturnsVersion()
    {
        var response = new RpcResponse<DaemonInfo>(new DaemonInfo
        {
            Connections = 1,
            Version = "v1.2.3",
        });

        var connected = BitcoinJobManagerBase<BitcoinJob>
            .TryGetLegacyDaemonConnection(response, out var version);

        Assert.True(connected);
        Assert.Equal("v1.2.3", version);
    }

    [Fact]
    public async Task AcceptedCandidate_PersistsWhenPpsEvidenceConstructionFails()
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "btc-pps",
            Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.PPS,
            },
        };
        var accepted = new Share
        {
            PoolId = pool.Id,
            Miner = "miner",
            Difficulty = double.MaxValue,
            NetworkDifficulty = 1,
            RewardBasisSatoshis = 625_000_000,
            IsBlockCandidate = true,
            BlockHash = new string('a', 64),
            TransactionConfirmationData = "coinbase",
            Created = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.AttachEvidence(pool, accepted));

        Assert.True(manager.Persisted);
        Assert.True(accepted.BlockRecordEmitted);
        Assert.True(manager.PersistedCandidate.IsBlockCandidate);
        Assert.True(manager.PersistedCandidate.BlockOnly);
        Assert.Equal("bitcoin-direct", manager.PersistedCandidate.BlockType);
        Assert.Null(manager.PersistedCandidate.AccountingId);
        Assert.Null(manager.PersistedCandidate.PpsCalculatedAmount);
        Assert.Equal(accepted.BlockHash, manager.PersistedCandidate.BlockHash);
    }

    [Fact]
    public async Task AcceptedCandidate_PersistsBeforeSuccessfulPpsEvidence()
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "btc-pps",
            Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.PPS,
            },
        };
        var accepted = new Share
        {
            PoolId = pool.Id,
            Miner = "miner",
            Difficulty = 1,
            NetworkDifficulty = 100,
            RewardBasisSatoshis = 625_000_000,
            IsBlockCandidate = true,
            BlockHash = new string('b', 64),
            TransactionConfirmationData = "coinbase",
            Created = DateTime.UtcNow,
        };

        await manager.AttachEvidence(pool, accepted);

        Assert.True(manager.Persisted);
        Assert.True(accepted.BlockRecordEmitted);
        Assert.Equal(0.0625m, accepted.PpsCalculatedAmount);
        Assert.True(manager.PersistedCandidate.BlockOnly);
        Assert.Equal("bitcoin-direct", manager.PersistedCandidate.BlockType);
        Assert.Null(manager.PersistedCandidate.AccountingId);
        Assert.Null(manager.PersistedCandidate.PpsCalculatedAmount);

        // A downstream accounting rejection cannot erase the independent candidate copy.
        accepted.PpsCalculatedAmount += 0.000000000000000000000001m;
        Assert.Throws<InvalidDataException>(() =>
            Miningcore.Mining.ShareAccounting.CreatePpsCredit(pool, accepted));
        Assert.Equal(accepted.BlockHash, manager.PersistedCandidate.BlockHash);
    }

    [Fact]
    public async Task DirectCandidate_PersistsBeforeTransportFailureAndRemainsReconcilable()
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>())
        {
            DirectSubmissionException = new IOException(
                "response lost after daemon accepted request"),
        };
        manager.Configure(new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "BTC",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
        }, new ClusterConfig());
        var candidate = new Share
        {
            PoolId = "btc-direct",
            Miner = "miner",
            Difficulty = 1,
            NetworkDifficulty = 100,
            IsBlockCandidate = true,
            BlockHeight = 101,
            BlockHash = new string('c', 64),
            TransactionConfirmationData = new string('d', 64),
            SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
            GrossRewardSatoshis = 5_000_000_000,
            DirectMinerRewardSatoshis = 4_900_000_000,
            DirectMinerScriptPubKey = "0014" + new string('1', 40),
            DirectRecipientOutputs = "[]",
            Created = DateTime.UtcNow,
        };

        var result = await manager.PersistAndSubmitDirect(candidate,
            "block-hex", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.True(result.Ambiguous);
        Assert.True(manager.Persisted);
        Assert.True(manager.SubmissionStartedAfterPersistence);
        Assert.True(candidate.BlockRecordEmitted);
        Assert.Equal(candidate.BlockHash,
            manager.PersistedCandidate.BlockHash);
        Assert.Equal(candidate.TransactionConfirmationData,
            manager.PersistedCandidate.TransactionConfirmationData);
        Assert.Equal(BitcoinDirectCoinbaseSettlement.BlockType,
            manager.PersistedCandidate.BlockType);
        Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
            manager.PersistedCandidate.SettlementMode);
    }

    [Fact]
    public async Task DirectTemplateContractFailure_EscapesJobUpdate()
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "BTC",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = new Dictionary<string, object>
            {
                ["soloCoinbasePayout"] = true,
            },
        };
        manager.Configure(pool, new ClusterConfig());
        manager.Enqueue(new BlockTemplate
        {
            Height = 1,
            CoinbaseValue = 0,
        });

        await Assert.ThrowsAsync<PoolStartupException>(() =>
            manager.Update(CancellationToken.None));
    }

    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());

        return builder.Build();
    }

    private sealed class TestEquihashJobManager : EquihashJobManager
    {
        public TestEquihashJobManager(IComponentContext ctx,
            IMasterClock clock, IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider) :
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }

        public bool LegacyDaemonEnabled => hasLegacyDaemon;
    }

    private sealed class TestBitcoinJobManager : BitcoinJobManager
    {
        public TestBitcoinJobManager(IComponentContext ctx, IMasterClock clock,
            IMessageBus messageBus, IExtraNonceProvider extraNonceProvider) :
            base(ctx, clock, messageBus, extraNonceProvider)
        {
        }

        public bool Persisted { get; private set; }
        public Share PersistedCandidate { get; private set; }
        public Exception DirectSubmissionException { get; init; }
        public bool SubmissionStartedAfterPersistence { get; private set; }

        public Task AttachEvidence(PoolConfig pool, Share share) =>
            AttachPpsEvidencePreservingAcceptedCandidateAsync(pool, share);

        private readonly Queue<RpcResponse<BlockTemplate>> responses = new();

        public void Enqueue(BlockTemplate template) => responses.Enqueue(
            new RpcResponse<BlockTemplate>(template));

        public Task<(bool IsNew, bool Force)> Update(CancellationToken ct) =>
            UpdateJob(ct, false);

        public async Task<(bool Accepted, bool Ambiguous)>
            PersistAndSubmitDirect(Share share, string blockHex,
                CancellationToken ct)
        {
            var result = await PersistAndSubmitDirectCandidateAsync(share,
                blockHex, ct);
            return (result.Accepted, result.Ambiguous);
        }

        protected override Task<RpcResponse<BlockTemplate>>
            GetBlockTemplateAsync(CancellationToken ct) =>
            Task.FromResult(responses.Dequeue());

        protected override Task PersistCandidateWithoutAccountingAsync(
            Share share)
        {
            Persisted = true;
            PersistedCandidate = CreateCandidateWithoutAccounting(share);
            return Task.CompletedTask;
        }

        protected override Task<SubmitResult> SubmitDirectBlockAsync(
            Share share, string blockHex, CancellationToken ct)
        {
            SubmissionStartedAfterPersistence = Persisted;

            return DirectSubmissionException != null
                ? Task.FromException<SubmitResult>(DirectSubmissionException)
                : Task.FromResult(new SubmitResult(true,
                    share.TransactionConfirmationData));
        }
    }
}
