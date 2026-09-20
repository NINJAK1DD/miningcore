using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Banning;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public partial class MergedMiningManagerReorgTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidatedMergedProof_StatisticalSubscriberFailure_IsNeverInvalid(bool candidate)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        builder.RegisterInstance(Substitute.For<IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var bus = new MessageBus();
        var publications = 0;
        using var accounting = bus.Listen<Share>().Where(x => x != null).Subscribe(_ =>
        {
            publications++;
            throw new StratumException(StratumError.Other, "accounting subscriber failure");
        });
        var telemetry = new List<TelemetryEvent>();
        using var metrics = bus.Listen<TelemetryEvent>().Where(x => x != null).Subscribe(telemetry.Add);
        var recorder = Substitute.For<IBlockCandidateRecorder>();
        var manager = new TestManager(container, clock, bus, Substitute.For<IExtraNonceProvider>(), recorder);
        var (parent, _, cluster) = CreateConfig();
        parent.Banning = new PoolShareBasedBanningConfig { Enabled = true, CheckThreshold = 0, InvalidPercent = 1 };
        cluster.Banning = new ClusterBanningConfig { BanOnInvalidShares = true };
        manager.Configure(parent, cluster);
        var share = new Share { IsBlockCandidate = candidate, BlockHash = new string('a', 64) };
        var validations = 0;
        manager.ProcessMergedShareHandler = () =>
        {
            validations++;
            return new MergedMiningShareResult { Share = share, ParentBlockHex = candidate ? "parent" : null };
        };
        var candidates = 0;
        manager.SubmitCandidatePathsHandler = async _ =>
        {
            candidates++;
            await recorder.PersistBlockCandidateAsync(share);
            return new[] { true };
        };
        var context = new MergedMiningBitcoinWorkerContext { Miner = "parent", Worker = "worker" };
        context.Init(1, null, clock);
        context.IsAuthorized = context.IsSubscribed = true;
        var job = TestJob.Create(new BlockTemplate(), new AuxBlockTemplate(), "accepted-job");
        context.AddJob(job, 4);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(), clock,
            "merged-publication", false);
        worker.SetContext(context);
        var pool = new PublicationPool(container, clock, bus, manager);
        pool.Configure(parent, cluster);
        var request = new JsonRpcRequest
        {
            Id = 1, Method = "mining.submit",
            Params = JArray.FromObject(new[] { "parent.worker", job.JobId, "00", "00000000", "00000000" }),
        };
        await pool.Dispatch(worker, request);
        await pool.Dispatch(worker, request); // Already decoded duplicate held across cleanup.
        await manager.DrainCandidateOperationsAsync();
        Assert.Equal(1, validations);
        Assert.Equal(1, publications);
        Assert.Equal(candidate ? 1 : 0, candidates);
        await recorder.Received(candidate ? 1 : 0).PersistBlockCandidateAsync(share);
        Assert.Equal(0, context.Stats.InvalidShares);
        Assert.Equal(0, context.Stats.ValidShares); // Persistence admission failed.
        Assert.Empty(context.validJobs);
        Assert.True(worker.IsDisconnectRequested);
        Assert.Equal(0, worker.ResponseSequence);
        Assert.DoesNotContain(telemetry, x => x.Category == TelemetryCategory.Share && x.Success == false);
        pool.Bans.DidNotReceiveWithAnyArgs().Ban(default, default);
    }

    private sealed class PublicationPool : MergedMiningBitcoinPool
    {
        internal IBanManager Bans { get; } = Substitute.For<IBanManager>();
        internal PublicationPool(IComponentContext ctx, IMasterClock clock, IMessageBus bus,
            MergedMiningBitcoinJobManager manager) : base(ctx, new JsonSerializerSettings(),
            Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(), AutoMapperFactory.CreateMapper(),
            clock, bus, new RecyclableMemoryStreamManager(), new NicehashService(Substitute.For<System.Net.Http.IHttpClientFactory>(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())))
        {
            this.manager = manager;
            banManager = Bans;
        }

        internal Task Dispatch(StratumConnection connection, JsonRpcRequest request) =>
            OnRequestAsync(connection, new System.Reactive.Timestamped<JsonRpcRequest>(request, clock.Now), CancellationToken.None);
    }
}
