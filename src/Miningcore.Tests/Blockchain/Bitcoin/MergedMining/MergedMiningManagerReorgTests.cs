using System;
using System.Collections.Generic;
using System.IO;
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
using NLog;
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
    public async Task NonCandidateStatisticalShare_PropagatesPublishedCloneAdmission()
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
            NetworkDifficulty = 100,
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
        Assert.Same(admission.Task, returned.PersistenceAdmission);
        Assert.False(returned.PersistenceAdmission.IsCompleted);
        admission.TrySetResult();
        await returned.PersistenceAdmission;
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

        using var validationStarted = new ManualResetEventSlim();
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
            validationStarted.Set();
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

        var submitTask = Task.Run(async () => await manager.SubmitShareAsync(worker,
            new object[] { "ltc-miner.rig01", job.JobId, "00", "00000000", "00000000" },
            hostShutdown.Token));
        Assert.True(validationStarted.Wait(TimeSpan.FromSeconds(2)));

        hostShutdown.Cancel();
        var shutdownDrain = manager.DrainCandidateOperationsAsync();
        Assert.False(shutdownDrain.IsCompleted);

        releaseValidation.Set();
        await candidateSubmissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(shutdownDrain.IsCompleted);
        Assert.False(persisted.Task.IsCompleted);

        releasePersistence.TrySetResult(true);
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await shutdownDrain.WaitAsync(TimeSpan.FromSeconds(2));
        var returnedShare = await submitTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(candidateShare, returnedShare);
        await recorder.Received(1).PersistBlockCandidateAsync(
            Arg.Is<Share>(x => x.BlockOnly && x.BlockHash == candidateShare.BlockHash));
        Assert.Throws<OperationCanceledException>(() =>
            manager.BeginCandidatePreparation());
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

        public Func<MergedMiningShareResult> ProcessMergedShareHandler { get; set; }
        public Func<CancellationToken, Task<bool[]>> SubmitCandidatePathsHandler { get; set; }
        public Exception ParentSubmissionException { get; set; }

        public MergedMiningBitcoinJob Current => (MergedMiningBitcoinJob) currentJob;

        public void Seed(BlockTemplate parent, AuxBlockTemplate auxiliary) =>
            currentJob = TestJob.Create(parent, auxiliary, "seed");

        public void Enqueue(BlockTemplate template) =>
            responses.Enqueue(new RpcResponse<BlockTemplate>(template));

        public void InitializeJobUpdates(CancellationToken ct) => SetupJobUpdates(ct);

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
