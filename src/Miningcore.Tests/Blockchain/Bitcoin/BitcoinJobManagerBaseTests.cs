using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Rpc;
using Miningcore.Tests.Util;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobManagerBaseTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public void BitcoinConfigure_ResolvesAndCachesDirectSoloPolicy(
        bool? configured, bool expected)
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015),
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "bitcoin-solo",
            Coin = "bitcoin",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "BTC",
                CanonicalName = "Bitcoin",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = configured.HasValue
                ? new Dictionary<string, object>
                {
                    ["soloCoinbasePayout"] = configured.Value,
                }
                : null,
        };

        manager.Configure(pool, new ClusterConfig());

        Assert.Equal(expected, manager.DirectCoinbasePayoutEnabled);

        pool.Extra = new Dictionary<string, object>
        {
            ["soloCoinbasePayout"] = !expected,
        };
        Assert.Equal(expected, manager.DirectCoinbasePayoutEnabled);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public void BitcoinConfigure_ResolvesAndCachesBip54CoinbasePolicy(
        bool? configured, bool expected)
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015),
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "bitcoin-solo",
            Coin = "bitcoin",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "BTC",
                CanonicalName = "Bitcoin",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = configured.HasValue
                ? new Dictionary<string, object>
                {
                    ["bip54Coinbase"] = configured.Value,
                }
                : null,
        };

        manager.Configure(pool, new ClusterConfig());

        Assert.Equal(expected,
            manager.CachedBip54CoinbasePolicy);

        pool.Extra = new Dictionary<string, object>
        {
            ["bip54Coinbase"] = !expected,
        };
        Assert.Equal(expected,
            manager.CachedBip54CoinbasePolicy);
    }

    [Theory]
    [InlineData(null, 100u, 0xfffffffeu, false)]
    [InlineData(false, 0u, 0u, true)]
    public async Task BitcoinUpdate_PassesCachedBip54PolicyToConstructedJob(
        bool? configured, uint expectedLockTime, uint expectedSequence,
        bool witnessFirst)
    {
        ModuleInitializer.Initialize();
        using var container = BuildContainer();
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var manager = new TestBitcoinJobManager(container, clock,
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var coin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var extra = new Dictionary<string, object>
        {
            ["soloCoinbasePayout"] = false,
        };
        if(configured.HasValue)
            extra["bip54Coinbase"] = configured.Value;
        var pool = new PoolConfig
        {
            Id = "bitcoin-cached-policy-update",
            Coin = "bitcoin",
            Address = new Key().PubKey.GetAddress(
                ScriptPubKeyType.Segwit, Network.RegTest).ToString(),
            Template = coin,
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Extra = extra,
        };
        var poolDestination = BitcoinAddress.Create(pool.Address,
            Network.RegTest);

        manager.Configure(pool, new ClusterConfig());
        manager.PrepareJobConstruction(Network.RegTest, poolDestination);

        var cachedPolicy = Assert.IsType<bool>(
            manager.CachedBip54CoinbasePolicy);
        Assert.Equal(configured ?? true, cachedPolicy);

        // Poison the fallback input after Configure. UpdateJob must pass the
        // manager's cached policy rather than letting BitcoinJob re-resolve it.
        manager.ReplaceBoundPoolConfig(new BitcoinPoolConfigExtra
        {
            Bip54Coinbase = !cachedPolicy,
            SoloCoinbasePayout = false,
        });
        manager.Enqueue(new BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<BitcoinBlockTransaction>(),
            DefaultWitnessCommitment =
                "6a24aa21a9ed" + new string('0', 64),
        });

        var update = await manager.Update(CancellationToken.None);

        Assert.True(update.IsNew);
        var jobParams = Assert.IsType<object[]>(
            manager.Current.GetJobParams(false));
        var coinbaseHex = (string) jobParams[2] + "00000001" +
            "00000000000000" + (string) jobParams[3];
        var transaction = NBitcoin.Transaction.Parse(coinbaseHex,
            Network.RegTest);
        Assert.Equal(expectedLockTime, transaction.LockTime.Value);
        Assert.Equal(expectedSequence,
            Assert.Single(transaction.Inputs).Sequence.Value);
        var witnessIndex = witnessFirst ? 0 : 1;
        Assert.StartsWith("6a24aa21a9ed",
            transaction.Outputs[witnessIndex].ScriptPubKey.ToHex(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonCanonicalBitcoinConfigure_HasNoCachedBip54Policy()
    {
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015),
            new MessageBus(), Substitute.For<IExtraNonceProvider>());
        var pool = new PoolConfig
        {
            Id = "litecoin-solo",
            Coin = "litecoin",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "LTC",
                CanonicalName = "Litecoin",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
        };

        manager.Configure(pool, new ClusterConfig());

        Assert.Null(manager.CachedBip54CoinbasePolicy);
    }

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
        var submission = BitcoinDirectSubmissionTestData.Create();
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        Share persistedCandidate = null;
        recorder.PersistDirectBlockSubmissionAsync(Arg.Any<Share>())
            .Returns(call =>
            {
                persistedCandidate = call.Arg<Share>();
                return new DirectBlockSubmissionPreparation();
            });
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            DirectSubmissionException = new IOException(
                "response lost after daemon accepted request"),
            PersistenceObserved = () => persistedCandidate != null,
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
            BlockHash = submission.BlockHash,
            TransactionConfirmationData = submission.CoinbaseTxId,
            SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
            GrossRewardSatoshis = 5_000_000_000,
            DirectMinerRewardSatoshis = 4_900_000_000,
            DirectMinerScriptPubKey = "0014" + new string('1', 40),
            DirectRecipientOutputs = "[]",
            Created = DateTime.UtcNow,
        };

        var result = await manager.PersistAndSubmitDirect(candidate,
            submission.BlockHex, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.True(result.Ambiguous);
        Assert.True(manager.SubmissionStartedAfterPersistence);
        Assert.True(candidate.BlockRecordEmitted);
        Assert.Equal(candidate.BlockHash,
            persistedCandidate.BlockHash);
        Assert.Equal(candidate.TransactionConfirmationData,
            persistedCandidate.TransactionConfirmationData);
        Assert.Equal(BitcoinDirectCoinbaseSettlement.BlockType,
            persistedCandidate.BlockType);
        Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
            persistedCandidate.SettlementMode);
    }

    [Fact]
    public async Task DirectCandidate_CrashAfterDurablePrepare_ReplaysExactBlockOnRestart()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        Share prepared = null;
        var firstRecorder = Substitute.For<IBlockCandidateRecorder>();
        firstRecorder.PersistDirectBlockSubmissionAsync(Arg.Any<Share>())
            .Returns(call =>
            {
                prepared = call.Arg<Share>();
                return new DirectBlockSubmissionPreparation();
            });
        using var container = BuildContainer();
        var first = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), firstRecorder)
        {
            PreparedCheckpointException = new IOException(
                "simulated process termination after durable commit"),
        };
        first.Configure(CreateDirectPool(), new ClusterConfig());
        var share = CreateDirectCandidate(submission);

        await Assert.ThrowsAsync<IOException>(() => first
            .PersistAndSubmitDirect(share, submission.BlockHex,
                CancellationToken.None));
        Assert.NotNull(prepared);
        Assert.Equal(submission.BlockHex, prepared.DirectSubmissionBlock);
        Assert.False(first.SubmissionStartedAfterPersistence);

        var persisted = new Miningcore.Persistence.Model.Block
        {
            Id = 42,
            PoolId = prepared.PoolId,
            BlockHeight = checked((ulong) prepared.BlockHeight),
            Status = Miningcore.Persistence.Model.BlockStatus.Pending,
            Type = prepared.BlockType,
            Hash = prepared.BlockHash,
            Miner = prepared.Miner,
            TransactionConfirmationData =
                prepared.TransactionConfirmationData,
            SettlementMode = prepared.SettlementMode,
            GrossRewardSatoshis = prepared.GrossRewardSatoshis,
            DirectMinerRewardSatoshis =
                prepared.DirectMinerRewardSatoshis,
            DirectMinerScriptPubKey = prepared.DirectMinerScriptPubKey,
            DirectRecipientOutputs = prepared.DirectRecipientOutputs,
            DirectSubmissionState = prepared.DirectSubmissionState,
            DirectSubmissionBlock = prepared.DirectSubmissionBlock,
            DirectSubmissionAttempts = prepared.DirectSubmissionAttempts,
            DirectSubmissionDefinitiveMisses =
                prepared.DirectSubmissionDefinitiveMisses,
            Created = prepared.Created,
        };
        var restartRecorder = Substitute.For<IBlockCandidateRecorder>();
        restartRecorder.GetDirectBlockSubmissionsForReplayAsync(
                prepared.PoolId, 0, 32, Arg.Any<CancellationToken>())
            .Returns(new[] { persisted });
        restartRecorder.GetDirectBlockSubmissionsForReplayAsync(
                prepared.PoolId, 42, 32, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Miningcore.Persistence.Model.Block>());
        BitcoinDirectSubmissionOutcome? recordedOutcome = null;
        restartRecorder.RecordDirectBlockSubmissionAttemptAsync(
                Arg.Any<Share>(), Arg.Any<BitcoinDirectSubmissionOutcome>(),
                Arg.Any<DateTime>())
            .Returns(call =>
            {
                recordedOutcome = call.ArgAt<BitcoinDirectSubmissionOutcome>(1);
                return (Miningcore.Persistence.Model.Block) null;
            });
        var restarted = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), restartRecorder);
        restarted.Configure(CreateDirectPool(), new ClusterConfig());

        await restarted.ReplayDirect(CancellationToken.None);

        Assert.Equal(submission.BlockHex, restarted.SubmittedBlockHex);
        Assert.Equal(BitcoinDirectSubmissionOutcome.ObservedActive,
            recordedOutcome);
    }

    [Fact]
    public async Task DirectCandidate_PreparedBarrierRemainsUnsubmittedAndUnannouncedUntilReleased()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        var prepared = new TaskCompletionSource<Share>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.PersistDirectBlockSubmissionAsync(Arg.Any<Share>())
            .Returns(call =>
            {
                prepared.TrySetResult(call.Arg<Share>());
                return new DirectBlockSubmissionPreparation();
            });
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            PreparedCheckpoint = release.Task,
        };
        manager.Configure(CreateDirectPool(), new ClusterConfig());
        var candidate = CreateDirectCandidate(submission);

        var submit = manager.PersistAndSubmitDirect(candidate,
            submission.BlockHex, CancellationToken.None);
        var preparedCandidate = await prepared.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.False(submit.IsCompleted);
        Assert.Null(manager.SubmittedBlockHex);
        Assert.Equal(BitcoinDirectSubmission.Prepared,
            preparedCandidate.DirectSubmissionState);
        await recorder.DidNotReceive()
            .RecordDirectBlockSubmissionAttemptAsync(Arg.Any<Share>(),
                Arg.Any<BitcoinDirectSubmissionOutcome>(),
                Arg.Any<DateTime>());

        release.TrySetResult(true);
        var result = await submit;

        Assert.True(result.Accepted);
        Assert.Equal(submission.BlockHex, manager.SubmittedBlockHex);
        await recorder.Received(1).RecordDirectBlockSubmissionAttemptAsync(
            preparedCandidate, BitcoinDirectSubmissionOutcome.ObservedActive,
            Arg.Any<DateTime>());
    }

    [Theory]
    [InlineData(DirectBlockSubmissionFailStopReason.CommittedCleanupFailure)]
    [InlineData(DirectBlockSubmissionFailStopReason.CommitOutcomeUncertain)]
    public async Task DirectCandidate_ExceptionalDatabaseFailStopRunsAfterSubmission(
        DirectBlockSubmissionFailStopReason reason)
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        var persistenceError = new IOException(reason.ToString());
        recorder.PersistDirectBlockSubmissionAsync(Arg.Any<Share>())
            .Returns(new DirectBlockSubmissionPreparation(persistenceError,
                reason));
        TestBitcoinJobManager manager = null;
        var completionObservedSubmission = false;
        recorder.CompleteDirectBlockSubmissionPreparationAsync(
                Arg.Any<Share>(), Arg.Any<DirectBlockSubmissionPreparation>())
            .Returns(_ =>
            {
                completionObservedSubmission = manager.SubmissionCount == 1 &&
                    manager.SubmittedBlockHex == submission.BlockHex;
                return Task.FromException(persistenceError);
            });
        using var container = BuildContainer();
        manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder);
        manager.Configure(CreateDirectPool(), new ClusterConfig());

        var error = await Assert.ThrowsAsync<IOException>(() => manager
            .PersistAndSubmitDirect(CreateDirectCandidate(submission),
                submission.BlockHex, CancellationToken.None));

        Assert.Same(persistenceError, error);
        Assert.True(completionObservedSubmission);
        Assert.Equal(1, manager.SubmissionCount);
    }

    [Fact]
    public async Task DirectReplay_ExactActiveDuplicateCommitsObservedStateAndDoesNotReplayAgain()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        var persisted = new Miningcore.Persistence.Model.Block
        {
            Id = 42,
            PoolId = "btc-direct",
            BlockHeight = 101,
            Status = Miningcore.Persistence.Model.BlockStatus.Pending,
            Type = BitcoinDirectCoinbaseSettlement.BlockType,
            Hash = submission.BlockHash,
            Miner = "miner",
            TransactionConfirmationData = submission.CoinbaseTxId,
            SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
            GrossRewardSatoshis = 5_000_000_000,
            DirectMinerRewardSatoshis = 4_900_000_000,
            DirectMinerScriptPubKey = "0014" + new string('1', 40),
            DirectRecipientOutputs = "[]",
            DirectSubmissionState = BitcoinDirectSubmission.Prepared,
            DirectSubmissionBlock = submission.BlockHex,
            DirectSubmissionAttempts = 0,
            DirectSubmissionDefinitiveMisses = 0,
            Created = DateTime.UtcNow,
        };
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.GetDirectBlockSubmissionsForReplayAsync(
                persisted.PoolId, Arg.Any<long>(), 32,
                Arg.Any<CancellationToken>())
            .Returns(_ => BitcoinDirectSubmission.RequiresReplay(
                    persisted.DirectSubmissionState)
                ? new[] { persisted }
                : Array.Empty<Miningcore.Persistence.Model.Block>());
        BitcoinDirectSubmissionOutcome? recordedOutcome = null;
        recorder.RecordDirectBlockSubmissionAttemptAsync(
                Arg.Any<Share>(), Arg.Any<BitcoinDirectSubmissionOutcome>(),
                Arg.Any<DateTime>())
            .Returns(call =>
            {
                recordedOutcome =
                    call.ArgAt<BitcoinDirectSubmissionOutcome>(1);
                persisted.DirectSubmissionState =
                    BitcoinDirectSubmission.ObservedActive;
                persisted.DirectSubmissionAttempts = 1;
                persisted.DirectSubmissionLastAttempt = DateTime.UtcNow;
                return persisted;
            });
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            DirectSubmissionAccepted = false,
            DirectSubmissionAmbiguous = true,
            DirectSubmissionDuplicate = true,
            DirectSubmissionCoinbaseTx = submission.CoinbaseTxId,
        };
        manager.Configure(CreateDirectPool(), new ClusterConfig());

        await manager.ReplayDirect(CancellationToken.None);
        await manager.ReplayDirect(CancellationToken.None);

        Assert.Equal(BitcoinDirectSubmissionOutcome.ObservedActive,
            recordedOutcome);
        Assert.Equal(BitcoinDirectSubmission.ObservedActive,
            persisted.DirectSubmissionState);
        Assert.Equal(1, manager.SubmissionCount);
        await recorder.Received(1).RecordDirectBlockSubmissionAttemptAsync(
            Arg.Any<Share>(), BitcoinDirectSubmissionOutcome.ObservedActive,
            Arg.Any<DateTime>());
    }

    [Fact]
    public async Task DirectReplay_MalformedRowIsQuarantinedWithoutStoppingValidPeer()
    {
        var submission = BitcoinDirectSubmissionTestData.Create();
        Miningcore.Persistence.Model.Block Create(long id, string hash) =>
            new()
            {
                Id = id,
                PoolId = "btc-direct",
                BlockHeight = checked((ulong) (100 + id)),
                Status = Miningcore.Persistence.Model.BlockStatus.Pending,
                Type = BitcoinDirectCoinbaseSettlement.BlockType,
                Hash = hash,
                Miner = "miner",
                TransactionConfirmationData = submission.CoinbaseTxId,
                SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
                GrossRewardSatoshis = 5_000_000_000,
                DirectMinerRewardSatoshis = 4_900_000_000,
                DirectMinerScriptPubKey = "0014" + new string('1', 40),
                DirectRecipientOutputs = "[]",
                DirectSubmissionState = BitcoinDirectSubmission.Prepared,
                DirectSubmissionBlock = submission.BlockHex,
                DirectSubmissionAttempts = 0,
                DirectSubmissionDefinitiveMisses = 0,
                Created = DateTime.UtcNow,
            };
        var malformed = Create(41, new string('f', 64));
        var valid = Create(42, submission.BlockHash);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.GetDirectBlockSubmissionsForReplayAsync(
                malformed.PoolId, 0, 32, Arg.Any<CancellationToken>())
            .Returns(new[] { malformed, valid });
        recorder.QuarantineDirectBlockSubmissionAsync(malformed.Id,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                malformed.Status =
                    Miningcore.Persistence.Model.BlockStatus.Quarantined;
                malformed.DirectSubmissionState =
                    BitcoinDirectSubmission.Quarantined;
                return malformed;
            });
        recorder.RecordDirectBlockSubmissionAttemptAsync(
                Arg.Any<Share>(), BitcoinDirectSubmissionOutcome.ObservedActive,
                Arg.Any<DateTime>())
            .Returns(valid);
        using var container = BuildContainer();
        var manager = new TestBitcoinJobManager(container,
            MockMasterClock.FromTicks(638010200200475015), new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            DirectSubmissionCoinbaseTx = submission.CoinbaseTxId,
        };
        manager.Configure(CreateDirectPool(), new ClusterConfig());

        await manager.ReplayDirect(CancellationToken.None);

        Assert.Equal(BitcoinDirectSubmission.Quarantined,
            malformed.DirectSubmissionState);
        Assert.Equal(1, manager.SubmissionCount);
        await recorder.Received(1).QuarantineDirectBlockSubmissionAsync(
            malformed.Id, Arg.Any<CancellationToken>());
        await recorder.Received(1).RecordDirectBlockSubmissionAttemptAsync(
            Arg.Is<Share>(share => share.BlockHash == valid.Hash),
            BitcoinDirectSubmissionOutcome.ObservedActive,
            Arg.Any<DateTime>());
    }

    private static PoolConfig CreateDirectPool() => new()
    {
        Id = "btc-direct",
        Template = new BitcoinTemplate
        {
            Family = CoinFamily.Bitcoin,
            Symbol = "BTC",
        },
        Daemons = new[] { new DaemonEndpointConfig() },
    };

    private static Share CreateDirectCandidate(
        (string BlockHex, string BlockHash, string CoinbaseTxId) submission) =>
        new()
        {
            PoolId = "btc-direct",
            Miner = "miner",
            Difficulty = 1,
            NetworkDifficulty = 100,
            IsBlockCandidate = true,
            BlockHeight = 101,
            BlockHash = submission.BlockHash,
            TransactionConfirmationData = submission.CoinbaseTxId,
            SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
            GrossRewardSatoshis = 5_000_000_000,
            DirectMinerRewardSatoshis = 4_900_000_000,
            DirectMinerScriptPubKey = "0014" + new string('1', 40),
            DirectRecipientOutputs = "[]",
            Created = DateTime.UtcNow,
        };

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
            IMessageBus messageBus, IExtraNonceProvider extraNonceProvider,
            IBlockCandidateRecorder recorder = null) :
            base(ctx, clock, messageBus, extraNonceProvider, recorder)
        {
        }

        public Exception DirectSubmissionException { get; init; }
        public bool SubmissionStartedAfterPersistence { get; private set; }
        public Func<bool> PersistenceObserved { get; init; }
        public Exception PreparedCheckpointException { get; init; }
        public Task PreparedCheckpoint { get; init; }
        public string SubmittedBlockHex { get; private set; }
        public int SubmissionCount { get; private set; }
        public bool DirectSubmissionAccepted { get; init; } = true;
        public bool DirectSubmissionAmbiguous { get; init; }
        public bool DirectSubmissionDuplicate { get; init; }
        public string DirectSubmissionCoinbaseTx { get; init; }
        public bool Persisted { get; private set; }
        public Share PersistedCandidate { get; private set; }
        public BitcoinJob Current => currentJob;

        public void PrepareJobConstruction(Network jobNetwork,
            IDestination destination)
        {
            network = jobNetwork;
            poolAddressDestination = destination;
        }

        public void ReplaceBoundPoolConfig(BitcoinPoolConfigExtra config) =>
            extraPoolConfig = config;

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

        public Task ReplayDirect(CancellationToken ct) =>
            ReplayPreparedDirectSubmissionsAsync(ct);

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
            SubmittedBlockHex = blockHex;
            SubmissionCount++;
            SubmissionStartedAfterPersistence =
                PersistenceObserved?.Invoke() == true;

            return DirectSubmissionException != null
                ? Task.FromException<SubmitResult>(DirectSubmissionException)
                : Task.FromResult(new SubmitResult(
                    DirectSubmissionAccepted,
                    DirectSubmissionCoinbaseTx ??
                        share.TransactionConfirmationData,
                    DirectSubmissionAmbiguous,
                    DirectSubmissionDuplicate));
        }

        protected override Task OnDirectSubmissionPreparedAsync(
            Share candidate, CancellationToken ct)
        {
            if(PreparedCheckpointException != null)
                return Task.FromException(PreparedCheckpointException);

            return PreparedCheckpoint ?? Task.CompletedTask;
        }
    }
}
