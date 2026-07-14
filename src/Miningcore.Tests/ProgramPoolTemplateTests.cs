using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests;

public class ProgramPoolTemplateTests
{
    [Fact]
    public async Task RecoveryMode_StopsHostAndSkipsNormalBackgroundServices()
    {
        var recovered = false;
        var stopped = false;

        await Program.RunRecoveryModeAsync(() =>
        {
            recovered = true;
            return Task.CompletedTask;
        }, () => stopped = true);

        Assert.True(recovered);
        Assert.True(stopped);
        Assert.False(Program.ShouldConfigureBackgroundServices(true));
        Assert.True(Program.ShouldConfigureBackgroundServices(false));
        Assert.False(Program.ShouldConfigureApi(true, null));
        Assert.False(Program.ShouldConfigureApi(true, new ApiConfig { Enabled = true }));
        Assert.True(Program.ShouldConfigureApi(false, null));
        Assert.True(Program.ShouldConfigureApi(false, new ApiConfig { Enabled = true }));
        Assert.False(Program.ShouldConfigureApi(false, new ApiConfig { Enabled = false }));
    }

    [Fact]
    public async Task RecoveryMode_StopsHostWhenImportFails()
    {
        var stopped = false;
        var exitCode = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.RunRecoveryModeAsync(
                () => throw new InvalidOperationException("import failed"),
                () => stopped = true,
                code => exitCode = code));

        Assert.True(stopped);
        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AssignPoolTemplates_IsIndependentOfParentAuxiliaryOrder(bool parentFirst)
    {
        var litecoin = new BitcoinTemplate { Symbol = "LTC", Family = CoinFamily.Bitcoin };
        var dogecoin = new BitcoinTemplate { Symbol = "DOGE", Family = CoinFamily.Bitcoin };
        var parent = new PoolConfig { Id = "ltc-solo", Coin = "litecoin", Enabled = true };
        var auxiliary = new PoolConfig { Id = "doge-solo", Coin = "dogecoin", Enabled = true };
        var pools = parentFirst
            ? new[] { parent, auxiliary }
            : new[] { auxiliary, parent };
        var templates = new Dictionary<string, CoinTemplate>
        {
            ["litecoin"] = litecoin,
            ["dogecoin"] = dogecoin,
        };

        Program.AssignPoolTemplates(pools, templates);

        Assert.Same(litecoin, parent.Template);
        Assert.Same(dogecoin, auxiliary.Template);
    }

    [Fact]
    public void AssignPoolTemplates_RejectsUndefinedCoinBeforePoolStartup()
    {
        var pool = new PoolConfig { Id = "missing", Coin = "undefined", Enabled = true };

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.AssignPoolTemplates(new[] { pool },
                new Dictionary<string, CoinTemplate>()));

        Assert.Equal(pool.Id, ex.PoolId);
    }

    [Fact]
    public void MergedMining_ShareRelaySender_RequiresPostgresForSynchronousBlocks()
    {
        var config = MergedMiningCluster(shareRelaySender: true,
            globalPaymentProcessing: true, postgres: false);

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateMergedMiningDeployment(config));

        Assert.Contains("synchronously", ex.Message);
        Assert.False(Program.ShouldRunPaymentProcessor(config));
    }

    [Fact]
    public async Task MergedMining_ShareRelaySender_WithPostgresCanRunPayoutManager()
    {
        var config = MergedMiningCluster(shareRelaySender: true);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        blockRepository.HasMergedMiningBlockIndexesAsync(connection,
            Arg.Any<CancellationToken>()).Returns(true);

        Assert.True(Program.ShouldRunPaymentProcessor(config));
        Assert.True(Program.RequiresMergedMiningPersistence(config));
        Program.ValidateMergedMiningDeployment(config);
        await Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
            blockRepository, CancellationToken.None);
        await connectionFactory.Received(1).OpenConnectionAsync();
    }

    [Fact]
    public void MergedMining_ShareRelaySender_WithPostgresDoesNotRequireLocalPayoutManager()
    {
        var config = MergedMiningCluster(shareRelaySender: true,
            globalPaymentProcessing: false);

        Program.ValidateMergedMiningDeployment(config);

        Assert.True(Program.RequiresMergedMiningPersistence(config));
        Assert.False(Program.ShouldRunPaymentProcessor(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaymentProcessor_DirectAndRelayReceiverNodesRemainEnabled(bool relayReceiver)
    {
        var config = MergedMiningCluster(relayReceiver: relayReceiver);

        Assert.True(Program.ShouldRunPaymentProcessor(config));
    }

    [Fact]
    public void PaymentProcessor_NonMergedRelaySenderWithPostgresRetainsOriginalBehaviour()
    {
        var config = MergedMiningCluster(shareRelaySender: true);
        config.Pools[0].Extra = null;

        Assert.True(Program.ShouldRunPaymentProcessor(config));
        Assert.False(Program.RequiresMergedMiningPersistence(config));
    }

    [Fact]
    public void MergedMining_DirectNode_RequiresGlobalPaymentProcessing()
    {
        var config = MergedMiningCluster(globalPaymentProcessing: false);

        var ex = Assert.Throws<PoolStartupException>(() =>
            Program.ValidateMergedMiningDeployment(config));

        Assert.Contains("cluster-level payment processing", ex.Message);
    }

    [Fact]
    public async Task HostShutdown_WaitsBeyondLegacyTimeoutUntilCandidateJournalIsDurable()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile()))
            .CreateMapper();
        var databaseAttempt = new TaskCompletionSource<IDbConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var databaseAttemptStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(_ =>
        {
            databaseAttemptStarted.TrySetResult(true);
            return databaseAttempt.Task;
        });
        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), shareRepository, blockRepository,
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, messageBus)
        {
            ShutdownDatabaseAttemptTimeout = TimeSpan.FromMilliseconds(100),
        };
        var candidate = new Miningcore.Blockchain.Share
        {
            PoolId = "doge-solo",
            Miner = "DHostShutdownBeneficiary",
            BlockHeight = 789,
            BlockHash = "host-shutdown-doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData =
                "auxpow-block:host-shutdown-doge-block-hash",
        };
        var running = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCandidate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var durable = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var slowServer = new SlowServer();

        async Task RunProgramAsync(CancellationToken stoppingToken)
        {
            running.TrySetResult(true);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                stopObserved.TrySetResult(true);
            }

            // This models a manager-owned daemon operation that remains legitimately active
            // beyond .NET 6's former five-second host default.
            await releaseCandidate.Task;
            var persistence = recorder.PersistBlockCandidateAsync(candidate);
            await databaseAttemptStarted.Task;
            recorder.BeginShutdown();
            await persistence;
            durable.TrySetResult(true);
        }

        try
        {
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    Program.ConfigureHostShutdown(services);
                })
                .ConfigureWebHost(builder => builder
                    .UseServer(slowServer)
                    .Configure(_ => { }))
                .ConfigureServices(services =>
                {
                    services.AddSingleton(sp => new Program(
                        Substitute.For<IComponentContext>(),
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        RunProgramAsync));
                    Program.ConfigureMiningShutdownCoordinator(services);
                })
                .Build();

            await host.StartAsync();
            await running.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Program.HostShutdownTimeout,
                host.Services.GetRequiredService<IOptions<HostOptions>>()
                    .Value.ShutdownTimeout);

            var stopping = host.StopAsync();
            await stopObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(5100));

            Assert.False(stopping.IsCompleted);
            Assert.False(durable.Task.IsCompleted);
            Assert.False(slowServer.Stopping.Task.IsCompleted);

            releaseCandidate.TrySetResult(true);
            await slowServer.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(durable.Task.IsCompletedSuccessfully);
            Assert.False(stopping.IsCompleted);

            slowServer.ReleaseStop();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));

            var persisted = (await File.ReadAllLinesAsync(recoveryFilename))
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('#'))
                .Select(JsonConvert.DeserializeObject<Miningcore.Blockchain.Share>)
                .Single();
            Assert.Equal(candidate.BlockHash, persisted.BlockHash);
            Assert.Equal(candidate.Miner, persisted.Miner);
        }
        finally
        {
            releaseCandidate.TrySetResult(true);
            slowServer.ReleaseStop();
            databaseAttempt.TrySetException(new TimeoutException(
                "simulated late PostgreSQL failure"));
            await Task.Delay(25);
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public void ProductionContainer_ShareRecorderServicesResolveSameSingleton()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile()))
            .CreateMapper();
        var config = new ClusterConfig { Pools = Array.Empty<PoolConfig>() };

        using var host = new HostBuilder()
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureContainer<ContainerBuilder>(builder =>
            {
                builder.RegisterModule<AutofacModule>();
                builder.RegisterInstance(config);
                builder.RegisterInstance(mapper).As<IMapper>();
                builder.RegisterInstance(connectionFactory).As<IConnectionFactory>();
                builder.RegisterInstance(shareRepository).As<IShareRepository>();
                builder.RegisterInstance(blockRepository).As<IBlockRepository>();
            })
            .ConfigureServices(Program.ConfigureShareRecorderHostedService)
            .Build();

        var self = host.Services.GetRequiredService<ShareRecorder>();
        var candidateRecorder = host.Services.GetRequiredService<IBlockCandidateRecorder>();
        var hostedRecorder = host.Services.GetServices<IHostedService>()
            .OfType<ShareRecorder>()
            .Single();

        Assert.Same(self, candidateRecorder);
        Assert.Same(self, hostedRecorder);
    }

    [Fact]
    public void MergedMining_LegacyNonDurableAcknowledgementIsNoLongerRequired()
    {
        var config = MergedMiningCluster();
        ((Dictionary<string, object>) config.Pools[0].Extra["mergedMining"])
            ["acceptNonDurableBlockDelivery"] = false;

        Program.ValidateMergedMiningDeployment(config);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedMining_RecorderNode_RejectsMissingIndexes(bool relayReceiver)
    {
        var config = MergedMiningCluster(relayReceiver: relayReceiver);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        blockRepository.HasMergedMiningBlockIndexesAsync(connection,
            Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<PoolStartupException>(() =>
            Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
                blockRepository, CancellationToken.None));

        Assert.Contains("add_auxpow_block_idempotency.sql", ex.Message);
        Assert.True(Program.ShouldRunPaymentProcessor(config));
        await connectionFactory.Received(1).OpenConnectionAsync();
    }

    private static ClusterConfig MergedMiningCluster(bool shareRelaySender = false,
        bool relayReceiver = false, bool globalPaymentProcessing = true,
        bool postgres = true)
    {
        return new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = "ltc-solo",
                    Enabled = true,
                    PaymentProcessing = new PoolPaymentProcessingConfig
                    {
                        Enabled = true,
                    },
                    Extra = new Dictionary<string, object>
                    {
                        ["mergedMining"] = new Dictionary<string, object>
                        {
                            ["enabled"] = true,
                            ["auxPoolId"] = "doge-solo",
                        },
                    },
                },
            },
            ShareRelay = shareRelaySender ? new ShareRelayConfig() : null,
            ShareRelays = relayReceiver ? new[] { new ShareRelayEndpointConfig() } : null,
            PaymentProcessing = new ClusterPaymentProcessingConfig
            {
                Enabled = globalPaymentProcessing,
            },
            Persistence = postgres
                ? new PersistenceConfig { Postgres = new PostgresConfig() }
                : null,
        };
    }

    private sealed class SlowServer : IServer
    {
        private readonly TaskCompletionSource<bool> releaseStop = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IFeatureCollection Features { get; } = new FeatureCollection();
        public TaskCompletionSource<bool> Stopping { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync<TContext>(IHttpApplication<TContext> application,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Stopping.TrySetResult(true);
            await releaseStop.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseStop() => releaseStop.TrySetResult(true);

        public void Dispose()
        {
            ReleaseStop();
        }
    }
}
