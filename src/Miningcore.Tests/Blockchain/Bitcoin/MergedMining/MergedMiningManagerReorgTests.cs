using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningManagerReorgTests
{
    public static IEnumerable<object[]> SupportedPayoutPairs()
    {
        var schemes = new[]
        {
            PayoutScheme.SOLO,
            PayoutScheme.PPS,
            PayoutScheme.PROP,
            PayoutScheme.PPLNS,
        };

        return from parent in schemes
            from auxiliary in schemes
            select new object[] { parent, auxiliary };
    }

    public static IEnumerable<object[]> AccountingSubmissionCases()
    {
        foreach(var pair in SupportedPayoutPairs())
        {
            var parent = (PayoutScheme) pair[0];
            var auxiliary = (PayoutScheme) pair[1];

            if(parent != PayoutScheme.SOLO || auxiliary != PayoutScheme.SOLO)
                yield return new object[] { parent, auxiliary, true };
        }

        foreach(var parent in new[]
                {
                    PayoutScheme.PPS,
                    PayoutScheme.PROP,
                    PayoutScheme.PPLNS,
                })
            yield return new object[] { parent, PayoutScheme.SOLO, false };
    }

    [Theory]
    [MemberData(nameof(SupportedPayoutPairs))]
    public void Configure_AcceptsEverySupportedIndependentPayoutPair(
        PayoutScheme parentScheme, PayoutScheme auxiliaryScheme)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var manager = new TestManager(container, Substitute.For<IMasterClock>(),
            new MessageBus(), Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, auxiliary, cluster) = CreateConfig();
        parent.PaymentProcessing.PayoutScheme = parentScheme;
        auxiliary.PaymentProcessing.PayoutScheme = auxiliaryScheme;

        manager.Configure(parent, cluster);
    }

    [Theory]
    [InlineData(PayoutScheme.PPLNSBF)]
    [InlineData(PayoutScheme.PPBS)]
    public void Configure_RejectsUnreviewedMergedMiningPayoutSchemes(
        PayoutScheme scheme)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var manager = new TestManager(container, Substitute.For<IMasterClock>(),
            new MessageBus(), Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig();
        parent.PaymentProcessing.PayoutScheme = scheme;

        var error = Assert.Throws<PoolStartupException>(() =>
            manager.Configure(parent, cluster));

        Assert.Contains("SOLO, PPS, PROP or PPLNS", error.Message);
    }

    [Fact]
    public void Configure_NonSoloAuxiliaryRequiresAddressAttribution()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var manager = new TestManager(container, Substitute.For<IMasterClock>(),
            new MessageBus(), Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, auxiliary, cluster) = CreateConfig();
        auxiliary.PaymentProcessing.PayoutScheme = PayoutScheme.PPLNS;
        ((IDictionary<string, object>) parent.Extra["mergedMining"])
            ["requireAuxAddress"] = false;

        var error = Assert.Throws<PoolStartupException>(() =>
            manager.Configure(parent, cluster));

        Assert.Contains("requireAuxAddress must be true", error.Message);
    }

    [Fact]
    public void StatisticalShare_IsClearedAndKeepsParentBoundaryTimestamp()
    {
        var created = DateTime.UtcNow;
        var candidate = new Share
        {
            PoolId = "ltc-solo",
            Miner = "miner",
            Worker = "worker",
            SessionId = "session",
            Difficulty = 4,
            ShareDifficulty = 5,
            ActualDifficulty = 6,
            NetworkDifficulty = 7,
            BlockHeight = 123,
            IsBlockCandidate = true,
            BlockHash = new string('a', 64),
            BlockType = "merged-parent",
            TransactionConfirmationData = "coinbase",
            Created = created,
        };

        var statistical = MergedMiningBitcoinJobManager.CreateStatisticalShare(candidate);

        Assert.False(statistical.IsBlockCandidate);
        Assert.False(statistical.BlockOnly);
        Assert.Null(statistical.BlockHash);
        Assert.Null(statistical.BlockType);
        Assert.Null(statistical.TransactionConfirmationData);
        Assert.True(statistical.PreserveCreated);
        Assert.Equal(candidate.PoolId, statistical.PoolId);
        Assert.Equal(candidate.Miner, statistical.Miner);
        Assert.Equal(candidate.SessionId, statistical.SessionId);
        Assert.Equal(candidate.Difficulty, statistical.Difficulty);
        Assert.Equal(candidate.NetworkDifficulty, statistical.NetworkDifficulty);
        Assert.Equal(candidate.BlockHeight, statistical.BlockHeight);
        Assert.Equal(created, statistical.Created);

        // Cloning the ordinary record must not mutate the object still owned by the
        // independent parent submission path.
        Assert.True(candidate.IsBlockCandidate);
        Assert.Equal("merged-parent", candidate.BlockType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SoloSoloStatisticalShare_ClearsAccountingEvidenceAndPassesRecorderValidation(
        bool supplyAuxiliaryAddress)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var admission = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Share published = null;
        using var subscription = messageBus.Listen<Share>()
            .Where(x => x != null)
            .Subscribe(x =>
            {
                published = x;
                x.SetPersistenceAdmission(admission.Task);
            });
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        var validated = new Share
        {
            PoolId = parent.Id,
            Miner = "ltc-miner",
            Worker = "rig01",
            Difficulty = 1,
            ShareDifficulty = 1,
            ActualDifficulty = 1,
            NetworkDifficulty = 100,
            // Simulate the partial evidence emitted by v0.2.0 and prove that
            // the manager also defends against another stale producer.
            RewardBasisSatoshis = 625_000_000,
        };
        manager.ProcessMergedShareHandler = () => new MergedMiningShareResult
        {
            Share = validated,
        };
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "merged-admission", false);
        var context = new MergedMiningBitcoinWorkerContext
        {
            Miner = validated.Miner,
            Worker = validated.Worker,
            UserAgent = "test-miner",
            AuxiliaryMiner = supplyAuxiliaryAddress ? "doge-miner" : null,
        };
        var job = TestJob.Create(new BlockTemplate(), new AuxBlockTemplate(),
            "admission-job");
        context.AddJob(job, 4);
        worker.SetContext(context);

        var returned = await manager.SubmitShareAsync(worker,
            new object[] { "ltc-miner.rig01", job.JobId, "00", "00000000", "00000000" },
            CancellationToken.None);

        Assert.Same(validated, returned);
        Assert.NotSame(returned, published);
        Assert.True(returned.StatisticalRecordEmitted);
        Assert.Null(returned.AccountingId);
        Assert.Equal(ShareAccountingRole.None, returned.AccountingRole);
        Assert.Equal(0, returned.RewardBasisSatoshis);
        Assert.Null(returned.PpsCalculatedAmount);
        Assert.Null(returned.PairedShare);
        Assert.Null(published.AccountingId);
        Assert.Equal(ShareAccountingRole.None, published.AccountingRole);
        Assert.Equal(0, published.RewardBasisSatoshis);
        Assert.Null(published.PpsCalculatedAmount);
        Assert.Null(published.PairedShare);
        Assert.Same(published, Assert.Single(
            ShareAccounting.ValidateAndFlatten(published,
                new Dictionary<string, PoolConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    [parent.Id] = parent,
                })));
        Assert.Same(admission.Task, returned.PersistenceAdmission);
        Assert.False(returned.PersistenceAdmission.IsCompleted);
        admission.TrySetResult();
        await returned.PersistenceAdmission;
    }

    [Theory]
    [MemberData(nameof(AccountingSubmissionCases))]
    public async Task MergedAccountingSubmission_PreservesIndependentEvidence(
        PayoutScheme parentScheme, PayoutScheme auxiliaryScheme,
        bool supplyAuxiliaryAddress)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        Share published = null;
        using var subscription = messageBus.Listen<Share>()
            .Where(x => x != null)
            .Subscribe(x => published = x);
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, auxiliary, cluster) = CreateConfig();
        parent.PaymentProcessing.PayoutScheme = parentScheme;
        auxiliary.PaymentProcessing.PayoutScheme = auxiliaryScheme;
        manager.Configure(parent, cluster);
        var validated = new Share
        {
            PoolId = parent.Id,
            Miner = "ltc-miner",
            Worker = "rig01",
            Difficulty = 1,
            ShareDifficulty = 1,
            ActualDifficulty = 1,
            NetworkDifficulty = 100,
            RewardBasisSatoshis = 1,
        };
        var auxiliaryTemplate = new AuxBlockTemplate
        {
            Height = 200,
            CoinbaseValue = 1_000_000_000,
        };
        manager.ProcessMergedShareHandler = () => new MergedMiningShareResult
        {
            Share = validated,
            AuxiliaryBlockTemplate = auxiliaryTemplate,
            AuxiliaryDifficulty = 200,
        };
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "merged-pps-accounting",
            false);
        var context = new MergedMiningBitcoinWorkerContext
        {
            Miner = validated.Miner,
            Worker = validated.Worker,
            UserAgent = "test-miner",
            AuxiliaryMiner = supplyAuxiliaryAddress ? "doge-miner" : null,
        };
        var job = TestJob.Create(new BlockTemplate
            {
                CoinbaseValue = 625_000_000,
            }, auxiliaryTemplate,
            "pps-accounting-job");
        context.AddJob(job, 4);
        worker.SetContext(context);

        var returned = await manager.SubmitShareAsync(worker,
            new object[]
            {
                "ltc-miner.rig01", job.JobId, "00", "00000000", "00000000",
            }, CancellationToken.None);

        Assert.Same(validated, returned);
        Assert.NotNull(published);
        Assert.False(string.IsNullOrEmpty(returned.AccountingId));
        Assert.Equal(supplyAuxiliaryAddress
            ? ShareAccountingRole.Parent
            : ShareAccountingRole.Single, returned.AccountingRole);
        Assert.Equal(625_000_000, returned.RewardBasisSatoshis);
        if(parentScheme == PayoutScheme.PPS)
            Assert.True(returned.PpsCalculatedAmount > 0);
        else
            Assert.Null(returned.PpsCalculatedAmount);
        if(supplyAuxiliaryAddress)
        {
            Assert.NotNull(returned.PairedShare);
            Assert.Equal(ShareAccountingRole.Auxiliary,
                returned.PairedShare.AccountingRole);
            Assert.Equal(1_000_000_000,
                returned.PairedShare.RewardBasisSatoshis);
            Assert.Equal(returned.AccountingId,
                returned.PairedShare.AccountingId);
            if(auxiliaryScheme == PayoutScheme.PPS)
                Assert.True(returned.PairedShare.PpsCalculatedAmount > 0);
            else
                Assert.Null(returned.PairedShare.PpsCalculatedAmount);
        }
        else
            Assert.Null(returned.PairedShare);
        Assert.Equal(returned.AccountingId, published.AccountingId);
        Assert.Equal(returned.RewardBasisSatoshis,
            published.RewardBasisSatoshis);
        Assert.Equal(returned.PpsCalculatedAmount,
            published.PpsCalculatedAmount);
        // The synthetic connection is not backed by a socket, so the manager
        // cannot populate the otherwise-required remote endpoint.
        published.IpAddress = IPAddress.Loopback.ToString();
        if(published.PairedShare != null)
            published.PairedShare.IpAddress = published.IpAddress;
        var pools = new Dictionary<string, PoolConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [parent.Id] = parent,
            [auxiliary.Id] = auxiliary,
        };
        var projections = ShareAccounting.ValidateAndFlatten(published, pools);
        Assert.Same(published, projections[0]);
        if(supplyAuxiliaryAddress)
        {
            Assert.Equal(2, projections.Length);
            Assert.Same(published.PairedShare, projections[1]);
        }
        else
            Assert.Single(projections);

        foreach(var projection in projections)
        {
            var credit = ShareAccounting.CreatePpsCredit(
                pools[projection.PoolId], projection);
            if(pools[projection.PoolId].PaymentProcessing.PayoutScheme ==
               PayoutScheme.PPS)
            {
                Assert.NotNull(credit);
                Assert.Equal(projection.PpsCalculatedAmount,
                    credit.CalculatedAmount);
                Assert.Equal(projection.AccountingId,
                    credit.AccountingId.ToString("N"));
            }
            else
                Assert.Null(credit);
        }
    }

    [Fact]
    public async Task CandidatePersistence_SurvivesPpsEvidenceConstructionFailure()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.PersistBlockCandidateAsync(Arg.Any<Share>())
            .Returns(Task.CompletedTask);
        var manager = new TestManager(container, clock, new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder);
        var (parent, _, cluster) = CreateConfig();
        parent.PaymentProcessing.PayoutScheme = PayoutScheme.PPS;
        manager.Configure(parent, cluster);
        var candidate = new Share
        {
            PoolId = parent.Id,
            Miner = "ltc-miner",
            Worker = "rig01",
            Difficulty = double.MaxValue,
            NetworkDifficulty = 1,
            RewardBasisSatoshis = 625_000_000,
            BlockHeight = 123,
            BlockHash = new string('b', 64),
            IsBlockCandidate = true,
        };
        manager.ProcessMergedShareHandler = () => new MergedMiningShareResult
        {
            Share = candidate,
            ParentBlockHex = "parent-block",
        };
        manager.SubmitCandidatePathsHandler = async _ =>
        {
            await recorder.PersistBlockCandidateAsync(new Share
            {
                PoolId = parent.Id,
                BlockHash = candidate.BlockHash,
                BlockType = "merged-parent",
                BlockOnly = true,
                IsBlockCandidate = true,
            });
            return new[] { true };
        };
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "pps-evidence-failure",
            false);
        var context = new MergedMiningBitcoinWorkerContext
        {
            Miner = candidate.Miner,
            Worker = candidate.Worker,
            UserAgent = "test-miner",
        };
        var job = TestJob.Create(new BlockTemplate
            {
                CoinbaseValue = 625_000_000,
            }, new AuxBlockTemplate(),
            "pps-evidence-job");
        context.AddJob(job, 4);
        worker.SetContext(context);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.SubmitShareAsync(worker, new object[]
            {
                "ltc-miner.rig01", job.JobId, "00", "00000000", "00000000",
            }, CancellationToken.None).AsTask());
        Assert.Contains("exceeds the supported decimal accounting range",
            error.Message);

        await recorder.Received(1).PersistBlockCandidateAsync(
            Arg.Is<Share>(x => x.BlockOnly && x.IsBlockCandidate &&
                x.BlockHash == candidate.BlockHash));
    }

    [Fact]
    public void FirstValidStatisticalShare_CreatesAndCopiesWorkerSession()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var poolId = $"ltc-solo-{suffix}";
        var context = new MergedMiningBitcoinWorkerContext
        {
            Miner = $"miner-{suffix}",
            Worker = "rig01",
            SessionId = null,
        };
        var returnedShare = new Share
        {
            PoolId = poolId,
            Miner = context.Miner,
            Worker = context.Worker,
        };
        var now = DateTime.UtcNow;

        var sessionId = MergedMiningBitcoinJobManager.EnsureStatisticalShareSession(
            context, returnedShare, poolId, "127.0.0.1", now);
        var emittedStatisticalShare =
            MergedMiningBitcoinJobManager.CreateStatisticalShare(returnedShare);

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(sessionId, returnedShare.SessionId);
        Assert.Equal(sessionId, emittedStatisticalShare.SessionId);
        Assert.Equal(sessionId, WorkerSessionTracker.GetCurrentSessionId(
            poolId, context.Miner, context.Worker, now));
    }

    [Fact]
    public async Task UnclassifiableParentSubmission_PersistsUncertainBlockRecord()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.PersistBlockCandidateAsync(Arg.Any<Share>())
            .Returns(Task.CompletedTask);
        var manager = new TestManager(container, clock, new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            ParentSubmissionException = new InvalidDataException(
                "JSON-RPC batch response IDs were malformed"),
        };
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        var candidate = new Share
        {
            PoolId = parent.Id,
            Miner = "ltc-miner",
            Worker = "rig01",
            BlockHeight = 456,
            BlockHash = new string('a', 64),
            IsBlockCandidate = true,
            Created = DateTime.UtcNow,
        };

        var accepted = await manager.SubmitAndPersistParentBlockAsync(candidate,
            "parent-block", CancellationToken.None);

        Assert.False(accepted);
        Assert.True(candidate.BlockRecordEmitted);
        Assert.False(candidate.IsBlockCandidate);
        Assert.Null(candidate.BlockType);
        await recorder.Received(1).PersistBlockCandidateAsync(Arg.Is<Share>(x =>
            x.BlockOnly && x.IsBlockCandidate &&
            x.BlockType == "merged-parent-uncertain" &&
            x.BlockHash == candidate.BlockHash &&
            x.TransactionConfirmationData.StartsWith("parent-uncertain:")));
    }

    [Fact]
    public async Task StratumParentSubmissionFailure_IsNotConvertedToUncertainRecord()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        var manager = new TestManager(container, clock, new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder)
        {
            ParentSubmissionException = new StratumException(StratumError.Other,
                "invalid candidate"),
        };
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);
        var candidate = new Share
        {
            PoolId = parent.Id,
            BlockHeight = 457,
            BlockHash = new string('b', 64),
            IsBlockCandidate = true,
            Created = DateTime.UtcNow,
        };

        await Assert.ThrowsAsync<StratumException>(() =>
            manager.SubmitAndPersistParentBlockAsync(candidate, "parent-block",
                CancellationToken.None));
        await recorder.DidNotReceive().PersistBlockCandidateAsync(Arg.Any<Share>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubmissionFailure_DrainsSuccessfulPeerBeforeRethrow(
        bool parentFails)
    {
        var peerPersisted = false;
        var failed = Task.FromException<bool>(
            new IOException(parentFails ? "parent persistence failed" :
                "auxiliary persistence failed"));
        var successful = Task.Run(async () =>
        {
            await Task.Delay(25);
            peerPersisted = true;
            return true;
        });
        var submissions = parentFails
            ? new[] { failed, successful }
            : new[] { successful, failed };

        await Assert.ThrowsAsync<IOException>(() =>
            MergedMiningBitcoinJobManager.DrainSubmissionTasksAsync(submissions));

        Assert.True(peerPersisted);
    }

    [Fact]
    public async Task SubmissionTimeout_DrainsAcceptedPeerBeforeRethrow()
    {
        var peerPersisted = false;
        var timedOut = Task.FromException<bool>(new TimeoutException("RPC timeout"));
        var accepted = Task.Run(async () =>
        {
            await Task.Delay(25);
            peerPersisted = true;
            return true;
        });

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MergedMiningBitcoinJobManager.DrainSubmissionTasksAsync(
                new[] { timedOut, accepted }));

        Assert.True(peerPersisted);
    }

    [Fact]
    public async Task BothSubmissionFailures_AreReportedAfterBothDrain()
    {
        var parent = Task.FromException<bool>(new IOException("parent failed"));
        var auxiliary = Task.FromException<bool>(new TimeoutException("DOGE failed"));

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            MergedMiningBitcoinJobManager.DrainSubmissionTasksAsync(
                new[] { parent, auxiliary }));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, x => x is IOException);
        Assert.Contains(error.InnerExceptions, x => x is TimeoutException);
    }

    [Fact]
    public async Task AuxiliaryRefreshTimeout_RetainsCacheAndPublishesDegradedState()
    {
        await using var server = new HangingJsonRpcServer();
        // This deadline includes runner scheduling and loopback connection setup. A
        // one-second budget proved flaky on loaded Linux CI before the server could
        // observe the request, so keep a generous admission margin here.
        var rpcTimeout = TimeSpan.FromSeconds(5);
        var testTimeout = TimeSpan.FromSeconds(10);
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port,
            (int) rpcTimeout.TotalMilliseconds);
        manager.Configure(parent, cluster);
        var parentTemplate = CreateParentTemplate();
        var cachedAuxiliary = CreateAuxiliaryTemplate();
        manager.Seed(parentTemplate, cachedAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        AuxiliaryTemplateRpcTelemetryEvent rpcTelemetry = null;
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var rpcSubscription = messageBus
            .Listen<AuxiliaryTemplateRpcTelemetryEvent>()
            .Subscribe(x => rpcTelemetry = x);
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        var update = manager.Update(CancellationToken.None);
        await server.RequestReceived.Task.WaitAsync(testTimeout);
        await update.WaitAsync(testTimeout);

        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.NotNull(rpcTelemetry);
        Assert.Equal(parent.Id, rpcTelemetry.ParentPoolId);
        Assert.Equal("doge-solo", rpcTelemetry.AuxiliaryPoolId);
        Assert.Equal(AuxiliaryTemplateRpcPhase.Refresh, rpcTelemetry.Phase);
        Assert.Equal(AuxiliaryTemplateRpcOutcome.Timeout, rpcTelemetry.Outcome);
        var stateTelemetry = Assert.Single(stateEvents);
        Assert.NotNull(stateTelemetry);
        Assert.Equal(parent.Id, stateTelemetry.ParentPoolId);
        Assert.Equal("doge-solo", stateTelemetry.AuxiliaryPoolId);
        Assert.True(stateTelemetry.Available);
        Assert.True(stateTelemetry.Degraded);
        Assert.True(stateTelemetry.FallbackStarted);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(testTimeout);

        // The stopped listener makes this second refresh fail quickly by connection
        // refusal. Level-triggered gauge state is reasserted, while the counter still
        // represents fallback episodes rather than every cached refresh.
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.False(stateEvents[1].FallbackStarted);
    }

    [Fact]
    public async Task AuxiliaryRefreshWithoutCache_PublishesUnavailableState()
    {
        var unavailableEndpoint = new HangingJsonRpcServer();
        var port = unavailableEndpoint.Port;
        await unavailableEndpoint.DisposeAsync();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(port, 5000);
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), null);
        manager.Enqueue(CreateParentTemplate());
        AuxiliaryTemplateStateTelemetryEvent stateTelemetry = null;
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(x => stateTelemetry = x);

        await manager.Update(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(stateTelemetry);
        Assert.Equal(parent.Id, stateTelemetry.ParentPoolId);
        Assert.Equal("doge-solo", stateTelemetry.AuxiliaryPoolId);
        Assert.False(stateTelemetry.Available);
        Assert.False(stateTelemetry.Degraded);
        Assert.False(stateTelemetry.FallbackStarted);
    }

    [Fact]
    public async Task AuxiliaryRefreshHostCancellation_DoesNotEnterFallbackState()
    {
        await using var server = new HangingJsonRpcServer();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port, 10_000);
        manager.Configure(parent, cluster);
        var parentTemplate = CreateParentTemplate();
        var cachedAuxiliary = CreateAuxiliaryTemplate();
        manager.Seed(parentTemplate, cachedAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        AuxiliaryTemplateRpcTelemetryEvent rpcTelemetry = null;
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var rpcSubscription = messageBus
            .Listen<AuxiliaryTemplateRpcTelemetryEvent>()
            .Subscribe(x => rpcTelemetry = x);
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);
        using var shutdown = new CancellationTokenSource();

        var update = manager.Update(shutdown.Token);
        await server.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        shutdown.Cancel();
        await update.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.NotNull(rpcTelemetry);
        Assert.Equal(parent.Id, rpcTelemetry.ParentPoolId);
        Assert.Equal("doge-solo", rpcTelemetry.AuxiliaryPoolId);
        Assert.Equal(AuxiliaryTemplateRpcPhase.Refresh, rpcTelemetry.Phase);
        Assert.Equal(AuxiliaryTemplateRpcOutcome.Cancellation,
            rpcTelemetry.Outcome);
        Assert.Empty(stateEvents);
    }

    [Fact]
    public async Task AuxiliaryRefresh_PreCancelledHostToken_DoesNotStartAuxiliaryRpc()
    {
        await using var server = new HangingJsonRpcServer();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port, 10_000);
        manager.Configure(parent, cluster);
        var cachedAuxiliary = CreateAuxiliaryTemplate();
        manager.Seed(CreateParentTemplate(), cachedAuxiliary);
        var cachedJob = manager.Current;
        manager.Enqueue(CreateParentTemplate());
        var rpcEvents = new List<AuxiliaryTemplateRpcTelemetryEvent>();
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var rpcSubscription = messageBus
            .Listen<AuxiliaryTemplateRpcTelemetryEvent>()
            .Subscribe(rpcEvents.Add);
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        var result = await manager.Update(shutdown.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.False(server.RequestReceived.Task.IsCompleted);
        Assert.Empty(rpcEvents);
        Assert.Empty(stateEvents);
        Assert.Same(cachedJob, manager.Current);
        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
    }

    [Fact]
    public async Task FreshAuxiliaryTemplate_RecoversOnlyAfterReplacementJobIsInstalled()
    {
        var cachedAuxiliary = CreateAuxiliaryTemplate();
        var freshAuxiliary = CreateAuxiliaryTemplate();
        freshAuxiliary.Height++;
        freshAuxiliary.Hash = new string('c', 64);
        freshAuxiliary.PreviousBlockhash = cachedAuxiliary.Hash;
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "daemon unavailable"),
            SequenceJsonRpcServer.Success(freshAuxiliary),
            SequenceJsonRpcServer.RpcError(-1, "daemon unavailable"));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        var parentTemplate = CreateParentTemplate();
        manager.Seed(parentTemplate, cachedAuxiliary);
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var fallback = Assert.Single(stateEvents);
        Assert.True(fallback.Available);
        Assert.True(fallback.Degraded);
        Assert.True(fallback.FallbackStarted);

        manager.JobCreationException = new InvalidOperationException(
            "fresh auxiliary template is unusable");
        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.False(stateEvents[1].FallbackStarted);

        manager.JobCreationException = null;
        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, stateEvents.Count);
        Assert.True(stateEvents[2].Available);
        Assert.True(stateEvents[2].Degraded);
        Assert.False(stateEvents[2].FallbackStarted);
    }

    [Fact]
    public async Task FreshActiveIdentity_ReconfirmedWhileParentJobInitializationFails_RecoversDegradedState()
    {
        var activeAuxiliary = CreateAuxiliaryTemplate();
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "daemon unavailable"),
            SequenceJsonRpcServer.Success(activeAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        var activeParent = CreateParentTemplate();
        manager.Seed(activeParent, activeAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var fallback = Assert.Single(stateEvents);
        Assert.True(fallback.Available);
        Assert.True(fallback.Degraded);
        Assert.True(fallback.FallbackStarted);

        var newParent = CreateParentTemplate();
        newParent.Height++;
        newParent.PreviousBlockhash = new string('2', 64);
        manager.JobCreationException = new InvalidOperationException(
            "parent job cannot initialize");
        manager.Enqueue(newParent);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(activeParent, manager.Current.BlockTemplate);
        Assert.Same(activeAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.False(stateEvents[1].Degraded);
        Assert.False(stateEvents[1].FallbackStarted);
    }

    [Fact]
    public async Task AuxiliaryJobCreationCancellation_DoesNotPublishTemplateState()
    {
        var activeAuxiliary = CreateAuxiliaryTemplate();
        var freshAuxiliary = CreateAuxiliaryTemplate();
        freshAuxiliary.Height++;
        freshAuxiliary.Hash = new string('c', 64);
        freshAuxiliary.PreviousBlockhash = activeAuxiliary.Hash;
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(freshAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>())
        {
            JobCreationException = new OperationCanceledException(
                "job creation cancelled during shutdown"),
        };
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), activeAuxiliary);
        var activeJob = manager.Current;
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        var result = await manager.Update(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsNew);
        Assert.False(result.Force);
        Assert.Empty(stateEvents);
        Assert.Same(activeJob, manager.Current);
        Assert.Same(activeAuxiliary, manager.Current.AuxiliaryBlockTemplate);
    }

    [Fact]
    public async Task FreshAuxiliaryTemplate_ThatCannotReplaceHealthyJob_EntersFallback()
    {
        var activeAuxiliary = CreateAuxiliaryTemplate();
        var freshAuxiliary = CreateAuxiliaryTemplate();
        freshAuxiliary.Height++;
        freshAuxiliary.Hash = new string('c', 64);
        freshAuxiliary.PreviousBlockhash = activeAuxiliary.Hash;
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(freshAuxiliary),
            SequenceJsonRpcServer.Success(freshAuxiliary),
            SequenceJsonRpcServer.Success(freshAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>())
        {
            JobCreationException = new InvalidOperationException(
                "fresh auxiliary template is unusable"),
        };
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), activeAuxiliary);
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(activeAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        var fallback = Assert.Single(stateEvents);
        Assert.True(fallback.Available);
        Assert.True(fallback.Degraded);
        Assert.True(fallback.FallbackStarted);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(activeAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.False(stateEvents[1].FallbackStarted);

        manager.JobCreationException = null;
        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotSame(activeAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(freshAuxiliary.Height,
            manager.Current.AuxiliaryBlockTemplate.Height);
        Assert.Equal(freshAuxiliary.Hash,
            manager.Current.AuxiliaryBlockTemplate.Hash);
        Assert.Equal(freshAuxiliary.PreviousBlockhash,
            manager.Current.AuxiliaryBlockTemplate.PreviousBlockhash);
        Assert.Equal(3, stateEvents.Count);
        Assert.True(stateEvents[2].Available);
        Assert.False(stateEvents[2].Degraded);
        Assert.False(stateEvents[2].FallbackStarted);
    }

    [Fact]
    public async Task FreshAuxiliaryTemplate_ThatCannotInitialize_RemainsUnavailable()
    {
        var freshAuxiliary = CreateAuxiliaryTemplate();
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(freshAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>())
        {
            JobCreationException = new InvalidOperationException(
                "initial auxiliary template is unusable"),
        };
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.Seed(CreateParentTemplate(), null);
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var unavailable = Assert.Single(stateEvents);
        Assert.False(unavailable.Available);
        Assert.False(unavailable.Degraded);
        Assert.False(unavailable.FallbackStarted);
        Assert.Null(manager.Current.AuxiliaryBlockTemplate);
    }

    [Fact]
    public async Task StartupTemplate_InstalledByBlockTemplateStream_PublishesAvailableImmediately()
    {
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "daemon unavailable"));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        var startupAuxiliary = CreateAuxiliaryTemplate();
        manager.SeedStartup(startupAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None,
            JobRefreshBy.BlockTemplateStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(startupAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        var available = Assert.Single(stateEvents);
        Assert.True(available.Available);
        Assert.False(available.Degraded);
        Assert.False(available.FallbackStarted);
        Assert.Null(manager.StartupAuxiliaryTemplate);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.True(stateEvents[1].FallbackStarted);

        manager.Enqueue(CreateParentTemplate());
        await manager.Update(CancellationToken.None,
            JobRefreshBy.BlockTemplateStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, stateEvents.Count);
        Assert.Same(startupAuxiliary, manager.Current.AuxiliaryBlockTemplate);
    }

    [Fact]
    public async Task StartupTemplate_InstalledAfterFreshInitializationFailure_PublishesFallback()
    {
        var startupAuxiliary = CreateAuxiliaryTemplate();
        var freshAuxiliary = CreateAuxiliaryTemplate();
        freshAuxiliary.Height++;
        freshAuxiliary.Hash = new string('c', 64);
        freshAuxiliary.PreviousBlockhash = new string('d', 64);
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(freshAuxiliary),
            SequenceJsonRpcServer.Success(freshAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.SeedStartup(startupAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        manager.Enqueue(CreateParentTemplate());
        manager.Enqueue(CreateParentTemplate());
        var rejectFreshTemplate = true;
        manager.JobCreationExceptionFactory = auxiliaryTemplate =>
            rejectFreshTemplate && string.Equals(auxiliaryTemplate.Hash,
                freshAuxiliary.Hash, StringComparison.OrdinalIgnoreCase)
                ? new InvalidOperationException("fresh template cannot initialize")
                : null;
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var unavailable = Assert.Single(stateEvents);
        Assert.False(unavailable.Available);
        Assert.False(unavailable.Degraded);
        Assert.False(unavailable.FallbackStarted);
        Assert.Null(manager.Current);

        await manager.Update(CancellationToken.None,
            JobRefreshBy.BlockTemplateStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(startupAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.True(stateEvents[1].FallbackStarted);
        Assert.Null(manager.StartupAuxiliaryTemplate);

        rejectFreshTemplate = false;
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(freshAuxiliary.Height,
            manager.Current.AuxiliaryBlockTemplate.Height);
        Assert.Equal(freshAuxiliary.Hash,
            manager.Current.AuxiliaryBlockTemplate.Hash);
        Assert.Equal(3, stateEvents.Count);
        Assert.True(stateEvents[2].Available);
        Assert.False(stateEvents[2].Degraded);
        Assert.False(stateEvents[2].FallbackStarted);
    }

    [Fact]
    public async Task StartupCache_RefreshFailureAndFailedFirstJob_ThenStreamInstall_EntersFallback()
    {
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.RpcError(-1, "daemon unavailable"));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        var startupAuxiliary = CreateAuxiliaryTemplate();
        manager.SeedStartup(startupAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        manager.Enqueue(CreateParentTemplate());
        var rejectFirstJob = true;
        manager.JobCreationExceptionFactory = _ =>
        {
            if(!rejectFirstJob)
                return null;

            rejectFirstJob = false;
            return new InvalidOperationException("first job cannot initialize");
        };
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        var unavailable = Assert.Single(stateEvents);
        Assert.False(unavailable.Available);
        Assert.False(unavailable.Degraded);
        Assert.False(unavailable.FallbackStarted);
        Assert.Null(manager.Current);

        await manager.Update(CancellationToken.None,
            JobRefreshBy.BlockTemplateStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(startupAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(2, stateEvents.Count);
        Assert.True(stateEvents[1].Available);
        Assert.True(stateEvents[1].Degraded);
        Assert.True(stateEvents[1].FallbackStarted);
        Assert.Null(manager.StartupAuxiliaryTemplate);
    }

    [Fact]
    public async Task StartupCache_FreshIdentityReconfirmedAfterStaleFailure_ThenStreamInstall_IsHealthy()
    {
        var startupAuxiliary = CreateAuxiliaryTemplate();
        var supersedingAuxiliary = CreateAuxiliaryTemplate();
        supersedingAuxiliary.Height++;
        supersedingAuxiliary.Hash = new string('c', 64);
        supersedingAuxiliary.PreviousBlockhash = new string('d', 64);
        await using var server = new SequenceJsonRpcServer(
            SequenceJsonRpcServer.Success(supersedingAuxiliary),
            SequenceJsonRpcServer.Success(startupAuxiliary));
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(),
            Substitute.For<IBlockCandidateRecorder>());
        var (parent, _, cluster) = CreateConfig(server.Port);
        manager.Configure(parent, cluster);
        manager.SeedStartup(startupAuxiliary);
        manager.Enqueue(CreateParentTemplate());
        manager.Enqueue(CreateParentTemplate());
        manager.Enqueue(CreateParentTemplate());
        var remainingFailures = 2;
        manager.JobCreationExceptionFactory = _ => remainingFailures-- > 0
            ? new InvalidOperationException("job cannot initialize")
            : null;
        var stateEvents = new List<AuxiliaryTemplateStateTelemetryEvent>();
        using var stateSubscription = messageBus
            .Listen<AuxiliaryTemplateStateTelemetryEvent>()
            .Subscribe(stateEvents.Add);

        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await manager.Update(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, stateEvents.Count);
        Assert.All(stateEvents, state =>
        {
            Assert.False(state.Available);
            Assert.False(state.Degraded);
            Assert.False(state.FallbackStarted);
        });
        Assert.Null(manager.Current);

        await manager.Update(CancellationToken.None,
            JobRefreshBy.BlockTemplateStream).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(startupAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal(3, stateEvents.Count);
        Assert.True(stateEvents[2].Available);
        Assert.False(stateEvents[2].Degraded);
        Assert.False(stateEvents[2].FallbackStarted);
        Assert.Null(manager.StartupAuxiliaryTemplate);
    }

    [Fact]
    public async Task MinerEof_DoesNotCancelManagerOwnedCandidatePersistence()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.PersistBlockCandidateAsync(Arg.Any<Share>())
            .Returns(Task.CompletedTask);
        var manager = new TestManager(container, clock, new MessageBus(),
            Substitute.For<IExtraNonceProvider>(), recorder);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var listenerEndpoint = (IPEndPoint) listener.LocalEndpoint;
        using var client = new TcpClient();
        var acceptTask = listener.AcceptSocketAsync();
        await client.ConnectAsync(listenerEndpoint.Address, listenerEndpoint.Port);
        using var serverSocket = await acceptTask;
        listener.Stop();

        var rpcStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRegistered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRpc = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionClosed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new Share
        {
            PoolId = parent.Id,
            BlockHash = new string('a', 64),
            BlockOnly = true,
            IsBlockCandidate = true,
        };
        Task<bool[]> candidateOperation = null;
        CancellationToken requestToken = default;
        CancellationToken operationToken = default;

        Task HandleRequestAsync(StratumConnection _, JsonRpcRequest request,
            CancellationToken ct)
        {
            Assert.Equal("mining.submit", request.Method);
            requestToken = ct;
            var preparation = manager.BeginCandidatePreparation();
            candidateOperation = manager.StartCandidateOperationAsync(async ownedToken =>
            {
                operationToken = ownedToken;
                rpcStarted.TrySetResult(true);
                await releaseRpc.Task.WaitAsync(ownedToken);
                await recorder.PersistBlockCandidateAsync(candidate);
                return new[] { true };
            }, preparation);
            operationRegistered.TrySetResult(true);
            return candidateOperation;
        }

        var connection = new StratumConnection(
            new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "candidate-eof", false);
        var dispatch = connection.DispatchAsync(serverSocket, CancellationToken.None,
            new StratumEndpoint(listenerEndpoint, new PoolEndpoint()),
            (IPEndPoint) client.Client.LocalEndPoint, null, HandleRequestAsync,
            _ => connectionClosed.TrySetResult(null),
            (_, ex) => connectionClosed.TrySetResult(ex));

        var requestBytes = Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"mining.submit\",\"params\":[]}\n");
        await client.GetStream().WriteAsync(requestBytes);
        await client.GetStream().FlushAsync();
        await rpcStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await operationRegistered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        client.Close();
        Assert.True(SpinWait.SpinUntil(() => requestToken.IsCancellationRequested,
            TimeSpan.FromSeconds(2)));
        Assert.False(dispatch.IsCompleted);
        Assert.False(connectionClosed.Task.IsCompleted);
        Assert.True(requestToken.IsCancellationRequested);
        Assert.False(operationToken.IsCancellationRequested);
        Assert.False(candidateOperation.IsCompleted);
        var shutdownDrain = manager.DrainCandidateOperationsAsync();
        Assert.False(shutdownDrain.IsCompleted);

        releaseRpc.TrySetResult(true);
        Assert.Equal(new[] { true },
            await candidateOperation.WaitAsync(TimeSpan.FromSeconds(2)));
        await shutdownDrain.WaitAsync(TimeSpan.FromSeconds(2));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
        var dispatchError = await connectionClosed.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.Null(dispatchError);
        await recorder.Received(1).PersistBlockCandidateAsync(candidate);
    }

    [Fact]
    public async Task HostShutdown_DrainsValidationBeforeCandidateRegistrationAndPersistence()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        recorder.PersistBlockCandidateAsync(Arg.Any<Share>())
            .Returns(Task.CompletedTask);
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(), recorder);
        var (parent, _, cluster) = CreateConfig();
        manager.Configure(parent, cluster);

        using var coordinationDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var validationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseValidation = new ManualResetEventSlim();
        var candidateSubmissionStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var candidateShare = new Share
        {
            BlockHeight = 321,
            BlockHash = new string('c', 64),
            IsBlockCandidate = true,
            Difficulty = 1,
            NetworkDifficulty = 1,
        };

        manager.ProcessMergedShareHandler = () =>
        {
            validationStarted.TrySetResult(true);
            releaseValidation.Wait();
            return new MergedMiningShareResult
            {
                Share = candidateShare,
                ParentBlockHex = "parent-block",
                ParentHeaderHex = new string('d', 160),
            };
        };
        manager.SubmitCandidatePathsHandler = async operationToken =>
        {
            candidateSubmissionStarted.TrySetResult(true);
            await releasePersistence.Task.WaitAsync(operationToken);
            await recorder.PersistBlockCandidateAsync(new Share
            {
                PoolId = parent.Id,
                BlockHash = candidateShare.BlockHash,
                BlockOnly = true,
                IsBlockCandidate = true,
            });
            persisted.TrySetResult(true);
            return new[] { true };
        };

        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            new RecyclableMemoryStreamManager(), clock, "shutdown-validation", false);
        var context = new MergedMiningBitcoinWorkerContext
        {
            Miner = "ltc-miner",
            Worker = "rig01",
            UserAgent = "test-miner",
        };
        var job = TestJob.Create(new BlockTemplate(), new AuxBlockTemplate(),
            "shutdown-job");
        context.AddJob(job, 4);
        worker.SetContext(context);
        using var hostShutdown = new CancellationTokenSource();

        // Validation deliberately blocks synchronously. Give it a dedicated worker so a
        // saturated shared thread pool cannot prevent the test from reaching its first signal.
        var submitTask = Task.Factory.StartNew(
                () => manager.SubmitShareAsync(worker,
                    new object[]
                    {
                        "ltc-miner.rig01", job.JobId, "00", "00000000", "00000000",
                    }, hostShutdown.Token).AsTask(),
                CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();

        try
        {
            await WaitForPhaseOrSubmissionAsync(validationStarted.Task,
                "merged-share validation to start", submitTask,
                coordinationDeadline.Token);

            hostShutdown.Cancel();
            var shutdownDrain = manager.DrainCandidateOperationsAsync();
            Assert.False(shutdownDrain.IsCompleted);

            releaseValidation.Set();
            await WaitForPhaseOrSubmissionAsync(candidateSubmissionStarted.Task,
                "candidate submission to start", submitTask,
                coordinationDeadline.Token);
            Assert.False(shutdownDrain.IsCompleted);
            Assert.False(persisted.Task.IsCompleted);

            releasePersistence.TrySetResult(true);
            // Manager-owned persistence may outlive the initiating submission task, so
            // unlike the two synchronous startup phases this phase must not race submitTask.
            await WaitForCoordinationPhaseAsync(persisted.Task,
                "candidate persistence to complete", coordinationDeadline.Token);
            await WaitForCoordinationPhaseAsync(shutdownDrain,
                "candidate-operation drain to complete", coordinationDeadline.Token);
            var returnedShare = await WaitForCoordinationPhaseAsync(submitTask,
                "share submission to complete", coordinationDeadline.Token);

            Assert.Same(candidateShare, returnedShare);
            await recorder.Received(1).PersistBlockCandidateAsync(
                Arg.Is<Share>(x => x.BlockOnly &&
                    x.BlockHash == candidateShare.BlockHash));
            Assert.Throws<OperationCanceledException>(() =>
                manager.BeginCandidatePreparation());
        }
        finally
        {
            releaseValidation.Set();
            releasePersistence.TrySetResult(true);
            hostShutdown.Cancel();

            using var cleanupDeadline = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

            try
            {
                await submitTask.WaitAsync(cleanupDeadline.Token);
            }
            catch(OperationCanceledException) when(cleanupDeadline.IsCancellationRequested &&
                !submitTask.IsCompleted)
            {
                // Bound cleanup after releasing both artificial gates.
            }
            catch
            {
                // Observe a secondary submission fault without replacing the primary failure.
            }
        }
    }

    [Fact]
    public async Task LowerHeightAuthoritativeTemplate_ReplacesJobAndEmitsCleanParams()
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        using var container = builder.Build();

        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var messageBus = new MessageBus();
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        var manager = new TestManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>(), recorder);
        var (parent, auxiliary, cluster) = CreateConfig();
        manager.Configure(parent, cluster);

        var cachedAuxiliary = new AuxBlockTemplate
        {
            Height = 220,
            Hash = new string('a', 64),
            PreviousBlockhash = new string('b', 64),
            Bits = "207fffff",
        };
        manager.Seed(new BlockTemplate
        {
            Height = 102,
            PreviousBlockhash = new string('1', 64),
        }, cachedAuxiliary);
        manager.Enqueue(new BlockTemplate
        {
            Height = 101,
            PreviousBlockhash = new string('2', 64),
            Target = new string('f', 64),
            Bits = "207fffff",
        });

        NewChainHeightNotification heightNotification = null;
        using var heightSubscription = messageBus.Listen<NewChainHeightNotification>()
            .Subscribe(x => heightNotification = x);
        using var shutdown = new CancellationTokenSource();
        manager.InitializeJobUpdates(shutdown.Token);
        var emittedJob = new TaskCompletionSource<object>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var jobSubscription = manager.Jobs.Take(1)
            .Subscribe(x => emittedJob.TrySetResult(x));

        messageBus.SendMessage(new BtStreamMessage("ltc-templates", "ignored queued snapshot",
            clock.Now, clock.Now));
        var parameters = (object[]) await emittedJob.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal((uint) 101, manager.Current.BlockTemplate.Height);
        Assert.Equal(new string('2', 64), manager.Current.BlockTemplate.PreviousBlockhash);
        Assert.Same(cachedAuxiliary, manager.Current.AuxiliaryBlockTemplate);
        Assert.Equal((uint) 101, manager.BlockchainStats.BlockHeight);
        Assert.True((bool) parameters[^1]);
        Assert.Equal(parent.Id, heightNotification?.PoolId);
        Assert.Equal((ulong) 101, heightNotification?.BlockHeight);
    }

    private static async Task WaitForPhaseOrSubmissionAsync(Task phaseTask,
        string phase, Task<Share> submitTask, CancellationToken deadline)
    {
        var completedTask = await WaitForCoordinationPhaseAsync(
            Task.WhenAny(phaseTask, submitTask), phase, deadline);

        if(ReferenceEquals(completedTask, submitTask))
        {
            await submitTask;
            throw new InvalidOperationException(
                $"Share submission completed before {phase}");
        }

        await WaitForCoordinationPhaseAsync(phaseTask, phase, deadline);
    }

    private static async Task WaitForCoordinationPhaseAsync(Task task,
        string phase, CancellationToken deadline)
    {
        var deadlineTask = Task.Delay(Timeout.InfiniteTimeSpan, deadline);

        if(!ReferenceEquals(await Task.WhenAny(task, deadlineTask), task))
            throw new TimeoutException($"Timed out waiting for {phase}");

        await task;
    }

    private static async Task<T> WaitForCoordinationPhaseAsync<T>(Task<T> task,
        string phase, CancellationToken deadline)
    {
        await WaitForCoordinationPhaseAsync((Task) task, phase, deadline);
        return await task;
    }

    private static BlockTemplate CreateParentTemplate() => new()
    {
        Height = 102,
        PreviousBlockhash = new string('1', 64),
        Target = new string('f', 64),
        Bits = "207fffff",
    };

    private static AuxBlockTemplate CreateAuxiliaryTemplate() => new()
    {
        Height = 220,
        Hash = new string('a', 64),
        PreviousBlockhash = new string('b', 64),
        Bits = "207fffff",
    };

    private static (PoolConfig Parent, PoolConfig Auxiliary, ClusterConfig Cluster) CreateConfig(
        int auxiliaryRpcPort = 44555, int auxiliaryTemplatePollTimeoutMs = 500)
    {
        var parent = new PoolConfig
        {
            Id = "ltc-solo",
            Coin = "litecoin",
            Enabled = true,
            Address = "mipcBbFg9gMiCh81Kj8tqqdgoZub1ZJRfn",
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 19332 } },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Template = new BitcoinTemplate
            {
                Symbol = "LTC",
                Name = "Litecoin",
                Family = CoinFamily.Bitcoin,
            },
            Extra = new Dictionary<string, object>
            {
                ["btStream"] = new Dictionary<string, object>
                {
                    ["url"] = "tcp://127.0.0.1:12345",
                    ["topic"] = "ltc-templates",
                },
                ["mergedMining"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["auxPoolId"] = "doge-solo",
                    ["auxiliaryTemplatePollTimeoutMs"] = auxiliaryTemplatePollTimeoutMs,
                },
            },
            BlockRefreshInterval = 1000,
            JobRebroadcastTimeout = 10,
        };
        var auxiliary = new PoolConfig
        {
            Id = "doge-solo",
            Coin = "dogecoin",
            Enabled = true,
            Address = "DTestAddress",
            Daemons = new[]
            {
                new DaemonEndpointConfig
                {
                    Host = "127.0.0.1",
                    Port = auxiliaryRpcPort,
                },
            },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Template = new BitcoinTemplate
            {
                Symbol = "DOGE",
                Name = "Dogecoin",
                Family = CoinFamily.Bitcoin,
            },
        };
        var cluster = new ClusterConfig
        {
            Pools = new[] { parent, auxiliary },
            PaymentProcessing = new ClusterPaymentProcessingConfig { Enabled = true },
        };
        return (parent, auxiliary, cluster);
    }

    private sealed class TestManager : MergedMiningBitcoinJobManager
    {
        public TestManager(IComponentContext ctx, IMasterClock clock, IMessageBus messageBus,
            IExtraNonceProvider extraNonceProvider, IBlockCandidateRecorder recorder) :
            base(ctx, clock, messageBus, extraNonceProvider, recorder)
        {
        }

        private readonly Queue<RpcResponse<BlockTemplate>> responses = new();
        private int testJobId;

        public Func<MergedMiningShareResult> ProcessMergedShareHandler { get; set; }
        public Func<CancellationToken, Task<bool[]>> SubmitCandidatePathsHandler { get; set; }
        public Exception ParentSubmissionException { get; set; }
        public Exception JobCreationException { get; set; }
        public Func<AuxBlockTemplate, Exception> JobCreationExceptionFactory { get; set; }

        public MergedMiningBitcoinJob Current => (MergedMiningBitcoinJob) currentJob;

        public void Seed(BlockTemplate parent, AuxBlockTemplate auxiliary) =>
            currentJob = TestJob.Create(parent, auxiliary, "seed");

        public void SeedStartup(AuxBlockTemplate auxiliary)
        {
            currentJob = null;
            CacheStartupAuxiliaryTemplate(auxiliary);
        }

        public void Enqueue(BlockTemplate template) =>
            responses.Enqueue(new RpcResponse<BlockTemplate>(template));

        public void InitializeJobUpdates(CancellationToken ct) => SetupJobUpdates(ct);

        public Task<(bool IsNew, bool Force)> Update(CancellationToken ct,
            string via = null) => UpdateJob(ct, false, via);

        protected override Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(
            CancellationToken ct) => Task.FromResult(responses.Dequeue());

        protected override MergedMiningShareResult ProcessMergedShare(
            MergedMiningBitcoinJob job, StratumConnection worker, string extraNonce2,
            string nTime, string nonce, string versionBits) =>
            ProcessMergedShareHandler?.Invoke() ??
            base.ProcessMergedShare(job, worker, extraNonce2, nTime, nonce, versionBits);

        protected override Task<bool[]> SubmitCandidatePathsAsync(StratumConnection worker,
            MergedMiningBitcoinWorkerContext context, MergedMiningShareResult result,
            Share share, CancellationToken operationToken) =>
            SubmitCandidatePathsHandler?.Invoke(operationToken) ??
            base.SubmitCandidatePathsAsync(worker, context, result, share, operationToken);

        protected override Task<SubmitResult> SubmitParentBlockWithReconciliationAsync(
            Share share, string blockHex, CancellationToken ct)
        {
            if(ParentSubmissionException != null)
                return Task.FromException<SubmitResult>(ParentSubmissionException);

            return base.SubmitParentBlockWithReconciliationAsync(share, blockHex, ct);
        }

        protected override MergedMiningBitcoinJob CreateMergedMiningJob(
            BlockTemplate blockTemplate, AuxBlockTemplate auxiliaryTemplate)
        {
            var exception = JobCreationExceptionFactory?.Invoke(auxiliaryTemplate) ??
                JobCreationException;
            if(exception != null)
                throw exception;

            return TestJob.Create(blockTemplate, auxiliaryTemplate,
                $"test-{Interlocked.Increment(ref testJobId)}");
        }
    }

    private sealed class TestJob : MergedMiningBitcoinJob
    {
        public static TestJob Create(BlockTemplate parent, AuxBlockTemplate auxiliary,
            string id)
        {
            var job = new TestJob
            {
                BlockTemplate = parent,
                AuxiliaryBlockTemplate = auxiliary,
                JobId = id,
                Difficulty = 1,
            };
            job.rewardToPool = new NBitcoin.Money(parent.CoinbaseValue,
                NBitcoin.MoneyUnit.Satoshi);
            job.jobParams = new object[]
            {
                id, parent.PreviousBlockhash, "", "", Array.Empty<string>(),
                "", parent.Bits, "", false,
            };
            return job;
        }
    }

    private sealed class HangingJsonRpcServer : IAsyncDisposable
    {
        public HangingJsonRpcServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint) listener.LocalEndpoint).Port;
            serverTask = ServeAsync();
        }

        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task serverTask;

        public int Port { get; }
        public TaskCompletionSource<bool> RequestReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task ServeAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                // This fixture models one RPC attempt. Reject any unexpected reconnect
                // instead of leaving it queued silently behind the hanging request.
                listener.Stop();
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, stop.Token);

                if(read > 0)
                    RequestReceived.TrySetResult(true);

                await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested)
            {
            }
            catch(SocketException) when(stop.IsCancellationRequested)
            {
            }
            catch(IOException)
            {
                // The client is expected to abort the hanging request when its deadline
                // or host-cancellation token wins.
            }
            catch(ObjectDisposedException) when(stop.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await serverTask;
            stop.Dispose();
        }
    }

    private sealed class SequenceJsonRpcServer : IAsyncDisposable
    {
        public SequenceJsonRpcServer(params JObject[] responses)
        {
            this.responses = new Queue<JObject>(responses);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint) listener.LocalEndpoint).Port;
            serverTask = ServeAsync();
        }

        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Queue<JObject> responses;
        private readonly Task serverTask;

        public int Port { get; }

        public static JObject Success(AuxBlockTemplate template) => new()
        {
            ["result"] = JObject.FromObject(template),
            ["error"] = null,
        };

        public static JObject RpcError(int code, string message) => new()
        {
            ["result"] = null,
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        private async Task ServeAsync()
        {
            try
            {
                while(responses.Count > 0)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await RespondAsync(client, responses.Dequeue(), stop.Token);
                }
            }
            catch(OperationCanceledException) when(stop.IsCancellationRequested)
            {
            }
            catch(SocketException) when(stop.IsCancellationRequested)
            {
            }
            catch(ObjectDisposedException) when(stop.IsCancellationRequested)
            {
            }
        }

        private static async Task RespondAsync(TcpClient client, JObject response,
            CancellationToken ct)
        {
            var stream = client.GetStream();
            using var requestBuffer = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;

            while(true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if(read == 0)
                    throw new IOException("JSON-RPC client closed before sending a request");

                requestBuffer.Write(buffer, 0, read);
                var requestBytes = requestBuffer.ToArray();
                headerEnd = FindHeaderEnd(requestBytes);
                if(headerEnd < 0)
                    continue;

                var headers = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
                contentLength = headers.Split("\r\n")
                    .Select(x => x.Split(':', 2))
                    .Where(x => x.Length == 2 && string.Equals(x[0], "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(x => int.Parse(x[1].Trim()))
                    .Single();

                if(requestBytes.Length >= headerEnd + 4 + contentLength)
                    break;
            }

            var completeRequest = requestBuffer.ToArray();
            var requestJson = Encoding.UTF8.GetString(completeRequest,
                headerEnd + 4, contentLength);
            var request = JObject.Parse(requestJson);
            response["jsonrpc"] = "2.0";
            response["id"] = request["id"]?.DeepClone();
            var body = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
            var headersBytes = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headersBytes, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }

        private static int FindHeaderEnd(byte[] bytes)
        {
            for(var index = 0; index <= bytes.Length - 4; index++)
            {
                if(bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                    return index;
            }

            return -1;
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await serverTask;
            stop.Dispose();
        }
    }
}
