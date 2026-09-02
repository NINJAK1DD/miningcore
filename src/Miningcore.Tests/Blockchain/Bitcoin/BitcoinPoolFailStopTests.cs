using System;
using System.Collections.Generic;
using System.Net.Http;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinPoolFailStopTests
{
    [Fact]
    public void RuntimeDirectJobFailure_ClosesMiningAndRaisesCriticalAlertOnce()
    {
        var failStop = Substitute.For<IMiningFailStopCoordinator>();
        var messageBus = Substitute.For<IMessageBus>();
        var builder = new ContainerBuilder();
        builder.RegisterInstance(new JsonSerializerSettings());
        builder.RegisterInstance(failStop).As<IMiningFailStopCoordinator>();
        builder.RegisterInstance(Substitute.For<IBlockRepository>());
        builder.RegisterInstance(Substitute.For<IShareRepository>());
        using var container = builder.Build();
        var clock = Substitute.For<IMasterClock>();
        var manager = new BitcoinJobManager(container, clock, messageBus,
            Substitute.For<IExtraNonceProvider>());
        var config = new PoolConfig
        {
            Id = "btc-direct",
            Template = new BitcoinTemplate
            {
                Family = CoinFamily.Bitcoin,
                Symbol = "BTC",
            },
            Daemons = new[] { new DaemonEndpointConfig() },
            Extra = new Dictionary<string, object>
            {
                ["soloCoinbasePayout"] = true,
            },
        };
        var cluster = new ClusterConfig();
        manager.Configure(config, cluster);
        var streams = new RecyclableMemoryStreamManager();
        var pool = new TestBitcoinPool(container,
            new JsonSerializerSettings(), Substitute.For<IConnectionFactory>(),
            Substitute.For<IStatsRepository>(), AutoMapperFactory.CreateMapper(),
            clock, messageBus, streams,
            new NicehashService(Substitute.For<IHttpClientFactory>(),
                new Microsoft.Extensions.Caching.Memory.MemoryCache(
                    new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())));
        pool.Initialize(config, cluster, manager);
        var worker = new BitcoinWorkerContext();
        worker.AddJob(new BitcoinJob(), 1);
        var connection = new StratumConnection(
            NLog.LogManager.GetCurrentClassLogger(), streams, clock,
            "direct-worker", false);
        connection.SetContext(worker);
        pool.AddConnection(connection);
        var failure = new PoolStartupException(
            "direct template contract changed", config.Id);

        pool.HandleJobPipelineFailure(failure);
        pool.HandleJobPipelineFailure(failure);

        Assert.True(pool.DirectJobPipelineFailed);
        Assert.Empty(worker.validJobs);
        failStop.Received(1).BeginFailStop(ProcessExitCodes.GeneralFailure);
        messageBus.Received(1).SendMessage(
            Arg.Is<AdminNotification>(x =>
                x.Subject.Contains("direct-SOLO", StringComparison.Ordinal) &&
                x.Message.Contains(config.Id, StringComparison.Ordinal)),
            Arg.Any<string>());
    }

    private sealed class TestBitcoinPool : BitcoinPool
    {
        public TestBitcoinPool(IComponentContext ctx,
            JsonSerializerSettings serializerSettings, IConnectionFactory cf,
            IStatsRepository statsRepo, IMapper mapper, IMasterClock clock,
            IMessageBus messageBus, RecyclableMemoryStreamManager rmsm,
            NicehashService nicehashService) : base(ctx, serializerSettings, cf,
            statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
        {
        }

        public void Initialize(PoolConfig config, ClusterConfig cluster,
            BitcoinJobManager jobManager)
        {
            poolConfig = config;
            clusterConfig = cluster;
            manager = jobManager;
            logger = NLog.LogManager.GetCurrentClassLogger();
        }

        public void AddConnection(StratumConnection connection) =>
            RegisterConnection(connection);
    }
}
