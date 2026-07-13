using System;
using System.Collections.Generic;
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
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class MergedMiningManagerReorgTests
{
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
