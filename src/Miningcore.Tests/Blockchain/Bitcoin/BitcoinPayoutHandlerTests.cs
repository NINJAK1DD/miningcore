using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
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
using Miningcore.Notifications;
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Payout_SuccessWithoutUsableTransactionId_IsUnknown(string transactionId)
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(transactionId);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, CancellationToken.None));

        Assert.Contains("success without a transaction id", exception.Message);
    }

    [Fact]
    public async Task Payout_AllBalancesBelowWalletPrecision_LogsFocusedWarning()
    {
        var fixture = await CreateFixtureAsync();
        using var logFactory = new NLog.LogFactory();
        var logTarget = new NLog.Targets.MemoryTarget
        {
            Layout = "${level}|${message}",
        };
        var logConfig = new NLog.Config.LoggingConfiguration();
        logConfig.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Warn, logTarget);
        logFactory.Configuration = logConfig;
        fixture.Handler.UseLogger(logFactory.GetLogger(nameof(
            Payout_AllBalancesBelowWalletPrecision_LogsFocusedWarning)));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTest1",
                Amount = 0.00009m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTest2",
                Amount = 0.00001m,
            },
        }, CancellationToken.None);

        var warning = Assert.Single(logTarget.Logs);
        Assert.Equal("Warn|[Bitcoin Payout Handler] No payout submitted: all " +
            "2 selected balance(s) are below the configured payout precision " +
            "(payoutDecimalPlaces=4). Review minimumPayment", warning);
        Assert.Null(fixture.Handler.LastSendManyArgs);
        Assert.Empty(fixture.Handler.SendToAddressAddresses);
    }

    [Fact]
    public async Task Payout_MixedPrecision_LogsOwedAndWalletRequestTotals()
    {
        var fixture = await CreateFixtureAsync();
        using var logFactory = new NLog.LogFactory();
        var logTarget = new NLog.Targets.MemoryTarget
        {
            Layout = "${level}|${message}",
        };
        var logConfig = new NLog.Config.LoggingConfiguration();
        logConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Info, logTarget);
        logFactory.Configuration = logConfig;
        fixture.Handler.UseLogger(logFactory.GetLogger(nameof(
            Payout_MixedPrecision_LogsOwedAndWalletRequestTotals)));
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DPayable",
                Amount = 1m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DBelowPrecision",
                Amount = 0.00009m,
            },
        }, CancellationToken.None);

        Assert.Contains("Info|[Bitcoin Payout Handler] Preparing wallet request: " +
            "1 DOGE to 1 payable address(es) from 1.00009 DOGE owed across " +
            "2 selected balance(s)", logTarget.Logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payout_HighPrecisionAmount_LogsConfiguredPrecision(
        bool hasBrokenSendMany)
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: hasBrokenSendMany,
            coinTemplate: "kylacoin");
        using var logFactory = new NLog.LogFactory();
        var logTarget = new NLog.Targets.MemoryTarget
        {
            Layout = "${level}|${message}",
        };
        var logConfig = new NLog.Config.LoggingConfiguration();
        logConfig.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Info, logTarget);
        logFactory.Configuration = logConfig;
        fixture.Handler.UseLogger(logFactory.GetLogger(nameof(
            Payout_HighPrecisionAmount_LogsConfiguredPrecision)));

        if(hasBrokenSendMany)
        {
            fixture.Handler.SendToAddressResponses["KTest"] =
                new RpcResponse<string>("payout-txid");
            fixture.Handler.MempoolResponses["payout-txid"] =
                new RpcResponse<JToken>(new JObject());
        }
        else
        {
            fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
            fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        }

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "KTest",
                Amount = 0.000000123456m,
            },
        }, CancellationToken.None);

        Assert.Contains("Info|[Bitcoin Payout Handler] Preparing wallet request: " +
            "0.000000123456 KCN to 1 payable address(es) from 0.000000123456 KCN " +
            "owed across 1 selected balance(s)", logTarget.Logs);

        if(hasBrokenSendMany)
        {
            Assert.Contains(logTarget.Logs, message =>
                message.Contains("Sending 0.000000123456 KCN to KTest"));
        }

        Assert.DoesNotContain(logTarget.Logs, message => message.Contains(" 0 KCN"));
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
    public async Task Payout_SendManyCancellationAfterWalletResponse_VerifiesWithFreshToken()
    {
        var fixture = await CreateFixtureAsync();
        using var cts = new CancellationTokenSource();
        fixture.Handler.SendManyOverride = _ =>
        {
            cts.Cancel();
            return Task.FromResult(new RpcResponse<string>("payout-txid"));
        };
        fixture.Handler.ReturnCancelledRpcResponseForCancelledToken = true;
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(new[] { "payout-txid" }, fixture.Handler.MempoolTransactionIds);
        Assert.All(fixture.Handler.MempoolCancellationStates, Assert.False);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_SendManyCancellationDuringVerification_UsesGracePeriod()
    {
        var fixture = await CreateFixtureAsync();
        using var cts = new CancellationTokenSource();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        var verificationTokenCancelled = true;
        fixture.Handler.MempoolEntryOverride = (_, verificationToken) =>
        {
            cts.Cancel();
            verificationTokenCancelled = verificationToken.IsCancellationRequested;

            return Task.FromResult(verificationTokenCancelled
                ? new RpcResponse<JToken>(null, new JsonRpcError(-500, "Cancelled", null))
                : new RpcResponse<JToken>(new JObject()));
        };

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.False(verificationTokenCancelled);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_SendMany_PreCanceled_DoesNotStartWalletSubmission()
    {
        var fixture = await CreateFixtureAsync();
        using var cts = new CancellationTokenSource();
        var walletSubmissionStarted = false;
        fixture.Handler.SendManyOverride = _ =>
        {
            walletSubmissionStarted = true;
            return Task.FromResult(new RpcResponse<string>("payout-txid"));
        };
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, cts.Token));

        Assert.False(walletSubmissionStarted);
        Assert.DoesNotContain(fixture.Events, x => x.StartsWith("persist:"));
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

    [Fact]
    public async Task Payout_SendManyFailureNotificationFailure_DoesNotPropagate()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError(-5, "rejected", null));
        fixture.MessageBus.When(x => x.SendMessage(
                Arg.Is<PaymentNotification>(notification =>
                    notification.Outcome == PaymentNotificationOutcome.Failure),
                Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("failure subscriber failed"));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification =>
                notification.Outcome == PaymentNotificationOutcome.Failure &&
                notification.Error.Contains("rejected")),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_LockedWalletWithoutPassword_NotifiesBestEffort()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError((int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null));
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call =>
            {
                notification = call.ArgAt<PaymentNotification>(0);
                throw new InvalidOperationException("failure subscriber failed");
            });

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        Assert.NotNull(notification);
        Assert.Equal(PaymentNotificationOutcome.Failure, notification.Outcome);
        Assert.Contains("walletPassword was not configured", notification.Error);
        Assert.Equal(0, fixture.Handler.UnlockWalletCalls);
        await fixture.PaymentRepository.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Payout_WalletUnlockRejected_NotifiesActualUnlockErrorBestEffort()
    {
        var fixture = await CreateFixtureAsync(walletPassword: "wrong-password");
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError((int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null));
        fixture.Handler.UnlockWalletResponse = new RpcResponse<JToken>(null,
            new JsonRpcError(-14, "incorrect passphrase", null));
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call =>
            {
                notification = call.ArgAt<PaymentNotification>(0);
                throw new InvalidOperationException("failure subscriber failed");
            });

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        Assert.NotNull(notification);
        Assert.Equal(PaymentNotificationOutcome.Failure, notification.Outcome);
        Assert.Contains("incorrect passphrase", notification.Error);
        Assert.Contains("code -14", notification.Error);
        Assert.DoesNotContain("wallet locked", notification.Error);
        Assert.Equal(1, fixture.Handler.UnlockWalletCalls);
        Assert.Equal("wrong-password", fixture.Handler.UnlockWalletPassword);
        await fixture.PaymentRepository.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Payout_CancellationDuringWalletUnlock_PropagatesWithoutFailureAlert()
    {
        var fixture = await CreateFixtureAsync(walletPassword: "configured-password");
        using var cts = new CancellationTokenSource();
        fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
            new JsonRpcError((int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED,
                "wallet locked", null));
        fixture.Handler.UnlockWalletOverride = (_, token) =>
        {
            Assert.Equal(cts.Token, token);
            cts.Cancel();
            return Task.FromResult(new RpcResponse<JToken>(null,
                new JsonRpcError(-500, "Cancelled", null)));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
            }, cts.Token));

        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
        await fixture.PaymentRepository.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>());
        await fixture.BalanceRepository.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>());
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
    public async Task Payout_BrokenSendMany_CancellationAfterWalletResponses_VerifiesWithFreshToken()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        var secondSubmissionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationTriggered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        fixture.Handler.SendToAddressOverride = async (address, _) =>
        {
            if(address == "DTest1")
            {
                await secondSubmissionStarted.Task;
                cts.Cancel();
                cancellationTriggered.TrySetResult();
                return new RpcResponse<string>("payout-txid-1");
            }

            secondSubmissionStarted.TrySetResult();
            await cancellationTriggered.Task;
            return new RpcResponse<string>("payout-txid-2");
        };
        fixture.Handler.ReturnCancelledRpcResponseForCancelledToken = true;
        fixture.Handler.MempoolResponses["payout-txid-1"] =
            new RpcResponse<JToken>(new JObject());
        fixture.Handler.MempoolResponses["payout-txid-2"] =
            new RpcResponse<JToken>(new JObject());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, cts.Token));

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(new[] { "DTest1", "DTest2" },
            fixture.Handler.SendToAddressAddresses.OrderBy(x => x));
        Assert.Equal(new[] { "payout-txid-1", "payout-txid-2" },
            fixture.Handler.MempoolTransactionIds.OrderBy(x => x));
        Assert.All(fixture.Handler.MempoolCancellationStates, Assert.False);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid-1", fixture.Now);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid-2", fixture.Now);

        var events = fixture.Events.ToArray();
        var persistenceCommit = Array.FindIndex(events, x => x == "commit");
        var firstVerification = Array.FindIndex(events, x => x.StartsWith("verify:"));
        Assert.True(persistenceCommit >= 0);
        Assert.True(firstVerification > persistenceCommit);
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification => notification.Error == null),
            Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_BrokenSendMany_PreCanceled_DoesNotStartWalletSubmission()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
            }, cts.Token));

        Assert.Empty(fixture.Handler.SendToAddressAddresses);
        Assert.DoesNotContain(fixture.Events, x => x.StartsWith("persist:"));
    }

    [Fact]
    public async Task Payout_BrokenSendMany_CancellationDuringWalletCall_IsUncertain()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        fixture.Handler.SendToAddressOverride = (_, _) =>
        {
            cts.Cancel();
            return Task.FromException<RpcResponse<string>>(
                new OperationCanceledException(cts.Token));
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
            }, cts.Token));

        Assert.Contains(BitcoinCommands.SendToAddress, exception.Message);
        Assert.Contains("interrupted", exception.Message);
        Assert.IsType<PayoutOutcomeUncertainException>(exception.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(
            exception.InnerException.InnerException);
    }

    [Fact]
    public async Task Payout_BrokenSendMany_MultipleCancelledRpcResponses_AggregatesEveryRecipient()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        var bothSubmissionsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSubmissions = 0;

        fixture.Handler.SendToAddressOverride = async (_, _) =>
        {
            if(Interlocked.Increment(ref startedSubmissions) == 2)
            {
                cts.Cancel();
                bothSubmissionsStarted.TrySetResult();
            }

            await bothSubmissionsStarted.Task;
            return new RpcResponse<string>(null,
                new JsonRpcError(-500, "Cancelled", null));
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, cts.Token));

        Assert.Contains("DTest1", exception.Message);
        Assert.Contains("1 DOGE", exception.Message);
        Assert.Contains("DTest2", exception.Message);
        Assert.Contains("2 DOGE", exception.Message);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.All(aggregate.InnerExceptions,
            inner => Assert.IsType<PayoutOutcomeUncertainException>(inner));
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_BrokenSendMany_UncertainAmountUsesConfiguredPrecision()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true,
            coinTemplate: "kylacoin");
        fixture.Handler.SendToAddressOverride = (_, _) =>
            Task.FromResult(new RpcResponse<string>(null,
                new JsonRpcError(-500, "response lost", null)));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance
                {
                    PoolId = fixture.Config.Id,
                    Address = "KTest",
                    Amount = 0.000000123456m,
                },
            }, CancellationToken.None));

        Assert.Contains("KTest 0.000000123456 KCN", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Payout_BrokenSendMany_SuccessWithoutUsableTransactionId_IsUnknown(
        string transactionId)
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest"] =
            new RpcResponse<string>(transactionId);

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance
                {
                    PoolId = fixture.Config.Id,
                    Address = "DTest",
                    Amount = 1,
                },
            }, CancellationToken.None));

        Assert.Contains("success without a transaction id", exception.Message);
        Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTest" && x.TransactionId == null);
        await fixture.PaymentRepository.DidNotReceive().TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task Payout_BrokenSendMany_MixedKnownAndCancelled_VerifiesKnownBeforeFailStop()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        var bothSubmissionsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSubmissions = 0;

        fixture.Handler.SendToAddressOverride = async (address, _) =>
        {
            if(Interlocked.Increment(ref startedSubmissions) == 2)
            {
                cts.Cancel();
                bothSubmissionsStarted.TrySetResult();
            }

            await bothSubmissionsStarted.Task;
            return address == "DTest1"
                ? new RpcResponse<string>("payout-txid-1")
                : new RpcResponse<string>(null,
                    new JsonRpcError(-500, "Cancelled", null));
        };
        fixture.Handler.MempoolResponses["payout-txid-1"] =
            new RpcResponse<JToken>(new JObject());

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, cts.Token));

        Assert.Contains("DTest2", exception.Message);
        Assert.Contains("2 DOGE", exception.Message);
        Assert.DoesNotContain("DTest1", exception.Message);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid-1", fixture.Now);
        Assert.Equal(new[] { "payout-txid-1" }, fixture.Handler.MempoolTransactionIds);
        Assert.All(fixture.Handler.MempoolCancellationStates, Assert.False);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_BrokenSendMany_ConclusiveMixedOutcomes_NotifiesEachSubset()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-5, "rejected", null));
        fixture.Handler.MempoolResponses["payout-txid"] =
            new RpcResponse<JToken>(new JObject());

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
            new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
        }, CancellationToken.None);

        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification =>
                notification.Error == null && notification.Amount == 1),
            Arg.Any<string>());
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Is<PaymentNotification>(notification =>
                notification.Error != null && notification.Amount == 2),
            Arg.Any<string>());
        fixture.MessageBus.Received(2).SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_BrokenSendMany_PostCancellationVerificationTimeout_ReportsEveryTransaction()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        fixture.Handler.PostCancellationVerificationGracePeriodOverride =
            TimeSpan.FromMilliseconds(20);
        var bothSubmissionsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedSubmissions = 0;
        fixture.Handler.SendToAddressOverride = async (address, _) =>
        {
            if(Interlocked.Increment(ref startedSubmissions) == 2)
            {
                cts.Cancel();
                bothSubmissionsStarted.TrySetResult();
            }

            await bothSubmissionsStarted.Task;
            return new RpcResponse<string>(
                address == "DTest1" ? "payout-txid-1" : "payout-txid-2");
        };
        fixture.Handler.MempoolEntryOverride = async (_, verificationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, verificationToken);
            return new RpcResponse<JToken>(new JObject());
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, cts.Token));

        Assert.Contains("payout-txid-1", exception.Message);
        Assert.Contains("payout-txid-2", exception.Message);
        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid-1", fixture.Now);
        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid-2", fixture.Now);
    }

    [Fact]
    public async Task Payout_BroadcastUncertainty_DefersNotificationToPayoutManager()
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
        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, CancellationToken.None));

        Assert.Contains("payout-txid", exception.Message);
        Assert.Contains("DTest2", exception.Message);
        Assert.Contains("rejected", exception.Message);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_UnknownSubmission_DefersNotificationToPayoutManager()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        fixture.Handler.MempoolResponses["payout-txid"] =
            new RpcResponse<JToken>(new JObject());
        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
            }, CancellationToken.None));

        Assert.Contains("response lost", exception.Message);
        fixture.MessageBus.DidNotReceive().SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());
    }

    [Fact]
    public async Task PayoutManager_MixedUnknownSubmission_EmitsOneReconciliationSafeNotification()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTest1",
                Amount = 1,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTest2",
                Amount = 2,
            },
        };
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(fixture.Connection,
                fixture.Config.Id, Arg.Any<decimal>())
            .Returns(balances);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        fixture.Handler.MempoolResponses["payout-txid"] =
            new RpcResponse<JToken>(new JObject());
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call => notification = call.ArgAt<PaymentNotification>(0));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.Pool, fixture.Config,
                fixture.Handler, CancellationToken.None));

        Assert.Contains("DTest2", exception.Message);
        Assert.Contains("response lost", exception.Message);
        fixture.PayoutLease.Received(1).MarkFinancialOutcomeUncertain();
        fixture.PayoutLease.DidNotReceive().CompleteFinancialOperation();
        fixture.MessageBus.Received(1).SendMessage(
            Arg.Any<PaymentNotification>(), Arg.Any<string>());

        Assert.NotNull(notification);
        Assert.Equal(PaymentNotificationOutcome.Uncertain, notification.Outcome);
        Assert.Equal(3, notification.Amount);
        var accepted = Assert.Single(notification.Reconciliation.Accepted);
        Assert.Equal("DTest1", accepted.Address);
        Assert.Equal(1, accepted.Amount);
        Assert.Equal("payout-txid", accepted.TransactionId);
        var uncertain = Assert.Single(notification.Reconciliation.Uncertain);
        Assert.Equal("DTest2", uncertain.Address);
        Assert.Equal(2, uncertain.Amount);
        Assert.Empty(notification.Reconciliation.Failed);

        var rendered = NotificationService.FormatPaymentNotification(notification,
            fixture.Config.Template.Symbol, fixture.Config.Template.ExplorerTxLink);
        Assert.Equal("Payout Outcome Uncertain Notification", rendered.Subject);
        Assert.Contains("Payout batch totalling 3 DOGE", rendered.EmailMessage);
        Assert.Contains("Accepted and persisted: 1 DOGE to DTest1, transaction payout-txid",
            rendered.EmailMessage);
        Assert.Contains("Uncertain: 2 DOGE to DTest2", rendered.EmailMessage);
        Assert.DoesNotContain("Failed to pay out", rendered.EmailMessage);
        Assert.DoesNotContain("<br/>", rendered.PushoverMessage);
        Assert.Contains("Accepted/persisted: 1 DOGE (1 recipient(s))",
            rendered.PushoverMessage);
        Assert.Contains("Uncertain: 2 DOGE (1 recipient(s))",
            rendered.PushoverMessage);

        await fixture.PaymentRepository.Received(1).TryBeginPaymentBatchAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "payout-txid", fixture.Now);
    }

    [Fact]
    public async Task PayoutManager_HighPrecisionUncertainty_PreservesExactAmount()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true,
            coinTemplate: "kylacoin");
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "KTest",
                Amount = 0.000000123456m,
            },
        };
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(fixture.Connection,
                fixture.Config.Id, Arg.Any<decimal>())
            .Returns(balances);
        fixture.Handler.SendToAddressResponses["KTest"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call => notification = call.ArgAt<PaymentNotification>(0));

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.Pool, fixture.Config,
                fixture.Handler, CancellationToken.None));

        var rendered = NotificationService.FormatPaymentNotification(notification,
            fixture.Config.Template.Symbol, fixture.Config.Template.ExplorerTxLink);
        Assert.Contains("0.000000123456 KCN", rendered.EmailMessage);
        Assert.Contains("0.000000123456 KCN", rendered.PushoverMessage);
        Assert.DoesNotContain("0 KCN", rendered.EmailMessage);
    }

    [Fact]
    public async Task Payout_SendManyPersistenceFailure_PreservesKnownTransactionId()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.PaymentRepository.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(),
                Arg.Any<IDbTransaction>(), fixture.Config.Id, "payout-txid", fixture.Now)
            .Returns(Task.FromException<bool>(
                new InvalidOperationException("database unavailable")));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance
                {
                    PoolId = fixture.Config.Id,
                    Address = "DTest",
                    Amount = 1,
                },
            }, CancellationToken.None));

        var uncertain = Assert.Single(exception.Reconciliation.Uncertain);
        Assert.Equal("DTest", uncertain.Address);
        Assert.Equal(1, uncertain.Amount);
        Assert.Equal("payout-txid", uncertain.TransactionId);
        Assert.Contains("could not be persisted", uncertain.Detail);
    }

    [Fact]
    public async Task Payout_SendManyReconciliation_DistinguishesRequestedAndTruncatedAmounts()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.PaymentRepository.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(),
                Arg.Any<IDbTransaction>(), fixture.Config.Id, "payout-txid", fixture.Now)
            .Returns(Task.FromException<bool>(
                new InvalidOperationException("database unavailable")));
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestBelow",
                Amount = 1.23454m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestAbove",
                Amount = 2.34566m,
            },
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, balances, CancellationToken.None));

        Assert.Equal(balances.Sum(x => x.Amount),
            exception.Reconciliation.Uncertain.Sum(x => x.Amount));
        Assert.Equal(1.2345m, Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTestBelow").SubmittedAmount);
        Assert.Equal(2.3456m, Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTestAbove").SubmittedAmount);
    }

    [Fact]
    public async Task Payout_BrokenSendManyReconciliation_DistinguishesRequestedAndTruncatedAmounts()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTestBelow"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        fixture.Handler.SendToAddressResponses["DTestAbove"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestBelow",
                Amount = 1.23454m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestAbove",
                Amount = 2.34566m,
            },
        };

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, balances, CancellationToken.None));

        Assert.Equal(balances.Sum(x => x.Amount),
            exception.Reconciliation.Uncertain.Sum(x => x.Amount));
        Assert.Equal(1.2345m, Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTestBelow").SubmittedAmount);
        Assert.Equal(2.3456m, Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTestAbove").SubmittedAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayoutManager_UncertainOutcome_AccountsForPrecisionSkippedRecipient(
        bool hasBrokenSendMany)
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: hasBrokenSendMany);
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DPayable",
                Amount = 1.00000m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DBelowPrecision",
                Amount = 0.00009m,
            },
        };
        fixture.BalanceRepository.GetPoolBalancesOverThresholdAsync(fixture.Connection,
                fixture.Config.Id, Arg.Any<decimal>())
            .Returns(balances);

        if(hasBrokenSendMany)
        {
            fixture.Handler.SendToAddressResponses["DPayable"] =
                new RpcResponse<string>(null,
                    new JsonRpcError(-500, "response lost", null));
        }
        else
        {
            fixture.Handler.PayoutResponse = new RpcResponse<string>(null,
                new JsonRpcError(-500, "response lost", null));
        }

        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call => notification = call.ArgAt<PaymentNotification>(0));

        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Manager.PayoutPoolBalancesAsync(fixture.Pool, fixture.Config,
                fixture.Handler, CancellationToken.None));

        Assert.NotNull(notification);
        Assert.Equal(balances.Sum(x => x.Amount), notification.Amount);
        var uncertain = Assert.Single(notification.Reconciliation.Uncertain);
        Assert.Equal("DPayable", uncertain.Address);
        var skipped = Assert.Single(notification.Reconciliation.NotAttempted);
        Assert.Equal("DBelowPrecision", skipped.Address);
        Assert.Equal(0.00009m, skipped.Amount);
        Assert.Contains("below wallet precision", skipped.Detail);

        var categories = notification.Reconciliation.Accepted
            .Concat(notification.Reconciliation.Failed)
            .Concat(notification.Reconciliation.Uncertain)
            .Concat(notification.Reconciliation.NotAttempted)
            .ToArray();
        Assert.Equal(balances.Select(x => x.Address).OrderBy(x => x),
            categories.Select(x => x.Address).OrderBy(x => x));
        Assert.Equal(notification.Amount, categories.Sum(x => x.Amount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Payout_PersistsTruncatedWalletAmountsAndCarriesResidualForward(
        bool hasBrokenSendMany)
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: hasBrokenSendMany);
        var balances = new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestBelow",
                Amount = 1.23454m,
            },
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "DTestAbove",
                Amount = 2.34566m,
            },
        };

        if(hasBrokenSendMany)
        {
            fixture.Handler.SendToAddressResponses["DTestBelow"] =
                new RpcResponse<string>("payout-txid-below");
            fixture.Handler.SendToAddressResponses["DTestAbove"] =
                new RpcResponse<string>("payout-txid-above");
            fixture.Handler.MempoolResponses["payout-txid-below"] =
                new RpcResponse<JToken>(new JObject());
            fixture.Handler.MempoolResponses["payout-txid-above"] =
                new RpcResponse<JToken>(new JObject());
        }
        else
        {
            fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
            fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        }

        await fixture.Handler.PayoutAsync(fixture.Pool, balances, CancellationToken.None);

        await fixture.PaymentRepository.Received(1).InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Is<Payment>(x => x.Address == "DTestBelow" && x.Amount == 1.2345m));
        await fixture.PaymentRepository.Received(1).InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Is<Payment>(x => x.Address == "DTestAbove" && x.Amount == 2.3456m));
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "DTestBelow", -1.2345m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "DTestAbove", -2.3456m, Arg.Any<string>());

        if(hasBrokenSendMany)
        {
            Assert.Equal(1.2345m, fixture.Handler.SendToAddressAmounts["DTestBelow"]);
            Assert.Equal(2.3456m, fixture.Handler.SendToAddressAmounts["DTestAbove"]);
        }
        else
        {
            var walletAmounts = Assert.IsType<Dictionary<string, decimal>>(
                fixture.Handler.LastSendManyArgs[1]);
            Assert.Equal(1.2345m, walletAmounts["DTestBelow"]);
            Assert.Equal(2.3456m, walletAmounts["DTestAbove"]);
        }
    }

    [Fact]
    public async Task Payout_BrokenSendManyCancellationAfterConclusiveFirstWave_Propagates()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        var firstWaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Handler.SendToAddressOverride = async (address, _) =>
        {
            if(Interlocked.Increment(ref started) == 8)
            {
                cts.Cancel();
                firstWaveStarted.TrySetResult();
            }

            await firstWaveStarted.Task;
            return new RpcResponse<string>($"tx-{address}");
        };
        var balances = Enumerable.Range(1, 9)
            .Select(x => new Balance
            {
                PoolId = fixture.Config.Id,
                Address = $"DTest{x}",
                Amount = x,
            }).ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, balances, cts.Token));

        Assert.Equal(8, fixture.Handler.SendToAddressAddresses.Count);
        await fixture.PaymentRepository.Received(8).InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<Payment>());
        await fixture.BalanceRepository.DidNotReceive().AddAmountAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), fixture.Config.Id,
            "DTest9", Arg.Any<decimal>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Payout_MinersPayFees_LabelsSuccessAsWalletRequest()
    {
        var fixture = await CreateFixtureAsync(minersPayTxFees: true);
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call => notification = call.ArgAt<PaymentNotification>(0));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance { PoolId = fixture.Config.Id, Address = "DTest", Amount = 1 },
        }, CancellationToken.None);

        var subtractFeesFrom = Assert.IsType<string[]>(fixture.Handler.LastSendManyArgs[4]);
        Assert.Equal(new[] { "DTest" }, subtractFeesFrom);
        var rendered = NotificationService.FormatPaymentNotification(notification,
            fixture.Config.Template.Symbol, fixture.Config.Template.ExplorerTxLink);
        Assert.StartsWith("Wallet request submitted for ", rendered.EmailMessage);
        Assert.Contains("1.0000 DOGE", rendered.EmailMessage);
        Assert.DoesNotContain("Paid ", rendered.EmailMessage);
    }

    [Fact]
    public async Task Payout_HighPrecisionSuccess_PreservesExactAmount()
    {
        var fixture = await CreateFixtureAsync(coinTemplate: "kylacoin");
        fixture.Handler.PayoutResponse = new RpcResponse<string>("payout-txid");
        fixture.Handler.MempoolResponse = new RpcResponse<JToken>(new JObject());
        PaymentNotification notification = null;
        fixture.MessageBus.When(x => x.SendMessage(Arg.Any<PaymentNotification>(),
                Arg.Any<string>()))
            .Do(call => notification = call.ArgAt<PaymentNotification>(0));

        await fixture.Handler.PayoutAsync(fixture.Pool, new[]
        {
            new Balance
            {
                PoolId = fixture.Config.Id,
                Address = "KTest",
                Amount = 0.000000123456m,
            },
        }, CancellationToken.None);

        var rendered = NotificationService.FormatPaymentNotification(notification,
            fixture.Config.Template.Symbol, fixture.Config.Template.ExplorerTxLink);
        Assert.Contains("0.000000123456 KCN", rendered.EmailMessage);
        Assert.Contains("0.000000123456 KCN", rendered.PushoverMessage);
        Assert.DoesNotContain("0 KCN", rendered.EmailMessage);
    }

    [Fact]
    public async Task Payout_BrokenSendManyPersistenceFailure_PreservesEveryKnownOutcome()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        fixture.Handler.SendToAddressResponses["DTest1"] =
            new RpcResponse<string>("payout-txid");
        fixture.Handler.SendToAddressResponses["DTest2"] =
            new RpcResponse<string>(null, new JsonRpcError(-5, "rejected", null));
        fixture.Handler.SendToAddressResponses["DTest3"] =
            new RpcResponse<string>(null, new JsonRpcError(-500, "response lost", null));
        fixture.PaymentRepository.TryBeginPaymentBatchAsync(Arg.Any<IDbConnection>(),
                Arg.Any<IDbTransaction>(), fixture.Config.Id, "payout-txid", fixture.Now)
            .Returns(Task.FromException<bool>(
                new InvalidOperationException("database unavailable")));

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, new[]
            {
                new Balance { PoolId = fixture.Config.Id, Address = "DTest1", Amount = 1 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest2", Amount = 2 },
                new Balance { PoolId = fixture.Config.Id, Address = "DTest3", Amount = 3 },
            }, CancellationToken.None));

        var submitted = Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTest1");
        Assert.Equal("payout-txid", submitted.TransactionId);
        Assert.Contains("could not be persisted", submitted.Detail);
        var unknown = Assert.Single(exception.Reconciliation.Uncertain,
            x => x.Address == "DTest3");
        Assert.Null(unknown.TransactionId);
        Assert.Contains("response lost", unknown.Detail);
        var failed = Assert.Single(exception.Reconciliation.Failed);
        Assert.Equal("DTest2", failed.Address);
        Assert.Contains("rejected", failed.Detail);
    }

    [Fact]
    public async Task Payout_BrokenSendManyCancellation_ClassifiesUnstartedRecipients()
    {
        var fixture = await CreateFixtureAsync(hasBrokenSendMany: true);
        using var cts = new CancellationTokenSource();
        var firstWaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Handler.SendToAddressOverride = async (_, _) =>
        {
            if(Interlocked.Increment(ref started) == 8)
            {
                cts.Cancel();
                firstWaveStarted.TrySetResult();
            }

            await firstWaveStarted.Task;
            return new RpcResponse<string>(null,
                new JsonRpcError(-500, "response lost", null));
        };
        var balances = Enumerable.Range(1, 9)
            .Select(x => new Balance
            {
                PoolId = fixture.Config.Id,
                Address = $"DTest{x}",
                Amount = x,
            }).ToArray();

        var exception = await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            fixture.Handler.PayoutAsync(fixture.Pool, balances, cts.Token));

        Assert.Equal(8, exception.Reconciliation.Uncertain.Length);
        var notAttempted = Assert.Single(exception.Reconciliation.NotAttempted);
        Assert.Equal("DTest9", notAttempted.Address);
        Assert.Equal(9, notAttempted.Amount);
        Assert.Contains("was not started", notAttempted.Detail);
        Assert.Equal(balances.Sum(x => x.Amount),
            exception.Reconciliation.Uncertain.Sum(x => x.Amount) +
            exception.Reconciliation.NotAttempted.Sum(x => x.Amount));

        var categories = exception.Reconciliation.Accepted
            .Concat(exception.Reconciliation.Failed)
            .Concat(exception.Reconciliation.Uncertain)
            .Concat(exception.Reconciliation.NotAttempted)
            .ToArray();
        Assert.Equal(balances.Length, categories.Length);
        Assert.Equal(balances.Length,
            categories.Select(x => x.Address).Distinct().Count());
        Assert.Equal(balances.Select(x => x.Address).OrderBy(x => x),
            categories.Select(x => x.Address).OrderBy(x => x));
        Assert.Equal(balances.Sum(x => x.Amount), categories.Sum(x => x.Amount));
    }

    [Fact]
    public void PaymentNotification_PushoverSummary_IsPlainTextAndBounded()
    {
        var notification = new PaymentNotification("doge-test", "unknown", 1, "DOGE")
        {
            Outcome = PaymentNotificationOutcome.Uncertain,
            Reconciliation = new PayoutReconciliation
            {
                Uncertain = new[]
                {
                    new PayoutReconciliationEntry
                    {
                        Address = "DTest",
                        Amount = 1,
                        Detail = new string('x', 2000),
                    },
                },
            },
        };

        var rendered = NotificationService.FormatPaymentNotification(notification,
            "DOGE", null);

        Assert.Contains("<br/>", rendered.EmailMessage);
        Assert.DoesNotContain("<br/>", rendered.PushoverMessage);
        Assert.Contains("Uncertain: 1 DOGE (1 recipient(s))", rendered.PushoverMessage);
        Assert.True(rendered.PushoverMessage.EnumerateRunes().Count() <= 1024);

        var truncated = NotificationService.TruncateForPushover(new string('x', 1100));
        Assert.Equal(1024, truncated.EnumerateRunes().Count());
        Assert.EndsWith("…", truncated);
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
        bool hasBrokenSendMany = false,
        string coinTemplate = "dogecoin",
        bool minersPayTxFees = false,
        string walletPassword = null)
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
        var paymentExtra = new Dictionary<string, object>();

        if(minersPayTxFees)
            paymentExtra["minersPayTxFees"] = true;

        if(walletPassword != null)
            paymentExtra["walletPassword"] = walletPassword;

        var config = new PoolConfig
        {
            Id = "doge-test",
            Template = ModuleInitializer.CoinTemplates[coinTemplate],
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 22555 } },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Extra = paymentExtra.Count > 0 ? paymentExtra : null,
            },
            RewardRecipients = Array.Empty<RewardRecipient>(),
            Extra = hasBrokenSendMany
                ? new Dictionary<string, object> { ["hasBrokenSendMany"] = true }
                : null,
        };
        var clusterConfig = new ClusterConfig
        {
            PaymentProcessing = new ClusterPaymentProcessingConfig(),
        };
        await handler.ConfigureAsync(clusterConfig, config, CancellationToken.None);

        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);

        var payoutLease = Substitute.For<IPayoutManagerLease>();
        var manager = new PayoutManager(container, connectionFactory, blockRepository,
            shareRepository, balanceRepository, clusterConfig, messageBus, payoutLease,
            new ProcessStatus());

        return new HandlerFixture(handler, pool, config, shareRepository, balanceRepository,
            paymentRepository, messageBus, events, currentTime, manager, payoutLease,
            connection);
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
        ConcurrentQueue<string> Events, DateTime Now, PayoutManager Manager,
        IPayoutManagerLease PayoutLease, IDbConnection Connection);

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
        public RpcResponse<JToken> UnlockWalletResponse { get; set; }
        public Func<string, CancellationToken, Task<RpcResponse<JToken>>>
            UnlockWalletOverride { get; set; }
        public int UnlockWalletCalls { get; private set; }
        public string UnlockWalletPassword { get; private set; }
        public Func<CancellationToken, Task<RpcResponse<string>>> SendManyOverride { get; set; }
        public object[] LastSendManyArgs { get; private set; }
        public RpcResponse<JToken> MempoolResponse { get; set; }
        public RpcResponse<Transaction> WalletTransactionResponse { get; set; }
        public Dictionary<string, RpcResponse<string>> SendToAddressResponses { get; } = new();
        public Func<string, CancellationToken, Task<RpcResponse<string>>> SendToAddressOverride { get; set; }
        public ConcurrentBag<string> SendToAddressAddresses { get; } = new();
        public ConcurrentDictionary<string, decimal> SendToAddressAmounts { get; } = new();
        public Dictionary<string, RpcResponse<JToken>> MempoolResponses { get; } = new();
        public Dictionary<string, RpcResponse<Transaction>> WalletTransactionResponses { get; } = new();
        public Func<string, CancellationToken, Task<RpcResponse<JToken>>> MempoolEntryOverride { get; set; }
        public ConcurrentQueue<RpcResponse<JToken>> MempoolResponseSequence { get; } = new();
        public ConcurrentQueue<RpcResponse<Transaction>> WalletTransactionResponseSequence { get; } = new();
        public ConcurrentBag<string> MempoolTransactionIds { get; } = new();
        public ConcurrentBag<bool> MempoolCancellationStates { get; } = new();
        public ConcurrentQueue<string> Events { get; set; } = new();
        public int BlockCalls { get; private set; }
        public int TransactionCalls { get; private set; }
        public int WalletTransactionCalls { get; private set; }
        public int VerificationDelayCalls { get; private set; }
        public bool ThrowCancellationFromMempoolLookup { get; set; }
        public bool ThrowCancellationFromVerificationDelay { get; set; }
        public bool ReturnCancelledRpcResponseForCancelledToken { get; set; }
        public TimeSpan? PostCancellationVerificationGracePeriodOverride { get; set; }

        protected override TimeSpan PostCancellationVerificationGracePeriod =>
            PostCancellationVerificationGracePeriodOverride ??
            base.PostCancellationVerificationGracePeriod;

        public void UseLogger(NLog.ILogger value)
        {
            logger = value;
        }

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
            LastSendManyArgs = args;

            if(SendManyOverride != null)
                return SendManyOverride(ct);

            return Task.FromResult(PayoutResponse);
        }

        protected override Task<RpcResponse<JToken>> UnlockWalletAsync(string password,
            CancellationToken ct)
        {
            UnlockWalletCalls++;
            UnlockWalletPassword = password;

            if(UnlockWalletOverride != null)
                return UnlockWalletOverride(password, ct);

            return Task.FromResult(UnlockWalletResponse);
        }

        protected override Task<RpcResponse<string>> SendToAddressAsync(object[] args,
            CancellationToken ct)
        {
            var address = args[0]?.ToString();
            SendToAddressAddresses.Add(address);
            SendToAddressAmounts[address] = (decimal) args[1];

            if(SendToAddressOverride != null)
                return SendToAddressOverride(address, ct);

            return Task.FromResult(SendToAddressResponses[address]);
        }

        protected override Task<RpcResponse<JToken>> GetMempoolEntryAsync(string txId,
            CancellationToken ct)
        {
            MempoolTransactionIds.Add(txId);
            MempoolCancellationStates.Add(ct.IsCancellationRequested);
            Events.Enqueue($"verify:{txId}");

            if(ThrowCancellationFromMempoolLookup)
                return Task.FromException<RpcResponse<JToken>>(new OperationCanceledException());

            if(ReturnCancelledRpcResponseForCancelledToken && ct.IsCancellationRequested)
            {
                return Task.FromResult(new RpcResponse<JToken>(null,
                    new JsonRpcError(-500, "Cancelled", null)));
            }

            if(MempoolEntryOverride != null)
                return MempoolEntryOverride(txId, ct);

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

            if(ReturnCancelledRpcResponseForCancelledToken && ct.IsCancellationRequested)
            {
                return Task.FromResult(new RpcResponse<Transaction>(null,
                    new JsonRpcError(-500, "Cancelled", null)));
            }

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

            if(ReturnCancelledRpcResponseForCancelledToken && ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            return Task.CompletedTask;
        }
    }
}
