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
using Npgsql;
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

    [Fact]
    public async Task RecoveryMode_MissingCoinTemplateStillImportsAndStopsSuccessfully()
    {
        var recovered = false;
        var stopped = false;
        Exception templateWarning = null;

        var exitCode = await Program.RunStartupBoundaryAsync(
            () => Program.RunRecoveryModeAsync(
                () => Program.RecoverSharesWithBestEffortTemplatesAsync(
                    () => throw new FileNotFoundException("optional coin template is missing"),
                    () =>
                    {
                        recovered = true;
                        return Task.CompletedTask;
                    },
                    ex => templateWarning = ex),
                () => stopped = true),
            _ => Task.CompletedTask,
            () => 0);

        Assert.True(recovered);
        Assert.True(stopped);
        Assert.Equal(0, exitCode);
        Assert.IsType<FileNotFoundException>(templateWarning);
    }

    [Fact]
    public async Task RecoveryEntryPoint_UnreachablePostgresReturnsFailureAndPreservesJournal()
    {
        var config = MergedMiningCluster();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw new NpgsqlException("PostgreSQL is unavailable"));

        await AssertRecoveryPreflightFailurePreservesJournal(() =>
            Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
                blockRepository, CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryEntryPoint_MissingMergedMiningIndexesReturnsFailureAndPreservesJournal()
    {
        var config = MergedMiningCluster();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var blockRepository = Substitute.For<IBlockRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        blockRepository.HasMergedMiningBlockIndexesAsync(connection,
            Arg.Any<CancellationToken>()).Returns(false);

        await AssertRecoveryPreflightFailurePreservesJournal(() =>
            Program.EnsureMergedMiningSchemaAsync(config, connectionFactory,
                blockRepository, CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryEntryPoint_MissingRecoveryManifestSchemaReturnsFailureAndPreservesJournal()
    {
        var config = MergedMiningCluster();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var shareRepository = Substitute.For<IShareRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        shareRepository.HasRecoveryImportSchemaAsync(connection,
            Arg.Any<CancellationToken>()).Returns(false);

        await AssertRecoveryPreflightFailurePreservesJournal(() =>
            Program.EnsureShareRecoverySchemaAsync(true, config, connectionFactory,
                shareRepository, CancellationToken.None));

        await shareRepository.Received(1).HasRecoveryImportSchemaAsync(connection,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalStartup_DoesNotRequireRecoveryManifestPreflight()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var shareRepository = Substitute.For<IShareRepository>();

        await Program.EnsureShareRecoverySchemaAsync(false, MergedMiningCluster(),
            connectionFactory, shareRepository, CancellationToken.None);

        await connectionFactory.DidNotReceive().OpenConnectionAsync();
    }

    [Fact]
    public async Task RecoveryEntryPoint_MalformedDatabaseConfigurationReturnsFailureAndPreservesJournal()
    {
        await AssertRecoveryPreflightFailurePreservesJournal(() =>
            throw new FormatException("malformed PostgreSQL configuration"));
    }

    [Fact]
    public async Task EntryPoint_EscapedCancellationReturnsFailure()
    {
        var reported = false;

        var exitCode = await Program.RunStartupBoundaryAsync(
            () => throw new OperationCanceledException(),
            _ =>
            {
                reported = true;
                return Task.CompletedTask;
            },
            () => 0);

        Assert.Equal(1, exitCode);
        Assert.True(reported);
    }

    [Fact]
    public async Task EntryPoint_ReturnsRecoveryFailureCodeAfterHostStopsCleanly()
    {
        var exitCode = await Program.RunStartupBoundaryAsync(
            () => Task.CompletedTask,
            _ => Task.CompletedTask,
            () => 1);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task PoolStartupFailure_StopsRealHostWithFailureExitCode()
    {
        var processStatus = new ProcessStatus();
        Exception reported = null;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IProcessStatus>(processStatus);
                services.AddSingleton(sp => new Program(
                    Substitute.For<IComponentContext>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    processStatus,
                    _ =>
                    {
                        Program.StopApplicationAsFailure(processStatus,
                            sp.GetRequiredService<IHostApplicationLifetime>()
                                .StopApplication);
                        return Task.CompletedTask;
                    }));
                services.AddHostedService(sp => sp.GetRequiredService<Program>());
            })
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var exitCode = await Program.RunStartupBoundaryAsync(
            () => host.RunAsync(),
            ex =>
            {
                reported = ex;
                return Task.CompletedTask;
            },
            () => processStatus.ExitCode);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, processStatus.ExitCode);
        Assert.Null(reported);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PoolSupervisor_FaultOrUnexpectedReturnStopsRealHostAndSibling(
        bool throws)
    {
        var processStatus = new ProcessStatus();
        var siblingStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IProcessStatus>(processStatus);
                services.AddSingleton(sp => new Program(
                    Substitute.For<IComponentContext>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    processStatus,
                    ct => Program.SupervisePoolLifetimesAsync(new[]
                    {
                        new KeyValuePair<string, Func<CancellationToken, Task>>(
                            "ltc-solo", async _ =>
                            {
                                await siblingStarted.Task;

                                if(throws)
                                    throw new PoolStartupException(
                                        "simulated parent pool failure", "ltc-solo");
                            }),
                        new KeyValuePair<string, Func<CancellationToken, Task>>(
                            "doge-solo", async siblingToken =>
                            {
                                siblingStarted.TrySetResult(true);

                                try
                                {
                                    await Task.Delay(Timeout.InfiniteTimeSpan,
                                        siblingToken);
                                }
                                catch(OperationCanceledException) when(
                                    siblingToken.IsCancellationRequested)
                                {
                                    siblingCancelled.TrySetResult(true);
                                    throw;
                                }
                            }),
                    }, ct, processStatus,
                    sp.GetRequiredService<IHostApplicationLifetime>()
                        .StopApplication)));
                services.AddHostedService(sp => sp.GetRequiredService<Program>());
            })
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var exitCode = await Program.RunStartupBoundaryAsync(
                () => host.RunAsync(),
                _ => Task.CompletedTask,
                () => processStatus.ExitCode)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, exitCode);
        Assert.Equal(1, processStatus.ExitCode);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(siblingCancelled.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PoolSupervisor_DeliberateHostStopDrainsHealthyPoolsSuccessfully()
    {
        var processStatus = new ProcessStatus();
        var poolsStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IProcessStatus>(processStatus);
                services.AddSingleton(sp => new Program(
                    Substitute.For<IComponentContext>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    processStatus,
                    ct => Program.SupervisePoolLifetimesAsync(
                        new[] { "ltc-solo", "doge-solo" }.Select(poolId =>
                            new KeyValuePair<string, Func<CancellationToken, Task>>(
                                poolId, async poolToken =>
                                {
                                    if(Interlocked.Increment(ref startedCount) == 2)
                                        poolsStarted.TrySetResult(true);

                                    await Task.Delay(Timeout.InfiniteTimeSpan,
                                        poolToken);
                                })),
                        ct, processStatus,
                        sp.GetRequiredService<IHostApplicationLifetime>()
                            .StopApplication)));
                services.AddHostedService(sp => sp.GetRequiredService<Program>());
            })
            .Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var run = Program.RunStartupBoundaryAsync(
            () => host.RunAsync(),
            _ => Task.CompletedTask,
            () => processStatus.ExitCode);

        await poolsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.StopApplication();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processStatus.ExitCode);
    }

    [Fact]
    public async Task NormalStopApplication_RealHostReturnsSuccess()
    {
        var processStatus = new ProcessStatus();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IProcessStatus>(processStatus);
                services.AddSingleton(sp => new Program(
                    Substitute.For<IComponentContext>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    processStatus,
                    _ =>
                    {
                        sp.GetRequiredService<IHostApplicationLifetime>()
                            .StopApplication();
                        return Task.CompletedTask;
                    }));
                services.AddHostedService(sp => sp.GetRequiredService<Program>());
            })
            .Build();

        var exitCode = await Program.RunStartupBoundaryAsync(
            () => host.RunAsync(),
            _ => Task.CompletedTask,
            () => processStatus.ExitCode);

        Assert.Equal(0, exitCode);
        Assert.Equal(0, processStatus.ExitCode);
    }

    [Fact]
    public async Task ShutdownTimeout_RealHostReturnsFailure()
    {
        var processStatus = new ProcessStatus();
        var blockingService = new BlockingStopService();
        Exception reported = null;

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options =>
                    options.ShutdownTimeout = TimeSpan.FromMilliseconds(100));
                services.AddSingleton<IProcessStatus>(processStatus);
                services.AddSingleton(blockingService);
                services.AddSingleton<IHostedService>(blockingService);
            })
            .Build();

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var run = Program.RunStartupBoundaryAsync(
            () => host.RunAsync(),
            ex =>
            {
                reported = ex;
                return Task.CompletedTask;
            },
            () => processStatus.ExitCode);

        await blockingService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifetime.StopApplication();

        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, exitCode);
        Assert.NotNull(reported);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.True(blockingService.StopCancellationObserved);
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
                        sp.GetRequiredService<IProcessStatus>(),
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
            .ConfigureServices((_, services) =>
                Program.ConfigureHostShutdown(services))
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

    private static async Task AssertRecoveryPreflightFailurePreservesJournal(
        Func<Task> preflight)
    {
        var filename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var content = "{\"poolId\":\"ltc-solo\",\"miner\":\"recovery-beneficiary\"}";
        Exception reported = null;

        try
        {
            await File.WriteAllTextAsync(filename, content);

            var exitCode = await Program.RunStartupBoundaryAsync(preflight,
                ex =>
                {
                    reported = ex;
                    return Task.CompletedTask;
                },
                () => 0);

            Assert.Equal(1, exitCode);
            Assert.NotNull(reported);
            Assert.True(File.Exists(filename));
            Assert.Equal(content, await File.ReadAllTextAsync(filename));
        }
        finally
        {
            File.Delete(filename);
        }
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

    private sealed class BlockingStopService : IHostedService
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool StopCancellationObserved { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch(OperationCanceledException)
            {
                StopCancellationObserved = true;
                throw;
            }
        }
    }
}
