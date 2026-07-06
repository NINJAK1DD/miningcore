using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;
using PersistedBlock = Miningcore.Persistence.Model.Block;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinPayoutHandlerTests : TestBase
{
    [Fact]
    public async Task Reconciliation_UnavailableBlock_RemainsPendingWithMarker()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not available", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-block:doge-block", block.TransactionConfirmationData);
        Assert.Equal(1, fixture.Handler.BlockCalls);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
    }

    [Fact]
    public async Task Reconciliation_ResolvedBlock_IsPersistedPendingBeforeTransactionClassification()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_ResolvesOnlyForMatchingParentProof()
    {
        var fixture = await CreateFixtureAsync();
        var losingClaim = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-a"), 1, "MinerA");
        var winningClaim = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-b"), 2, "MinerB");
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
            AuxPow = new AuxPow { ParentBlock = "parent-b" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { losingClaim, winningClaim }, CancellationToken.None);

        Assert.Equal(2, result.Length);
        Assert.Equal(BlockStatus.Orphaned, losingClaim.Status);
        Assert.Equal(0, losingClaim.Reward);
        Assert.Equal(BlockStatus.Pending, winningClaim.Status);
        Assert.Equal("auxpow", winningClaim.Type);
        Assert.Equal("coinbase-txid", winningClaim.TransactionConfirmationData);
        Assert.True(winningClaim.NotifyBlockFoundOnUpdate);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockFoundNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_OrphansMatchingProofOnOrphanedChild()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header"));
        block.Type = "auxpow-claim";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = -1,
            Transactions = new[] { "coinbase-txid" },
            AuxPow = new AuxPow { ParentBlock = "parent-header" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.Equal("auxpow-claim", block.Type);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockFoundNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_AcceptedAuxPowMarker_OrphansKnownInactiveChild()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = -1,
            Transactions = new[] { "coinbase-txid" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_IsOrphanedWhenFinalRecordAlreadyExists()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header"));
        fixture.Handler.FinalizedAuxPowBlock = PendingBlock("coinbase-txid", 99);
        fixture.Handler.FinalizedAuxPowBlock.Type = "auxpow";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
            AuxPow = new AuxPow { ParentBlock = "parent-header" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
    }

    [Fact]
    public async Task Reconciliation_AmbiguousParentSubmission_ResolvesOnLaterCycle()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateParentUncertain("ltc-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "ltc-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("merged-parent", block.Type);
        Assert.True(block.NotifyBlockFoundOnUpdate);
        Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_MissingParentProofPersistsRetryCount()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header"));
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-claim:doge-block:parent-header:1",
            block.TransactionConfirmationData);
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_MissingParentProofExpiresAfterRepeatedObservation()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header", 2));
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
    }

    [Fact]
    public async Task Reconciliation_MismatchedReturnedHash_RemainsPending()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "different-block",
            Confirmations = 1,
            Transactions = new[] { "wrong-coinbase" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal("auxpow-block:doge-block", block.TransactionConfirmationData);
        Assert.Equal(BlockStatus.Pending, block.Status);
    }

    [Fact]
    public async Task Reconciliation_UncertainSubmissionExpiresAfterRepeatedDefinitiveAbsence()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header", 2));
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not found", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
    }

    [Fact]
    public async Task Reconciliation_UncertainSubmissionPersistsDefinitiveMissCount()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header"));
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not found", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-claim:doge-block:parent-header:1",
            block.TransactionConfirmationData);
    }

    [Fact]
    public async Task Classification_SubsequentImmatureResponse_UpdatesRewardAndProgress()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        fixture.Handler.TransactionResponse = SuccessTransaction("immature", 50m, 10);

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal(50m, block.Reward);
        Assert.InRange(block.ConfirmationProgress, double.Epsilon, 0.999999d);
    }

    [Fact]
    public async Task Classification_SubsequentGenerateResponse_ConfirmsAndCreditsSoloMiner()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        fixture.Handler.TransactionResponse = SuccessTransaction("generate", 75m, 1000);

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        var confirmed = Assert.Single(result);
        Assert.Equal(BlockStatus.Confirmed, confirmed.Status);
        Assert.Equal(1, confirmed.ConfirmationProgress);
        Assert.Equal(75m, confirmed.Reward);

        var scheme = new SOLOPaymentScheme(fixture.ShareRepository, fixture.BalanceRepository);
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        await scheme.UpdateBalancesAsync(connection, transaction, fixture.Pool,
            fixture.Handler, confirmed, confirmed.Reward, CancellationToken.None);

        await fixture.BalanceRepository.Received(1).AddAmountAsync(connection, transaction,
            fixture.Config.Id, confirmed.Miner, confirmed.Reward,
            $"Reward for block {confirmed.BlockHeight}");
    }

    [Fact]
    public async Task Classification_OrdinaryBlockWithMissingTransaction_IsOrphaned()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("ordinary-coinbase-txid");
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null, new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
    }

    [Fact]
    public async Task Classification_AuxPowBlockWithWalletIndexLag_RemainsPendingWhenBlockExists()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = "auxpow";
        block.Hash = "doge-block";
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    [Fact]
    public async Task Classification_AuxPowBlockWithWalletIndexLag_IsOrphanedWhenBlockIsNotActive()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = "auxpow";
        block.Hash = "doge-block";
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = -1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    private async Task<HandlerFixture> CreateFixtureAsync()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var mapper = container.Resolve<IMapper>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var paymentRepository = Substitute.For<IPaymentRepository>();
        var clock = Substitute.For<IMasterClock>();
        var now = DateTime.UtcNow;
        clock.Now.Returns(now);
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestBitcoinPayoutHandler(container, connectionFactory, mapper,
            shareRepository, blockRepository, balanceRepository, paymentRepository, clock, messageBus);
        var config = new PoolConfig
        {
            Id = "doge-test",
            Template = ModuleInitializer.CoinTemplates["dogecoin"],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 22555 } },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
            RewardRecipients = Array.Empty<RewardRecipient>(),
        };
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);

        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);

        return new HandlerFixture(handler, pool, config, shareRepository, balanceRepository,
            messageBus, now);
    }

    private static PersistedBlock PendingBlock(string confirmationData, long id = 1,
        string miner = "DTestMiner")
    {
        return new PersistedBlock
        {
            Id = id,
            PoolId = "doge-test",
            BlockHeight = 100,
            Miner = miner,
            Hash = "doge-block",
            Status = BlockStatus.Pending,
            TransactionConfirmationData = confirmationData,
            Created = DateTime.UtcNow,
        };
    }

    private static RpcResponse<JToken>[] SuccessTransaction(string category, decimal amount,
        int confirmations)
    {
        return new[]
        {
            new RpcResponse<JToken>(JToken.FromObject(new Transaction
            {
                Amount = amount,
                Confirmations = confirmations,
                Details = new[] { new TransactionDetails { Category = category } },
            })),
        };
    }

    private sealed record HandlerFixture(TestBitcoinPayoutHandler Handler, IMiningPool Pool,
        PoolConfig Config, IShareRepository ShareRepository, IBalanceRepository BalanceRepository,
        IMessageBus MessageBus, DateTime Now);

    private sealed class TestBitcoinPayoutHandler : BitcoinPayoutHandler
    {
        public TestBitcoinPayoutHandler(IComponentContext ctx, IConnectionFactory cf, IMapper mapper,
            IShareRepository shareRepo, IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock, IMessageBus messageBus) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
        {
        }

        public RpcResponse<DaemonBlock> BlockResponse { get; set; }
        public RpcResponse<JToken>[] TransactionResponse { get; set; }
        public PersistedBlock FinalizedAuxPowBlock { get; set; }
        public int BlockCalls { get; private set; }
        public int TransactionCalls { get; private set; }

        protected override Task<RpcResponse<DaemonBlock>> GetBlockAsync(string blockHash,
            CancellationToken ct)
        {
            BlockCalls++;
            return Task.FromResult(BlockResponse);
        }

        protected override Task<RpcResponse<JToken>[]> GetTransactionsAsync(PersistedBlock[] blocks,
            CancellationToken ct)
        {
            TransactionCalls++;
            return Task.FromResult(TransactionResponse);
        }

        protected override Task<PersistedBlock> GetFinalizedAuxPowBlockAsync(string poolId,
            string blockHash, CancellationToken ct)
        {
            return Task.FromResult(FinalizedAuxPowBlock);
        }
    }
}
