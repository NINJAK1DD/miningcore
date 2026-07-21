using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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
using Miningcore.Payments;
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
    [Theory]
    [InlineData(-500, true)]
    [InlineData(-5, false)]
    [InlineData(-1, false)]
    public void WalletSubmission_OnlyTransportErrorsAreUnknown(int errorCode,
        bool expected)
    {
        Assert.Equal(expected, BitcoinPayoutHandler.IsUnknownWalletSubmission(
            new JsonRpcError(errorCode, "test", null)));
    }

    [Fact]
    public async Task Payout_TransportErrorAfterSendMany_IsUnknown()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError(-500, "response lost", null));

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Payout_ExplicitWalletRejection_IsConclusive()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError(-5, "rejected", null));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Payout_SuccessWithoutTransactionId_IsUnknown()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null);

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Payout_TransactionInMempool_PersistsAndSucceeds()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        Assert.Equal(0, fixture.Handler.WalletTransactionCalls);
    }

    [Fact]
    public async Task Payout_ConfirmationBetweenMempoolAndWalletQueries_PersistsAndSucceeds()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(null,
            new JsonRpcError(-5, "Transaction not in mempool", null));
        fixture.Handler.WalletTransactionResponse = new RpcResponse<Transaction>(new Transaction
        {
            TxId = "payout-txid",
            Confirmations = 1,
        });

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        Assert.Equal(1, fixture.Handler.WalletTransactionCalls);
    }

    [Fact]
    public async Task Payout_TransientMempoolError_RetriesAndSucceeds()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponseSequence.Enqueue(new RpcResponse<JToken>(null,
            new JsonRpcError(-500, "RPC timeout", null)));
        fixture.Handler.MempoolResponseSequence.Enqueue(
            new RpcResponse<JToken>(new JObject()));
        fixture.Handler.WalletTransactionResponseSequence.Enqueue(
            new RpcResponse<Transaction>(new Transaction
            {
                TxId = "payout-txid",
                Confirmations = 0,
            }));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        Assert.Equal(2, fixture.Handler.MempoolTransactionIds.Count);
        Assert.Equal(1, fixture.Handler.WalletTransactionCalls);
        Assert.Equal(1, fixture.Handler.VerificationDelayCalls);
    }

    [Fact]
    public async Task Payout_WalletOnlyTransaction_PersistsThenFailsStop()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(null,
            new JsonRpcError(-5, "Transaction not in mempool", null));
        fixture.Handler.WalletTransactionResponse = new RpcResponse<Transaction>(new Transaction
        {
            TxId = "payout-txid",
            Confirmations = 0,
        });

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));

        Assert.Contains("remained absent from the local mempool", exception.Message);
        Assert.Contains("after 3 verification attempts", exception.Message);
        Assert.Contains("persisted to prevent a duplicate payout", exception.Message);
        Assert.Equal(3, fixture.Handler.MempoolTransactionIds.Count);
        Assert.Equal(3, fixture.Handler.WalletTransactionCalls);
        Assert.Equal(2, fixture.Handler.VerificationDelayCalls);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
    }

    [Fact]
    public async Task Payout_CancellationDuringMempoolLookup_IsUncertain()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.ThrowCancellationFromMempoolLookup = true;

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));

        Assert.Contains("payout-txid", exception.Message);
        Assert.Contains("interrupted", exception.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
    }

    [Fact]
    public async Task Payout_CancellationDuringVerificationDelay_IsUncertain()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(null,
            new JsonRpcError(-5, "Transaction not in mempool", null));
        fixture.Handler.WalletTransactionResponse = new RpcResponse<Transaction>(new Transaction
        {
            TxId = "payout-txid",
            Confirmations = 0,
        });
        fixture.Handler.ThrowCancellationFromVerificationDelay = true;

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));

        Assert.Contains("payout-txid", exception.Message);
        Assert.Contains("interrupted", exception.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal(1, fixture.Handler.VerificationDelayCalls);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
    }

    [Fact]
    public async Task Payout_SendManySuccessNotificationFailure_DoesNotPropagate()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        fixture.MessageBus.When(x => x.SendMessage(
                Arg.Is<PaymentNotification>(notification => notification.Error == null),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("success subscriber failed"));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Payout_MempoolEntry_PersistsAndSucceedsRegardlessOfRelayState(
        bool? unbroadcast)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        var mempoolEntry = new JObject();

        if(unbroadcast.HasValue)
            mempoolEntry["unbroadcast"] = unbroadcast.Value;

        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(mempoolEntry);

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        Assert.Equal(0, fixture.Handler.WalletTransactionCalls);
    }

    [Fact]
    public async Task Payout_BrokenSendMany_VerifiesEveryPersistedTransactionBeforeFailStop()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid-1");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>("payout-txid-2");

        foreach(var txId in new[] { "payout-txid-1", "payout-txid-2" })
        {
            fixture.Handler.MempoolResponses[txId] = new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Transaction not in mempool", null));
            fixture.Handler.WalletTransactionResponses[txId] =
                new RpcResponse<Transaction>(new Transaction
                {
                    TxId = txId,
                    Confirmations = 0,
                });
        }

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, CancellationToken.None));

        Assert.Contains("payout-txid-1", exception.Message);
        Assert.Contains("payout-txid-2", exception.Message);
        Assert.Equal(new[]
        {
            (TransactionId: "payout-txid-1", Count: 3),
            (TransactionId: "payout-txid-2", Count: 3),
        }, fixture.Handler.MempoolTransactionIds
            .GroupBy(x => x)
            .OrderBy(x => x.Key)
            .Select(x => (TransactionId: x.Key, Count: x.Count())));

        var events = fixture.Events.ToArray();
        var persistenceCommit = Array.FindIndex(events, x => x == "commit");
        var firstVerification = Array.FindIndex(events, x => x.StartsWith("verify:"));
        Assert.True(persistenceCommit >= 0);
        Assert.True(firstVerification > persistenceCommit);

        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_BroadcastUncertainty_FailureNotificationCannotMaskFailStop()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-5, "rejected", null));
        fixture.Handler.MempoolResponses["payout-txid"] = new RpcResponse<JToken>(null,
            new JsonRpcError(-5, "Transaction not in mempool", null));
        fixture.Handler.WalletTransactionResponses["payout-txid"] =
            new RpcResponse<Transaction>(new Transaction
            {
                TxId = "payout-txid",
                Confirmations = 0,
            });
        fixture.MessageBus.When(x => x.SendMessage(
                Arg.Is<PaymentNotification>(notification => notification.Error != null),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("failure subscriber failed"));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, CancellationToken.None));

        Assert.Contains("payout-txid", exception.Message);
        Assert.DoesNotContain("failure subscriber failed", exception.Message);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error != null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_UnknownSubmission_SuccessNotificationCannotMaskFailStop()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        fixture.Handler.MempoolResponses["payout-txid"] =
            new RpcResponse<JToken>(new JObject());
        fixture.MessageBus.When(x => x.SendMessage(
                Arg.Is<PaymentNotification>(notification => notification.Error == null),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("success subscriber failed"));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, CancellationToken.None));

        Assert.Contains("response lost", exception.Message);
        Assert.DoesNotContain("success subscriber failed", exception.Message);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_LegacyDaemonWithoutMempoolEntry_PreservesCompatibility()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(null,
            new JsonRpcError((int) BitcoinRPCErrorCode.RPC_METHOD_NOT_FOUND,
                "Method not found", null));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        Assert.Equal(0, fixture.Handler.WalletTransactionCalls);
    }

    [Fact]
    public void PoolConfig_DefaultRewardRecipients_IsEmpty()
    {
        Assert.Empty(new PoolConfig().RewardRecipients);
    }

    [Fact]
    public async Task UpdateBlockRewardBalances_MissingRewardRecipients_ReturnsFullReward()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Config.RewardRecipients = null;
        var block = PendingBlock("coinbase-txid");
        block.Reward = 75m;
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();

        var remaining = await fixture.Handler.UpdateBlockRewardBalancesAsync(connection,
            transaction, fixture.Pool, block, CancellationToken.None);

        Assert.Equal(block.Reward, remaining);
        await fixture.BalanceRepository.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_UnavailableBlock_RemainsPendingWithMarker()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not available", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-block:doge-block:1", block.TransactionConfirmationData);
        Assert.Equal(1, fixture.Handler.BlockCalls);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
    }

    [Fact]
    public async Task Reconciliation_AcceptedMarker_RepeatedDefinitiveAbsenceOrphansWithNotification()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block", 2));
        block.Type = "auxpow";
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not available", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
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
    public async Task Reconciliation_ActiveAcceptedMarkerWithoutTransactions_RemainsPending()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        block.Type = "auxpow";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-block:doge-block", block.TransactionConfirmationData);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_ActiveAcceptedMarkerWithoutTransactions_DoesNotExpireAsOrphan()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block", 2));
        block.Type = "auxpow";
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-block:doge-block", block.TransactionConfirmationData);
        Assert.Equal(0, fixture.Handler.TransactionCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_DefinitiveAbsenceAfterActiveResetBecomesFirstMiss()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreatePending("doge-block"));
        block.Type = "auxpow";
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-5, "Block not available", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow-block:doge-block:1", block.TransactionConfirmationData);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
    }

    [Fact]
    public async Task Reconciliation_ActiveParentUncertainWithoutTransactions_ResetsHistoricalMisses()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateParentUncertain("ltc-block", 2));
        block.Hash = "ltc-block";
        block.Type = "merged-parent-uncertain";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "ltc-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("merged-parent-uncertain", block.Type);
        Assert.Equal("parent-uncertain:ltc-block:0", block.TransactionConfirmationData);
        Assert.False(block.NotifyBlockFoundOnUpdate);
    }

    [Fact]
    public async Task Reconciliation_ActiveMatchingClaimWithoutTransactions_FinalizesProofAndKeepsCoinbasePending()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header"));
        block.Type = "auxpow-claim";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            AuxPow = new AuxPow { ParentBlock = "parent-header" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal("auxpow", block.Type);
        Assert.True(block.NotifyBlockFoundOnUpdate);
        Assert.Equal("auxpow-block:doge-block", block.TransactionConfirmationData);
    }

    [Fact]
    public async Task Reconciliation_ActiveMismatchingClaimWithoutTransactions_IsRejectedByProof()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-a"));
        block.Type = "auxpow-claim";
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "doge-block",
            Confirmations = 1,
            AuxPow = new AuxPow { ParentBlock = "parent-b" },
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.Equal("auxpow-claim", block.Type);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
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
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
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
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
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
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
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
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
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
        Assert.Equal(AuxPowBlockConfirmation.CreateClaim("doge-block", "parent-header", 1,
            AuxPowBlockConfirmation.ClaimMissKind.MissingProof), block.TransactionConfirmationData);
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_HistoricalAbsenceMissesDoNotExpireMissingProof()
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
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.Equal(AuxPowBlockConfirmation.CreateClaim("doge-block", "parent-header", 1,
            AuxPowBlockConfirmation.ClaimMissKind.MissingProof), block.TransactionConfirmationData);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Reconciliation_AuxPowClaim_ConsecutiveMissingProofExpiresAfterRepeatedObservation()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock(AuxPowBlockConfirmation.CreateClaim(
            "doge-block", "parent-header", 2,
            AuxPowBlockConfirmation.ClaimMissKind.MissingProof));
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
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<BlockUnlockedNotification>(),
            Arg.Any<string>());
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
        Assert.True(block.NotifyBlockConfirmationProgressOnUpdate);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<BlockConfirmationProgressNotification>(), Arg.Any<string>());
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
        Assert.True(confirmed.NotifyBlockUnlockedOnUpdate);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<BlockUnlockedNotification>(), Arg.Any<string>());

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

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_BlockWithWalletIndexLag_RemainsPendingWhenActivityLookupUnavailable(
        string blockType, string blockHash)
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-500, "RPC timeout", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(1, fixture.Handler.BlockCalls);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_OldBlockFirstUnavailableActivityLookup_DoesNotNotifyImmediately(
        string blockType, string blockHash)
    {
        var fixture = await CreateFixtureAsync(now: DateTime.UtcNow);
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;
        block.Created = fixture.Now - TimeSpan.FromMinutes(31);
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-500, "RPC timeout", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        fixture.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_ContinuousUnavailableActivityLookup_NotifiesOnceAcrossHandlers(
        string blockType, string blockHash)
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;

        var first = await CreateFixtureAsync(tracker, start);
        ConfigureUnavailableWalletGrace(first, block);
        await first.Handler.ClassifyBlocksAsync(first.Pool,
            new[] { block }, CancellationToken.None);
        first.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(),
            Arg.Any<string>());

        var second = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(31));
        ConfigureUnavailableWalletGrace(second, block);
        await second.Handler.ClassifyBlocksAsync(second.Pool,
            new[] { block }, CancellationToken.None);
        Assert.Equal(BlockStatus.Pending, block.Status);
        second.MessageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject.Contains("reconciliation delayed") &&
                x.Message.Contains(blockHash)),
            Arg.Any<string>());

        var third = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(62));
        ConfigureUnavailableWalletGrace(third, block);
        await third.Handler.ClassifyBlocksAsync(third.Pool,
            new[] { block }, CancellationToken.None);
        third.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_RecoveredActivityLookup_ClearsUnavailableEpisode(
        string blockType, string blockHash)
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;

        var first = await CreateFixtureAsync(tracker, start);
        ConfigureUnavailableWalletGrace(first, block);
        await first.Handler.ClassifyBlocksAsync(first.Pool,
            new[] { block }, CancellationToken.None);

        var recovered = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(10));
        recovered.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        recovered.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = blockHash,
            Confirmations = 1,
        });
        await recovered.Handler.ClassifyBlocksAsync(recovered.Pool,
            new[] { block }, CancellationToken.None);

        var secondEpisode = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(11));
        ConfigureUnavailableWalletGrace(secondEpisode, block);
        await secondEpisode.Handler.ClassifyBlocksAsync(secondEpisode.Pool,
            new[] { block }, CancellationToken.None);
        secondEpisode.MessageBus.DidNotReceive().SendMessage(Arg.Any<AdminNotification>(),
            Arg.Any<string>());

        var delayed = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(42));
        ConfigureUnavailableWalletGrace(delayed, block);
        await delayed.Handler.ClassifyBlocksAsync(delayed.Pool,
            new[] { block }, CancellationToken.None);
        delayed.MessageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x => x.Message.Contains(blockHash)),
            Arg.Any<string>());
    }

    [Fact]
    public void ActiveBlockGracePeriodTracker_ConcurrentObservers_EmitOnlyOneAlert()
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        Assert.False(tracker.TryAcquireNotification("pool", 1, "block", "auxpow",
            start, TimeSpan.FromMinutes(30)));

        var alerts = 0;
        Parallel.For(0, 32, _ =>
        {
            if(tracker.TryAcquireNotification("pool", 1, "block", "auxpow",
                   start + TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(30)))
                Interlocked.Increment(ref alerts);
        });

        Assert.Equal(1, alerts);
    }

    [Fact]
    public async Task Classification_FailedDelayedNotification_IsRetried()
    {
        var tracker = new ActiveBlockGracePeriodTracker();
        var start = DateTime.UtcNow;
        var block = PendingBlock("coinbase-txid");
        block.Type = "auxpow";

        var first = await CreateFixtureAsync(tracker, start);
        ConfigureUnavailableWalletGrace(first, block);
        await first.Handler.ClassifyBlocksAsync(first.Pool,
            new[] { block }, CancellationToken.None);

        var failed = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(31));
        ConfigureUnavailableWalletGrace(failed, block);
        failed.MessageBus
            .When(x => x.SendMessage(Arg.Any<AdminNotification>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("notification transport failed"));

        await failed.Handler.ClassifyBlocksAsync(failed.Pool,
            new[] { block }, CancellationToken.None);

        var retry = await CreateFixtureAsync(tracker, start + TimeSpan.FromMinutes(32));
        ConfigureUnavailableWalletGrace(retry, block);
        await retry.Handler.ClassifyBlocksAsync(retry.Pool,
            new[] { block }, CancellationToken.None);

        retry.MessageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x => x.Message.Contains(block.Hash)),
            Arg.Any<string>());
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_BlockWithMissingWalletDetails_RemainsPendingWhenActivityLookupUnavailable(
        string blockType, string blockHash)
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(JToken.FromObject(new Transaction
            {
                Amount = 0m,
                Confirmations = 0,
                Details = Array.Empty<TransactionDetails>(),
            })),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-500, "Cancelled", null));

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_BlockWithWalletIndexLag_RemainsPendingWhenActivityHashMismatches(
        string blockType, string blockHash)
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "different-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    [Theory]
    [InlineData("auxpow", "doge-block")]
    [InlineData("merged-parent", "ltc-block")]
    public async Task Classification_BlockWithWalletIndexLag_RemainsPendingWhenActivityHasZeroConfirmations(
        string blockType, string blockHash)
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = blockType;
        block.Hash = blockHash;
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = blockHash,
            Confirmations = 0,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
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

    [Fact]
    public async Task Classification_MergedParentWithWalletIndexLag_RemainsPendingWhenBlockExists()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = "merged-parent";
        block.Hash = "ltc-block";
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "ltc-block",
            Confirmations = 1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Pending, block.Status);
        Assert.False(block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    [Fact]
    public async Task Classification_MergedParentWithWalletIndexLag_IsOrphanedWhenBlockIsNotActive()
    {
        var fixture = await CreateFixtureAsync();
        var block = PendingBlock("coinbase-txid");
        block.Type = "merged-parent";
        block.Hash = "ltc-block";
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(new DaemonBlock
        {
            Hash = "ltc-block",
            Confirmations = -1,
        });

        var result = await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, CancellationToken.None);

        Assert.Equal(new[] { block }, result);
        Assert.Equal(BlockStatus.Orphaned, block.Status);
        Assert.Equal(0, block.Reward);
        Assert.True(block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(1, fixture.Handler.BlockCalls);
    }

    private async Task<HandlerFixture> CreateFixtureAsync(
        IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker = null,
        DateTime? now = null,
        bool hasBrokenSendMany = false)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var mapper = container.Resolve<IMapper>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var paymentRepository = Substitute.For<IPaymentRepository>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var events = new ConcurrentQueue<string>();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        transaction.When(x => x.Commit()).Do(_ => events.Enqueue("commit"));
        paymentRepository.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(),
            Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>())
            .Returns(call =>
            {
                events.Enqueue($"persist:{call.ArgAt<string>(3)}");
                return true;
            });
        var clock = Substitute.For<IMasterClock>();
        var currentTime = now ?? DateTime.UtcNow;
        clock.Now.Returns(currentTime);
        var messageBus = Substitute.For<IMessageBus>();
        var handler = new TestBitcoinPayoutHandler(container, connectionFactory, mapper,
            shareRepository, blockRepository, balanceRepository, paymentRepository, clock, messageBus,
            activeBlockGracePeriodTracker ?? new ActiveBlockGracePeriodTracker());
        handler.Events = events;
        var config = new PoolConfig
        {
            Id = "doge-test",
            Template = ModuleInitializer.CoinTemplates["dogecoin"],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 22555 } },
            PaymentProcessing = new PoolPaymentProcessingConfig(),
            RewardRecipients = Array.Empty<RewardRecipient>(),
            Extra = hasBrokenSendMany
                ? new Dictionary<string, object> { ["hasBrokenSendMany"] = true }
                : null,
        };
        await handler.ConfigureAsync(new ClusterConfig(), config, CancellationToken.None);

        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);

        return new HandlerFixture(handler, pool, config, shareRepository, balanceRepository,
            paymentRepository, messageBus, events, currentTime);
    }

    private static void ConfigureUnavailableWalletGrace(HandlerFixture fixture,
        PersistedBlock block)
    {
        fixture.Handler.TransactionResponse = new[]
        {
            new RpcResponse<JToken>(null,
                new JsonRpcError(-5, "Invalid or non-wallet transaction id", null)),
        };
        fixture.Handler.BlockResponse = new RpcResponse<DaemonBlock>(null,
            new JsonRpcError(-500, "RPC timeout", null));
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
        IPaymentRepository PaymentRepository, IMessageBus MessageBus,
        ConcurrentQueue<string> Events, DateTime Now);

    private sealed class TestBitcoinPayoutHandler : BitcoinPayoutHandler
    {
        public TestBitcoinPayoutHandler(IComponentContext ctx, IConnectionFactory cf, IMapper mapper,
            IShareRepository shareRepo, IBlockRepository blockRepo, IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo, IMasterClock clock, IMessageBus messageBus,
            IActiveBlockGracePeriodTracker activeBlockGracePeriodTracker) :
            base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus,
                activeBlockGracePeriodTracker)
        {
        }

        public RpcResponse<DaemonBlock> BlockResponse { get; set; }
        public RpcResponse<JToken>[] TransactionResponse { get; set; }
        public PersistedBlock FinalizedAuxPowBlock { get; set; }
        public RpcResponse<string> PayoutResponse { get; set; }
        public RpcResponse<JToken> MempoolResponse { get; set; }
        public RpcResponse<Transaction> WalletTransactionResponse { get; set; }
        public Dictionary<string, RpcResponse<string>> SendToAddressResponses { get; } = new();
        public Dictionary<string, RpcResponse<JToken>> MempoolResponses { get; } = new();
        public Dictionary<string, RpcResponse<Transaction>> WalletTransactionResponses { get; } = new();
        public ConcurrentQueue<RpcResponse<JToken>> MempoolResponseSequence { get; } = new();
        public ConcurrentQueue<RpcResponse<Transaction>> WalletTransactionResponseSequence { get; } = new();
        public ConcurrentBag<string> MempoolTransactionIds { get; } = new();
        public ConcurrentQueue<string> Events { get; set; } = new();
        public int BlockCalls { get; private set; }
        public int TransactionCalls { get; private set; }
        public int WalletTransactionCalls { get; private set; }
        public int VerificationDelayCalls { get; private set; }
        public bool ThrowCancellationFromMempoolLookup { get; set; }
        public bool ThrowCancellationFromVerificationDelay { get; set; }

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

        protected override Task<RpcResponse<string>> SendManyAsync(object[] args,
            CancellationToken ct)
        {
            return Task.FromResult(PayoutResponse);
        }

        protected override Task<RpcResponse<string>> SendToAddressAsync(object[] args,
            CancellationToken ct)
        {
            var address = args[0]?.ToString();
            return Task.FromResult(SendToAddressResponses[address]);
        }

        protected override Task<RpcResponse<JToken>> GetMempoolEntryAsync(string txId,
            CancellationToken ct)
        {
            MempoolTransactionIds.Add(txId);
            Events.Enqueue($"verify:{txId}");

            if(ThrowCancellationFromMempoolLookup)
                return Task.FromException<RpcResponse<JToken>>(new OperationCanceledException());

            if(MempoolResponseSequence.TryDequeue(out var sequencedResponse))
                return Task.FromResult(sequencedResponse);

            return Task.FromResult(MempoolResponses.TryGetValue(txId, out var response)
                ? response
                : MempoolResponse);
        }

        protected override Task<RpcResponse<Transaction>> GetWalletTransactionAsync(string txId,
            CancellationToken ct)
        {
            WalletTransactionCalls++;

            if(WalletTransactionResponseSequence.TryDequeue(out var sequencedResponse))
                return Task.FromResult(sequencedResponse);

            return Task.FromResult(WalletTransactionResponses.TryGetValue(txId, out var response)
                ? response
                : WalletTransactionResponse);
        }

        protected override Task DelayPayoutVerificationAsync(TimeSpan delay,
            CancellationToken ct)
        {
            VerificationDelayCalls++;

            if(ThrowCancellationFromVerificationDelay)
                return Task.FromException(new OperationCanceledException());

            return Task.CompletedTask;
        }
    }
}
