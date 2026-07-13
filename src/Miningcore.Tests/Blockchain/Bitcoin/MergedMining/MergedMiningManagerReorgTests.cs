using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningManagerReorgTests
{
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

    private static (PoolConfig Parent, PoolConfig Auxiliary, ClusterConfig Cluster) CreateConfig()
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
            Template = new BitcoinTemplate { Symbol = "LTC", Name = "Litecoin" },
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
            Daemons = new[] { new DaemonEndpointConfig { Host = "127.0.0.1", Port = 44555 } },
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = PayoutScheme.SOLO,
            },
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
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

        public MergedMiningBitcoinJob Current => (MergedMiningBitcoinJob) currentJob;

        public void Seed(BlockTemplate parent, AuxBlockTemplate auxiliary) =>
            currentJob = TestJob.Create(parent, auxiliary, "seed");

        public void Enqueue(BlockTemplate template) =>
            responses.Enqueue(new RpcResponse<BlockTemplate>(template));

        public void InitializeJobUpdates(CancellationToken ct) => SetupJobUpdates(ct);

        protected override Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(
            CancellationToken ct) => Task.FromResult(responses.Dequeue());

        protected override MergedMiningBitcoinJob CreateMergedMiningJob(
            BlockTemplate blockTemplate, AuxBlockTemplate auxiliaryTemplate) =>
            TestJob.Create(blockTemplate, auxiliaryTemplate,
                $"test-{Interlocked.Increment(ref testJobId)}");
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
            job.jobParams = new object[]
            {
                id, parent.PreviousBlockhash, "", "", Array.Empty<string>(),
                "", parent.Bits, "", false,
            };
            return job;
        }
    }
}
