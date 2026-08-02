using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Data;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NSubstitute;
using NLog;
using NLog.Config;
using NLog.Targets;
using Polly.CircuitBreaker;
using ProtoBuf;
using Xunit;
using Block = Miningcore.Persistence.Model.Block;
using PersistedShare = Miningcore.Persistence.Model.Share;

namespace Miningcore.Tests.Mining;

[Collection(ShareRecoveryLoggingCollection.Name)]
public class ShareRecorderTests
{
    [Fact]
    public async Task StartAsync_ReceivesImmediateShareAndDisposesSubscription()
    {
        var shares = new Subject<Share>();
        var messageBus = Substitute.For<IMessageBus>();
        messageBus.Listen<Share>().Returns(shares);
        var fixture = CreateRecoveryFixture(messageBus);
        var persisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ShareRepository.BatchInsertAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                persisted.TrySetResult();
                return Task.CompletedTask;
            });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await fixture.Recorder.StartAsync(stop.Token);
        Assert.True(shares.HasObservers);

        shares.OnNext(new Share
        {
            PoolId = "doge-solo",
            Miner = "immediate-miner",
            Difficulty = 1,
            Created = DateTime.UtcNow,
        });

        await persisted.Task.WaitAsync(stop.Token);
        await fixture.Recorder.StopAsync(stop.Token);

        Assert.False(shares.HasObservers);
    }

    [Fact]
    public async Task PersistenceQueue_SustainedOutageRemainsBoundedAndJournalsOverflow()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = new MessageBus();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDatabase = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                databaseEntered.TrySetResult();
                await releaseDatabase.Task;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, messageBus)
        {
            PersistenceQueueCapacity = 2,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await recorder.StartAsync(timeout.Token);

            for(var index = 0; index < 250; index++)
            {
                messageBus.SendMessage(new Share
                {
                    PoolId = "ltc-solo",
                    Miner = $"initial-{index}",
                });
            }

            await databaseEntered.Task.WaitAsync(timeout.Token);
            messageBus.SendMessage(new Share { PoolId = "ltc-solo", Miner = "queued-one" });
            messageBus.SendMessage(new Share { PoolId = "ltc-solo", Miner = "queued-two" });
            var overflow = new Share
                { PoolId = "ltc-solo", Miner = "journal-overflow" };
            messageBus.SendMessage(overflow);
            await overflow.PersistenceAdmission.WaitAsync(timeout.Token);

            Assert.Equal(2, recorder.PersistenceQueueHighWatermark);
            var journal = await File.ReadAllTextAsync(recoveryFilename,
                timeout.Token);
            Assert.Contains("journal-overflow", journal);
            Assert.DoesNotContain("queued-one", journal);
            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));

            releaseDatabase.TrySetResult();
            await recorder.StopAsync(timeout.Token);
        }
        finally
        {
            releaseDatabase.TrySetResult();
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_DrainsPartialAcknowledgedBatchToPostgreSql()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var persisted = new ConcurrentBag<string>();
        var messageBus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                foreach(var share in call.Arg<IEnumerable<PersistedShare>>())
                    persisted.Add(share.Miner);
                return Task.CompletedTask;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
            }, messageBus);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await recorder.StartAsync(timeout.Token);
        for(var index = 0; index < 41; index++)
            messageBus.SendMessage(new Share
                { PoolId = "ltc-solo", Miner = $"partial-{index}" });

        await recorder.StopAsync(timeout.Token);

        Assert.Equal(41, persisted.Count);
        Assert.Equal(41, persisted.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task StopAsync_DatabaseDeadlineJournalsCompleteUnresolvedBacklog()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-stop-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var messageBus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                databaseEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                    call.Arg<CancellationToken>());
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, messageBus)
        {
            PersistenceQueueCapacity = 64,
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 41; index++)
                messageBus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"deadline-{index}" });

            using var stop = new CancellationTokenSource();
            var stopping = recorder.StopAsync(stop.Token);
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            var journal = await File.ReadAllTextAsync(recoveryFilename);
            for(var index = 0; index < 41; index++)
                Assert.Contains($"deadline-{index}", journal);
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task HostShutdown_ReservesDeadlineForDurableUnresolvedJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-host-drain-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var bus = new MessageBus();
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var journalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJournal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                databaseEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                    call.Arg<CancellationToken>());
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config, bus)
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(100),
            RecoveryJournalFlush = async stream =>
            {
                journalEntered.TrySetResult();
                await releaseJournal.Task;
                await stream.FlushAsync();
                if(stream is FileStream fileStream)
                    fileStream.Flush(true);
                else
                    stream.Flush();
            },
        };

        try
        {
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    Program.ConfigureHostShutdown(services,
                        TimeSpan.FromSeconds(2));
                    services.AddSingleton<IHostedService>(recorder);
                })
                .Build();
            await host.StartAsync();
            bus.SendMessage(new Share
            {
                PoolId = "ltc-solo",
                Miner = "host-budget-share",
                Created = DateTime.UtcNow,
            });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(8));

            var stopping = host.StopAsync();
            await journalEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(stopping.IsCompleted);

            releaseJournal.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("host-budget-share",
                await File.ReadAllTextAsync(recoveryFilename));
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            releaseJournal.TrySetResult();
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_DatabaseAndJournalFailureLatchesNearCapacityBacklog()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-stop-fatal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var processStatus = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus, lifetime);
        var fatalState = new ShareRecoveryFatalState(config, processStatus,
            config.ShareRecoveryStateDirectory);
        var handler = new ShareRecoveryFailureHandler(coordinator,
            new Lazy<ICriticalNotificationSender>(() =>
                Substitute.For<ICriticalNotificationSender>()), fatalState);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                databaseEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                    call.Arg<CancellationToken>());
            });
        var bus = new MessageBus(coordinator);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config, bus,
            handler, coordinator)
        {
            PersistenceQueueCapacity = 256,
            RecoveryJournalFlush = _ => Task.FromException(
                new IOException("injected shutdown journal failure")),
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 257; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"fatal-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var stop = new CancellationTokenSource();
            var stopping = recorder.StopAsync(stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<IOException>(() =>
                stopping.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.True(coordinator.IsFailStopRequested);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
            lifetime.Received(1).StopApplication();
            var latch = await File.ReadAllTextAsync(fatalState.FatalStateFilename);
            Assert.Contains("shareCount=257", latch);
            Assert.Contains("pools=ltc-solo", latch);
        }
        finally
        {
            try
            {
                await recorder.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("mapper")]
    [InlineData("open")]
    [InlineData("begin")]
    [InlineData("batch")]
    [InlineData("block")]
    public async Task PersistenceQueue_UnexpectedFailureJournalsAllUnresolvedAndStops(
        string failureStage)
    {
        await AssertUnexpectedQueueFailureJournalsAsync(failureStage);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("connection")]
    public async Task PersistenceQueue_CommitFailureWithCleanupFailureSuppressesReplayAndReportsExactShares(
        string cleanupStage)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-commit-uncertain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var handler = Substitute.For<IShareRecoveryFailureHandler>();
        var handled = new TaskCompletionSource<IReadOnlyCollection<Share>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        transaction.When(x => x.Commit()).Do(_ =>
            throw new IOException("commit transport failed"));
        var cleanupFailure = new IOException($"{cleanupStage} dispose failed");

        if(cleanupStage == "transaction")
            transaction.When(x => x.Dispose()).Do(_ => throw cleanupFailure);
        else
            connection.When(x => x.Dispose()).Do(_ => throw cleanupFailure);
        handler.StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                handled.TrySetResult(call.Arg<IReadOnlyCollection<Share>>());
                return Task.CompletedTask;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig
            {
                Pools = new[]
                {
                    new PoolConfig
                    {
                        Id = "ltc-solo",
                        Template = new BitcoinTemplate
                            { Symbol = "LTC", Name = "Litecoin" },
                    },
                },
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, bus, handler, Substitute.For<IMiningFailStopCoordinator>());
        var submitted = new Share
        {
            PoolId = "ltc-solo",
            Miner = "commit-uncertain-share",
            BlockOnly = true,
            IsBlockCandidate = true,
            BlockHash = "commit-uncertain-block",
            BlockHeight = 123,
            Created = DateTime.UtcNow,
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(submitted);

            var exact = await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(submitted, Assert.Single(exact));
            Assert.False(File.Exists(recoveryFilename));
            var call = handler.ReceivedCalls().Single(x =>
                x.GetMethodInfo().Name == nameof(
                    IShareRecoveryFailureHandler.StopClusterForUncertainCommitAsync));
            var commitError = Assert.IsType<TransactionCommitOutcomeUncertainException>(
                call.GetArguments()[2]);
            Assert.Same(cleanupFailure,
                commitError.Data[ConnectionFactoryExtensions.CleanupExceptionDataKey]);
            await handler.DidNotReceive().StopClusterAfterJournalAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<string>(),
                Arg.Any<Exception>());
        }
        finally
        {
            try
            {
                await recorder.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("connection")]
    public async Task PersistenceQueue_PostCommitCleanupFailureDoesNotJournalCommittedShares(
        string cleanupStage)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-committed-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var handler = Substitute.For<IShareRecoveryFailureHandler>();
        var handled = new TaskCompletionSource<(IReadOnlyCollection<Share> Shares,
            Exception Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException($"{cleanupStage} dispose failed");
        var bus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);

        if(cleanupStage == "transaction")
            transaction.When(x => x.Dispose()).Do(_ => throw cleanupFailure);
        else
            connection.When(x => x.Dispose()).Do(_ => throw cleanupFailure);

        handler.StopClusterAfterJournalAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                handled.TrySetResult((call.Arg<IReadOnlyCollection<Share>>(),
                    call.Arg<Exception>()));
                return Task.CompletedTask;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, bus, handler, Substitute.For<IMiningFailStopCoordinator>());
        var submitted = new Share
        {
            PoolId = "ltc-solo",
            Miner = "known-committed-cleanup",
            BlockOnly = true,
            Created = DateTime.UtcNow,
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(submitted);

            var result = await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(result.Shares);
            var error = Assert.IsType<TransactionCommittedCleanupException>(result.Error);
            Assert.Same(cleanupFailure, error.InnerException);
            Assert.False(File.Exists(recoveryFilename));
            await handler.DidNotReceive().StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<string>(),
                Arg.Any<Exception>());
        }
        finally
        {
            try
            {
                await recorder.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_BlockInsertCancellationJournalsCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-block-insert-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var blockInsertEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                blockInsertEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                        call.Arg<CancellationToken>())
                    .ContinueWith(_ => false, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default);
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig
            {
                Pools = new[]
                {
                    new PoolConfig
                    {
                        Id = "ltc-solo",
                        Template = new BitcoinTemplate
                            { Symbol = "LTC", Name = "Litecoin" },
                    },
                },
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, bus)
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(50),
        };
        var candidate = CreateDurableCandidate("block-insert-stall");

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(candidate);
            await blockInsertEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await recorder.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("block-insert-stall",
                await File.ReadAllTextAsync(recoveryFilename));
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_UnresponsiveCommitHasFiniteUncertainOutcomeBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-commit-bound-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var handler = Substitute.For<IShareRecoveryFailureHandler>();
        var commitEntered = new ManualResetEventSlim();
        var releaseCommit = new ManualResetEventSlim();
        var uncertain = new TaskCompletionSource<IReadOnlyCollection<Share>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        transaction.When(x => x.Commit()).Do(_ =>
        {
            commitEntered.Set();
            releaseCommit.Wait();
        });
        handler.StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                uncertain.TrySetResult(call.Arg<IReadOnlyCollection<Share>>());
                return Task.CompletedTask;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, bus, handler, Substitute.For<IMiningFailStopCoordinator>())
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(50),
            ShutdownRecoveryCompletionTimeout = TimeSpan.FromMilliseconds(100),
        };
        var submitted = new Share
        {
            PoolId = "ltc-solo",
            Miner = "unresponsive-commit",
            BlockOnly = true,
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(submitted);
            Assert.True(commitEntered.Wait(TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                recorder.StopAsync(CancellationToken.None));

            Assert.Same(submitted, Assert.Single(await uncertain.Task
                .WaitAsync(TimeSpan.FromSeconds(1))));
            Assert.False(File.Exists(recoveryFilename));
        }
        finally
        {
            releaseCommit.Set();
            try
            {
                await recorder.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            commitEntered.Dispose();
            releaseCommit.Dispose();
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MergedMiningProductionAdmission_JournalFailureCompletesFailStopWithoutAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-admission-failstop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var processStatus = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus, lifetime);
        var bus = new MessageBus(coordinator);
        var fatalState = new ShareRecoveryFatalState(config, processStatus,
            config.ShareRecoveryStateDirectory);
        var handler = new ShareRecoveryFailureHandler(coordinator,
            new Lazy<ICriticalNotificationSender>(() =>
                Substitute.For<ICriticalNotificationSender>()), fatalState);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDatabase = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                databaseEntered.TrySetResult();
                await releaseDatabase.Task;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config, bus,
            handler, coordinator)
        {
            EmergencyJournalQueueCapacity = 2,
            PersistenceQueueCapacity = 300,
            RecoveryJournalFlush = _ => Task.FromException(
                new IOException("injected journal failure")),
        };
        var acknowledged = false;

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 250; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"blocked-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(var index = 0; index < 300; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"queued-{index}" });

            var returnedShare = new Share
                { PoolId = "ltc-solo", Miner = "journal-failure" };
            var statisticalShare =
                MergedMiningBitcoinJobManager.CreateStatisticalShare(returnedShare);
            using var acceptance = coordinator.AcquireSubmissionAcceptance();
            acceptance.PublishShare(bus, statisticalShare);
            returnedShare.SetPersistenceAdmission(
                statisticalShare.PersistenceAdmission);
            returnedShare.StatisticalRecordEmitted = true;
            await Assert.ThrowsAnyAsync<IOException>(() =>
                returnedShare.PersistenceAdmission.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<OperationCanceledException>(() =>
                acceptance.QueueResponse(() => acknowledged = true));

            Assert.False(acknowledged);
            Assert.True(coordinator.IsFailStopRequested);
            Assert.True(coordinator.Token.IsCancellationRequested);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
            lifetime.Received(1).StopApplication();
            Assert.True(File.Exists(fatalState.FatalStateFilename));
            var latch = await File.ReadAllTextAsync(fatalState.FatalStateFilename);
            Assert.Contains("shareCount=551", latch);
            Assert.Contains("pools=ltc-solo", latch);
            Assert.DoesNotContain(nameof(LockRecursionException), latch);
        }
        finally
        {
            releaseDatabase.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await recorder.StopAsync(stop.Token);
            }
            catch
            {
                // The injected journal failure deliberately faults the hosted service.
            }

            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BothPersistenceChannelsFull_CapturesPostGateUnresolvedSet()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-admission-full-capture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var processStatus = new ProcessStatus();
        using var coordinator = new MiningFailStopCoordinator(processStatus,
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus(coordinator);
        var fatalState = new ShareRecoveryFatalState(config, processStatus,
            config.ShareRecoveryStateDirectory);
        var handler = new ShareRecoveryFailureHandler(coordinator,
            new Lazy<ICriticalNotificationSender>(() =>
                Substitute.For<ICriticalNotificationSender>()), fatalState);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDatabase = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var journalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseJournal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                databaseEntered.TrySetResult();
                await releaseDatabase.Task;
                throw new IOException("injected database failure after capture");
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config, bus,
            handler, coordinator)
        {
            PersistenceQueueCapacity = 250,
            EmergencyJournalQueueCapacity = 1,
            RecoveryJournalFlush = async _ =>
            {
                journalEntered.TrySetResult();
                await releaseJournal.Task;
                throw new IOException("injected emergency journal failure");
            },
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 250; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"database-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for(var index = 0; index < 250; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"primary-buffered-{index}" });
            bus.SendMessage(new Share
                { PoolId = "ltc-solo", Miner = "emergency-active" });
            await journalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bus.SendMessage(new Share
                { PoolId = "ltc-solo", Miner = "emergency-buffered" });

            using var acceptance = coordinator.AcquireSubmissionAcceptance();
            var finalShare = new Share
                { PoolId = "ltc-solo", Miner = "both-full-reporting-share" };
            var publishError = Assert.ThrowsAny<IOException>(() =>
                acceptance.PublishShare(bus, finalShare));
            Assert.Contains("both unavailable", publishError.Message);
            Assert.Throws<OperationCanceledException>(() =>
                acceptance.QueueResponse(() => { }));

            var latch = File.ReadAllLines(fatalState.FatalStateFilename);
            var sidecar = Assert.Single(latch.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            var captured = File.ReadLines(sidecar)
                .Select(line => line["shareJsonBase64=".Length..])
                .Select(Convert.FromBase64String)
                .Select(Encoding.UTF8.GetString)
                .Select(JsonConvert.DeserializeObject<Share>)
                .ToArray();

            Assert.Equal(503, captured.Length);
            Assert.Equal(503, captured.Select(x => x.Miner)
                .Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(captured, x => x.Miner == "both-full-reporting-share");
            Assert.Contains(captured, x => x.Miner == "emergency-active");
            Assert.Contains(captured, x => x.Miner == "emergency-buffered");
            Assert.Contains(captured, x => x.Miner == "primary-buffered-249");
        }
        finally
        {
            releaseJournal.TrySetResult();
            releaseDatabase.TrySetResult();
            try
            {
                await recorder.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Both injected durability targets deliberately fail.
            }

            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MergedMiningProductionAdmission_WaitsForJournalAndTerminalAnchor()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-merged-admission-success-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var processStatus = new ProcessStatus();
        using var coordinator = new MiningFailStopCoordinator(processStatus,
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus(coordinator);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDatabase = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var anchorEntered = new ManualResetEventSlim();
        using var releaseAnchor = new ManualResetEventSlim();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                databaseEntered.TrySetResult();
                await releaseDatabase.Task;
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config, bus)
        {
            EmergencyJournalQueueCapacity = 2,
            PersistenceQueueCapacity = 300,
        };
        var terminalState = new ShareRecoveryTerminalState(
            config.ShareRecoveryFile, config.ShareRecoveryStateDirectory);
        recorder.RecoveryTerminalStateWrite = (sequence, digest) =>
        {
            anchorEntered.Set();
            releaseAnchor.Wait();
            terminalState.Write(sequence, digest);
        };
        var acknowledged = false;

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 250; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"blocked-success-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for(var index = 0; index < 300; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"queued-success-{index}" });

            var returnedShare = new Share
                { PoolId = "ltc-solo", Miner = "merged-journal-success" };
            var statisticalShare =
                MergedMiningBitcoinJobManager.CreateStatisticalShare(returnedShare);
            using var acceptance = coordinator.AcquireSubmissionAcceptance();
            acceptance.PublishShare(bus, statisticalShare);
            returnedShare.SetPersistenceAdmission(
                statisticalShare.PersistenceAdmission);
            returnedShare.StatisticalRecordEmitted = true;
            var response = Task.Run(async () =>
            {
                await returnedShare.PersistenceAdmission;
                acceptance.QueueResponse(() => acknowledged = true);
            });

            Assert.True(anchorEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(response.IsCompleted);
            Assert.False(acknowledged);
            Assert.True(File.Exists(config.ShareRecoveryFile));
            Assert.False(File.Exists(recorder.RecoveryTerminalStateFilename));

            releaseAnchor.Set();
            await response.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(acknowledged);
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
            Assert.False(coordinator.IsFailStopRequested);
            Assert.Equal(0, processStatus.ExitCode);
        }
        finally
        {
            releaseAnchor.Set();
            releaseDatabase.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await recorder.StopAsync(stop.Token);
            ShareRecorder.ForgetRecoveryWriteStateForTests(config.ShareRecoveryFile);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_RejectsDeletionOfFinalCompleteFrame()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-terminal-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        };
        var processStatus = new ProcessStatus();
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "frame-one" },
            });
            var firstFrame = await File.ReadAllBytesAsync(recoveryFilename);
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "frame-two" },
            });
            await File.WriteAllBytesAsync(recoveryFilename, firstFrame);

            var state = new ShareRecoveryFatalState(config, processStatus,
                stateDirectory);
            var error = Assert.Throws<PoolStartupException>(() =>
                state.EnsureStartupAllowed());

            Assert.Contains("terminal anchor", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_FirstRuntimeAppendRejectsEarlierValidTail()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-first-append-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var first = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "frame-one" },
            });
            var firstFrame = await File.ReadAllBytesAsync(recoveryFilename);
            await first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "frame-two" },
            });
            var anchor = await File.ReadAllBytesAsync(first.RecoveryTerminalStateFilename);
            await File.WriteAllBytesAsync(recoveryFilename, firstFrame);
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            var fresh = new ShareRecorder(Substitute.For<IConnectionFactory>(),
                AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
                config, Substitute.For<IMessageBus>());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fresh.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "must-not-append" },
                }));

            Assert.Contains("terminal anchor", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(firstFrame, await File.ReadAllBytesAsync(recoveryFilename));
            Assert.Equal(anchor,
                await File.ReadAllBytesAsync(first.RecoveryTerminalStateFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_FirstRuntimeAppendRejectsMissingJournal()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-first-append-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var first = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "anchored" },
            });
            File.Delete(recoveryFilename);
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            var fresh = new ShareRecorder(Substitute.For<IConnectionFactory>(),
                AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
                config, Substitute.For<IMessageBus>());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fresh.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "replacement" },
                }));

            Assert.Contains("missing while its independent terminal anchor",
                error.Message);
            Assert.False(File.Exists(recoveryFilename));
            Assert.True(File.Exists(first.RecoveryTerminalStateFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_ChainedJournalWithoutAnchorFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-unanchored-v2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var first = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "anchored" },
            });
            File.Delete(first.RecoveryTerminalStateFilename);
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            var fresh = new ShareRecorder(Substitute.For<IConnectionFactory>(),
                AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
                config, Substitute.For<IMessageBus>());

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fresh.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "must-not-adopt" },
                }));

            Assert.Contains("unanchored v2 tail", error.Message);
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_NoJournalOrAnchorCreatesFirstCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-first-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "first-commit" },
            });

            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryTerminalAnchor_UpdateFailureRollsBackJournalFrame()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-terminal-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "anchored" },
            });
            var original = await File.ReadAllBytesAsync(recoveryFilename);
            recorder.RecoveryTerminalStateWrite = (_, _) =>
                throw new IOException("injected terminal-anchor failure");

            var error = await Assert.ThrowsAsync<IOException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "not-acknowledged" },
                }));

            Assert.Contains("terminal-anchor failure", error.Message);
            Assert.Equal(original, await File.ReadAllBytesAsync(recoveryFilename));
            var processStatus = new ProcessStatus();
            new ShareRecoveryFatalState(config, processStatus,
                    config.ShareRecoveryStateDirectory)
                .EnsureStartupAllowed();
            Assert.Equal(0, processStatus.ExitCode);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_ArchivesSourceAndRemovesTerminalAnchor()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-terminal-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.TryRegisterRecoveryImportAsync(connection, transaction,
                Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>())
            .Returns(true);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config,
            Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share
                {
                    PoolId = "ltc-solo",
                    Miner = "imported",
                    Created = DateTime.UtcNow,
                },
            });
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));

            var archive = await recorder.RecoverSharesAsync(recoveryFilename);

            Assert.NotNull(archive);
            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(recoveryFilename));
            Assert.False(File.Exists(recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_ArchiveFailureBlocksStartupAndAppendUntilRetried()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-retirement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.TryRegisterRecoveryImportAsync(connection, transaction,
                Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>())
            .Returns(true);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config,
            Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "committed-once" },
            });
            recorder.RecoveryArchiveMove = (_, _) =>
                throw new IOException("injected archive failure after commit");

            var archiveError = await Assert.ThrowsAsync<IOException>(() =>
                recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("archive failure after commit", archiveError.Message);
            transaction.Received(1).Commit();
            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(recorder.RecoveryImportStateFilename));
            var startupStatus = new ProcessStatus();
            var startupError = Assert.Throws<PoolStartupException>(() =>
                new ShareRecoveryFatalState(config, startupStatus,
                    config.ShareRecoveryStateDirectory).EnsureStartupAllowed());
            Assert.Contains("unfinished committed", startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                startupStatus.ExitCode);
            var appendError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "must-not-grow" },
                }));
            Assert.Contains("unfinished committed", appendError.Message);

            recorder.RecoveryArchiveMove = (source, destination) =>
                File.Move(source, destination);
            var archive = await recorder.RecoverSharesAsync(recoveryFilename);

            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(recoveryFilename));
            Assert.False(File.Exists(recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(recorder.RecoveryTerminalStateFilename));
            await shareRepository.Received(1).TryRegisterRecoveryImportAsync(connection,
                transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>());
            await shareRepository.Received(1).BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_CommittedMarkerRejectsSameLengthReplacement()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-replaced-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var fixture = CreateConfiguredRecoveryFixture(config);

        try
        {
            await fixture.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "same-length-original" },
            });
            fixture.Recorder.RecoveryArchiveMove = (_, _) =>
                throw new IOException("retain committed source");
            await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            var bytes = await File.ReadAllBytesAsync(recoveryFilename);
            var original = Encoding.UTF8.GetBytes("same-length-original");
            var replacement = Encoding.UTF8.GetBytes("same-length-replaced");
            Assert.Equal(original.Length, replacement.Length);
            var offset = bytes.AsSpan().IndexOf(original);
            Assert.True(offset >= 0);
            replacement.CopyTo(bytes.AsSpan(offset, replacement.Length));
            await File.WriteAllBytesAsync(recoveryFilename, bytes);

            fixture.Recorder.RecoveryArchiveMove = File.Move;
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            await fixture.ShareRepository.Received(1)
                .TryRegisterRecoveryImportAsync(fixture.Connection,
                    fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_CommittedMarkerRejectsExtendedValidChain()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-extended-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var fixture = CreateConfiguredRecoveryFixture(config);

        try
        {
            await fixture.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "imported-before-commit" },
            });
            fixture.Recorder.RecoveryArchiveMove = (_, _) =>
                throw new IOException("retain committed source");
            await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            var alternateConfig = new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory,
                    "alternate-state"),
            };
            var alternate = CreateConfiguredRecoveryFixture(alternateConfig);
            await alternate.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "never-imported-extension" },
            });

            await using(var stream = File.OpenRead(recoveryFilename))
            {
                var tail = ShareRecorder.ValidateRecoveryJournalDetailed(stream,
                    recoveryFilename);
                new ShareRecoveryTerminalState(recoveryFilename,
                        config.ShareRecoveryStateDirectory)
                    .Write(tail.Sequence, tail.FrameDigest);
            }

            fixture.Recorder.RecoveryArchiveMove = File.Move;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("no longer matches its import marker", error.Message);
            Assert.Contains("never-imported-extension",
                await File.ReadAllTextAsync(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            await fixture.ShareRepository.Received(1)
                .TryRegisterRecoveryImportAsync(fixture.Connection,
                    fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_RejectsHardLinkAliasOfActiveSource()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var aliasFilename = Path.Combine(directory, "reviewed-copy.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var fixture = CreateConfiguredRecoveryFixture(config);

        try
        {
            await fixture.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "alias-source" },
            });
            CreateHardLinkForTest(aliasFilename, recoveryFilename);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(aliasFilename));

            Assert.Contains("filesystem alias", error.Message);
            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(aliasFilename));
            await fixture.ShareRepository.DidNotReceiveWithAnyArgs()
                .TryRegisterRecoveryImportAsync(default, default, default,
                    default, default, default);
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_AnchorRemovalFailureResumesAfterArchiveRename()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-anchor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var fixture = CreateConfiguredRecoveryFixture(config);

        try
        {
            await fixture.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "archive-before-anchor" },
            });
            fixture.Recorder.RecoveryTerminalStateRemove = () =>
                throw new IOException("injected anchor-retirement failure");

            var error = await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("anchor-retirement failure", error.Message);
            Assert.False(File.Exists(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            var marker = new ShareRecoveryImportState(recoveryFilename,
                config.ShareRecoveryStateDirectory).TryRead();
            Assert.NotNull(marker);
            Assert.True(File.Exists(marker.ArchiveFilename));

            fixture.Recorder.RecoveryTerminalStateRemove = () =>
                new ShareRecoveryTerminalState(recoveryFilename,
                    config.ShareRecoveryStateDirectory).RemoveAfterArchive();
            var archive = await fixture.Recorder.RecoverSharesAsync(recoveryFilename);

            Assert.Equal(marker.ArchiveFilename, archive);
            Assert.False(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_ArchiveDirectorySyncFailureRetainsCommittedMarker()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-fsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var fixture = CreateConfiguredRecoveryFixture(config);

        try
        {
            await fixture.Recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "fsync-after-rename" },
            });
            fixture.Recorder.RecoveryDirectorySync = _ =>
                throw new IOException("injected archive-directory fsync failure");

            var error = await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("archive-directory fsync failure", error.Message);
            Assert.False(File.Exists(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));

            fixture.Recorder.RecoveryDirectorySync = _ => { };
            var archive = await fixture.Recorder.RecoverSharesAsync(recoveryFilename);

            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void BitcoinPool_DoesNotRepublishManagerEmittedStatisticalShare()
    {
        Assert.True(BitcoinPool.ShouldPublishStatisticalShare(new Share()));
        Assert.False(BitcoinPool.ShouldPublishStatisticalShare(new Share
        {
            StatisticalRecordEmitted = true,
        }));
    }

    [Fact]
    public async Task RecoverSharesAsync_MissingFileThrows()
    {
        var fixture = CreateRecoveryFixture();
        var filename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            fixture.Recorder.RecoverSharesAsync(filename));
    }

    [Fact]
    public async Task RecoverSharesAsync_InvalidRecordsThrow()
    {
        var fixture = CreateRecoveryFixture();
        var filename = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(filename, "this is not a share record");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));
        }

        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_HundredValidThenInvalid_WritesNothingOnRepeatedFailure()
    {
        var fixture = CreateRecoveryFixture();
        var records = Enumerable.Range(0, 100)
            .Select(x => RecoveryShareJson(x))
            .Append("not-json");
        var filename = await WriteRecoveryFileAsync(records);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
            await fixture.ShareRepository.DidNotReceive().BatchInsertAsync(
                Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
        }

        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_ValidInvalidValid_WritesNothing()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(new[]
        {
            RecoveryShareJson(1),
            "not-json",
            RecoveryShareJson(2),
        });

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            await fixture.ConnectionFactory.DidNotReceive().OpenConnectionAsync();
        }

        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_SecondBatchFailure_RollsBackAndRetriesWholeFile()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(Enumerable.Range(0, 200)
            .Select(x => RecoveryShareJson(x)));
        fixture.ShareRepository.BatchInsertAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask,
                Task.FromException(new IOException("database unavailable")),
                Task.CompletedTask, Task.CompletedTask);

        string archiveFilename = null;

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            fixture.Transaction.Received(1).Rollback();
            fixture.Transaction.DidNotReceive().Commit();

            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            fixture.Transaction.Received(1).Rollback();
            fixture.Transaction.Received(1).Commit();
            await fixture.ShareRepository.Received(4).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>());
            Assert.False(File.Exists(filename));
            Assert.True(File.Exists(archiveFilename));
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_ValidFile_CommitsEveryShareExactlyOnce()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(Enumerable.Range(0, 150)
            .Select(x => RecoveryShareJson(x)));

        string archiveFilename = null;

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            fixture.Transaction.Received(1).Commit();
            fixture.Transaction.DidNotReceive().Rollback();
            await fixture.ShareRepository.Received(2).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>());
            Assert.False(File.Exists(filename));
            Assert.True(File.Exists(archiveFilename));
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_SuccessfulOrdinaryReplay_IsRetiredWithoutReinsert()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(new[] { RecoveryShareJson(1) });
        fixture.ShareRepository.TryRegisterRecoveryImportAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>())
            .Returns(true, false);
        string archiveFilename = null;
        string replayArchiveFilename = null;

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);
            File.Copy(archiveFilename, filename);

            replayArchiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            await fixture.ShareRepository.Received(1).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
            Assert.False(File.Exists(filename));
            Assert.True(File.Exists(replayArchiveFilename));
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
            if(replayArchiveFilename != null)
                File.Delete(replayArchiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_SemanticallyEquivalentReplay_IsRetiredWithoutReinsert()
    {
        var fixture = CreateRecoveryFixture();
        var originals = new[] { RecoveryShareJson(7), RecoveryShareJson(8) };
        var filename = await WriteRecoveryFileAsync(originals);
        var registeredHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fixture.ShareRepository.TryRegisterRecoveryImportAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 2,
                Arg.Any<CancellationToken>())
            .Returns(call => registeredHashes.Add(call.ArgAt<string>(2)));
        string archiveFilename = null;
        string replayArchiveFilename = null;

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            var equivalent = originals.Reverse().Select(original =>
            {
                var parsed = Newtonsoft.Json.Linq.JObject.Parse(original);
                return new Newtonsoft.Json.Linq.JObject(
                    parsed.Properties().Reverse().Select(x =>
                        new Newtonsoft.Json.Linq.JProperty(x.Name, x.Value)))
                    .ToString(Formatting.None);
            });
            await File.WriteAllTextAsync(filename,
                $"# regenerated recovery file\r\n\r\n  {string.Join("  \r\n  ", equivalent)}  \r\n");

            replayArchiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            Assert.Single(registeredHashes);
            await fixture.ShareRepository.Received(1).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
            Assert.False(File.Exists(filename));
            Assert.True(File.Exists(replayArchiveFilename));
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
            if(replayArchiveFilename != null)
                File.Delete(replayArchiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_BlockOnlyReplay_IsRetiredWithoutDuplicateBlock()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(new[]
        {
            RecoveryShareJson(1, true),
        });
        fixture.BlockRepository.InsertAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<Block>())
            .Returns(true);
        fixture.ShareRepository.TryRegisterRecoveryImportAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>())
            .Returns(true, false);
        string archiveFilename = null;
        string replayArchiveFilename = null;

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);
            File.Copy(archiveFilename, filename);

            replayArchiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            await fixture.ShareRepository.DidNotReceive().BatchInsertAsync(
                Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
            await fixture.BlockRepository.Received(1).InsertAsync(
                fixture.Connection, fixture.Transaction, Arg.Any<Block>());
            fixture.MessageBus.Received(1).SendMessage(
                Arg.Any<BlockFoundNotification>(), Arg.Any<string>());
            Assert.False(File.Exists(filename));
            Assert.True(File.Exists(replayArchiveFilename));
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
            if(replayArchiveFilename != null)
                File.Delete(replayArchiveFilename);
        }
    }

    [Fact]
    public void GetSharesForPersistence_ExcludesBlockOnlyCandidates()
    {
        var regularShare = new Share { PoolId = "ltc-solo" };
        var parentBlockCandidate = new Share { PoolId = "ltc-solo", IsBlockCandidate = true };
        var auxiliaryBlockCandidate = new Share
        {
            PoolId = "doge-solo",
            IsBlockCandidate = true,
            BlockOnly = true,
        };

        var result = ShareRecorder.GetSharesForPersistence(new[]
        {
            regularShare,
            parentBlockCandidate,
            auxiliaryBlockCandidate,
        }).ToArray();

        Assert.Equal(new[] { regularShare, parentBlockCandidate }, result);
    }

    [Fact]
    public void MergedMiningFields_RoundTripThroughShareRelayWireFormat()
    {
        var share = new Share
        {
            PoolId = "doge-solo",
            Miner = "DExampleAddress",
            IsBlockCandidate = true,
            BlockOnly = true,
            BlockType = "auxpow",
            BlockRecordEmitted = true,
            PreserveCreated = true,
            StatisticalRecordEmitted = true,
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, share);
        stream.Position = 0;

        var result = Serializer.Deserialize<Share>(stream);

        Assert.True(result.BlockOnly);
        Assert.True(result.IsBlockCandidate);
        Assert.Equal(share.PoolId, result.PoolId);
        Assert.Equal(share.Miner, result.Miner);
        Assert.Equal("auxpow", result.BlockType);
        Assert.True(result.BlockRecordEmitted);
        Assert.True(result.PreserveCreated);
        Assert.False(result.StatisticalRecordEmitted);
    }

    [Fact]
    public void AcceptedMergedParent_IsOrdinaryForLegacyRelayReceiver()
    {
        var share = new Share
        {
            PoolId = "ltc-solo",
            IsBlockCandidate = true,
            BlockType = "merged-parent",
            TransactionConfirmationData = "coinbase-txid",
            Created = DateTime.UtcNow,
        };

        MergedMiningBitcoinJobManager.MarkParentBlockRecordEmitted(share);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, share);
        stream.Position = 0;
        var legacy = Serializer.Deserialize<LegacyRelayShare>(stream);

        Assert.True(share.BlockRecordEmitted);
        Assert.False(share.IsBlockCandidate);
        Assert.Null(share.BlockType);
        Assert.Null(share.TransactionConfirmationData);
        Assert.False(legacy.IsBlockCandidate);
        Assert.Equal(share.PoolId, legacy.PoolId);
        Assert.Equal(share.Created, legacy.Created);
    }

    [Fact]
    public void RecoveryContentHasher_IsOrderIndependentAndCardinalitySensitive()
    {
        var settings = new JsonSerializerSettings();
        var first = new ShareRecorder.RecoveryContentHasher(settings);
        var second = new ShareRecorder.RecoveryContentHasher(settings);
        var differentCardinality = new ShareRecorder.RecoveryContentHasher(settings);
        var a = Encoding.UTF8.GetBytes("normalized-a");
        var b = Encoding.UTF8.GetBytes("normalized-b");

        first.AppendNormalizedRecord(a);
        first.AppendNormalizedRecord(b);
        first.AppendNormalizedRecord(a);
        second.AppendNormalizedRecord(a);
        second.AppendNormalizedRecord(a);
        second.AppendNormalizedRecord(b);
        differentCardinality.AppendNormalizedRecord(a);
        differentCardinality.AppendNormalizedRecord(b);

        Assert.Equal(first.GetHash(), second.GetHash());
        Assert.NotEqual(first.GetHash(), differentCardinality.GetHash());
    }

    [Fact]
    public void RecoveryContentHasher_MillionRecordsRetainsConstantDigestState()
    {
        var hasher = new ShareRecorder.RecoveryContentHasher(
            new JsonSerializerSettings());
        var record = Encoding.UTF8.GetBytes("production-scale-normalized-record");

        for(var i = 0; i < 1_000_000; i++)
            hasher.AppendNormalizedRecord(record);

        Assert.Equal(1_000_000UL, hasher.RecordCount);
        Assert.Equal(128, hasher.AccumulatorStorageBytes);
        Assert.Equal(64, hasher.GetHash().Length);
    }

    [Fact]
    public async Task PersistSharesCoreAsync_OriginalShareDoesNotDuplicateEmittedBlockRecord()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new PoolConfig[0] }, messageBus);
        var share = new Share
        {
            PoolId = "ltc-solo",
            Miner = "LExampleAddress",
            IsBlockCandidate = true,
            BlockRecordEmitted = true,
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { share });

        await shareRepository.Received(1).BatchInsertAsync(connection, transaction,
            Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
        await blockRepository.DidNotReceive().InsertAsync(connection, transaction,
            Arg.Any<Block>());
    }

    [Fact]
    public async Task PersistSharesCoreAsync_WritesBlockOnlyCandidateWithoutShareRow()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new PoolConfig[0] }, messageBus);
        var candidate = new Share
        {
            PoolId = "doge-solo",
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            IsBlockCandidate = true,
            BlockOnly = true,
            BlockType = "auxpow",
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        await shareRepository.DidNotReceive().BatchInsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<IEnumerable<PersistedShare>>(),
            Arg.Any<CancellationToken>());
        await blockRepository.Received(1).InsertAsync(connection, transaction,
            Arg.Is<Block>(x => x.PoolId == candidate.PoolId &&
                x.BlockHeight == (ulong) candidate.BlockHeight &&
                x.Type == candidate.BlockType &&
                x.Hash == candidate.BlockHash &&
                x.TransactionConfirmationData == candidate.TransactionConfirmationData));
        transaction.Received(1).Commit();
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_CommitsSynchronouslyBeforeReturning()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);
        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), shareRepository, blockRepository,
            new ClusterConfig { Pools = Array.Empty<PoolConfig>() }, messageBus);
        var candidate = new Share
        {
            PoolId = "doge-solo",
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        await recorder.PersistBlockCandidateAsync(candidate);

        Received.InOrder(() =>
        {
            blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>());
            transaction.Commit();
        });
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_RejectsTypeWithoutIdempotencyRule()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), blockRepository,
            new ClusterConfig { Pools = Array.Empty<PoolConfig>() },
            Substitute.For<IMessageBus>());
        var candidate = new Share
        {
            PoolId = "future-pool",
            BlockHash = "future-block-hash",
            BlockType = "future-block-only-type",
            IsBlockCandidate = true,
            BlockOnly = true,
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            recorder.PersistBlockCandidateAsync(candidate));

        Assert.Contains("no declared stable persistence identity", error.Message);
        await connectionFactory.DidNotReceive().OpenConnectionAsync();
        await blockRepository.DidNotReceive().InsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
            Arg.Any<Block>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_PostCommitCleanupFailureNeverJournalsCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-candidate-committed-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var recoveryHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var candidateHandler = Substitute.For<ICandidatePersistenceFailureHandler>();
        var cleanupFailure = new IOException("transaction dispose failed");
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>(),
            Arg.Any<CancellationToken>()).Returns(true);
        transaction.When(x => x.Dispose()).Do(_ => throw cleanupFailure);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), blockRepository,
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, Substitute.For<IMessageBus>(), recoveryHandler,
            Substitute.For<IMiningFailStopCoordinator>(), candidateHandler);
        var candidate = CreateDurableCandidate("known-committed-cleanup");

        try
        {
            var error = await Assert.ThrowsAsync<TransactionCommittedCleanupException>(
                () => recorder.PersistBlockCandidateAsync(candidate));

            Assert.Same(cleanupFailure, error.InnerException);
            await recoveryHandler.Received(1).StopClusterAfterCommittedCleanupAsync(
                Arg.Is<IReadOnlyCollection<Share>>(x => x.Single() == candidate),
                recoveryFilename, error);
            await recoveryHandler.DidNotReceive().StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<string>(),
                Arg.Any<Exception>());
            await candidateHandler.DidNotReceive().StopClusterAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<Exception>(),
                Arg.Any<Exception>(), Arg.Any<bool>());
            Assert.False(File.Exists(recoveryFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_UncertainCommitCleanupFailureNeverJournalsCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-candidate-uncertain-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var recoveryHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var candidateHandler = Substitute.For<ICandidatePersistenceFailureHandler>();
        var commitFailure = new IOException("commit transport failed");
        var cleanupFailure = new IOException("connection dispose failed");
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>(),
            Arg.Any<CancellationToken>()).Returns(true);
        transaction.When(x => x.Commit()).Do(_ => throw commitFailure);
        connection.When(x => x.Dispose()).Do(_ => throw cleanupFailure);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), blockRepository,
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
            }, Substitute.For<IMessageBus>(), recoveryHandler,
            Substitute.For<IMiningFailStopCoordinator>(), candidateHandler);
        var candidate = CreateDurableCandidate("uncertain-cleanup");

        try
        {
            var error = await Assert.ThrowsAsync<
                TransactionCommitOutcomeUncertainException>(() =>
                recorder.PersistBlockCandidateAsync(candidate));

            Assert.Same(commitFailure, error.InnerException);
            Assert.Same(cleanupFailure,
                error.Data[ConnectionFactoryExtensions.CleanupExceptionDataKey]);
            await recoveryHandler.Received(1).StopClusterForUncertainCommitAsync(
                Arg.Is<IReadOnlyCollection<Share>>(x => x.Single() == candidate),
                recoveryFilename, error);
            await candidateHandler.DidNotReceive().StopClusterAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<Exception>(),
                Arg.Any<Exception>(), Arg.Any<bool>());
            Assert.False(File.Exists(recoveryFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_ShutdownBoundsDatabaseAndFlushesRecoveryJournal()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var failureHandler = Substitute.For<ICandidatePersistenceFailureHandler>();
        var mapper = AutoMapperFactory.CreateMapper();
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
            }, messageBus, failureHandler)
        {
            ShutdownDatabaseAttemptTimeout = TimeSpan.FromMilliseconds(100),
        };
        var candidate = new Share
        {
            PoolId = "doge-solo",
            Miner = "DShutdownBeneficiary",
            BlockHeight = 456,
            BlockHash = "shutdown-doge-block-hash",
            BlockType = "auxpow-claim",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData =
                "auxpow-claim:shutdown-doge-block-hash:parent-header:0",
        };

        try
        {
            var persistence = recorder.PersistBlockCandidateAsync(candidate);
            await databaseAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            recorder.BeginShutdown();
            await persistence.WaitAsync(TimeSpan.FromSeconds(2));

            await connectionFactory.Received(1).OpenConnectionAsync();
            var persisted = (await File.ReadAllLinesAsync(recoveryFilename))
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('#'))
                .Select(JsonConvert.DeserializeObject<Share>)
                .Single();
            Assert.Equal(candidate.PoolId, persisted.PoolId);
            Assert.Equal(candidate.Miner, persisted.Miner);
            Assert.Equal(candidate.BlockHash, persisted.BlockHash);
            Assert.Equal(candidate.BlockType, persisted.BlockType);
            Assert.Equal(candidate.TransactionConfirmationData,
                persisted.TransactionConfirmationData);
            Assert.True(persisted.BlockOnly);
            Assert.True(persisted.IsBlockCandidate);
            Assert.Empty(failureHandler.ReceivedCalls());
        }
        finally
        {
            databaseAttempt.TrySetException(new TimeoutException(
                "simulated late PostgreSQL failure"));
            await Task.Delay(25);
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_ConcurrentRecorderInstancesSerializeByCanonicalFilename()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var mapper = AutoMapperFactory.CreateMapper();
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
        };

        ShareRecorder CreateRecorder() => new(
            Substitute.For<IConnectionFactory>(), mapper,
            new JsonSerializerSettings(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), config,
            Substitute.For<IMessageBus>());

        var ordinaryRecorder = CreateRecorder();
        var candidateRecorder = CreateRecorder();
        var ordinaryShare = new Share
        {
            PoolId = "ltc-solo",
            Miner = "LStatisticalMiner",
            Difficulty = 4,
            Created = DateTime.UtcNow,
        };
        var candidate = new Share
        {
            PoolId = "doge-solo",
            Miner = "DDurableBeneficiary",
            BlockHeight = 987,
            BlockHash = "concurrent-journal-doge-block",
            BlockType = "auxpow",
            BlockOnly = true,
            IsBlockCandidate = true,
            TransactionConfirmationData =
                "auxpow-block:concurrent-journal-doge-block",
            Created = DateTime.UtcNow,
        };

        Assert.Same(ordinaryRecorder.RecoveryWriteGate,
            candidateRecorder.RecoveryWriteGate);

        try
        {
            await ordinaryRecorder.RecoveryWriteGate.WaitAsync();
            Task ordinaryWrite;
            Task candidateWrite;

            try
            {
                ordinaryWrite = ordinaryRecorder.WriteRecoveryJournalAsync(
                    new[] { ordinaryShare });
                candidateWrite = candidateRecorder.WriteRecoveryJournalAsync(
                    new[] { candidate });
                await Task.Delay(25);
                Assert.False(ordinaryWrite.IsCompleted);
                Assert.False(candidateWrite.IsCompleted);
            }
            finally
            {
                ordinaryRecorder.RecoveryWriteGate.Release();
            }

            await Task.WhenAll(ordinaryWrite, candidateWrite)
                .WaitAsync(TimeSpan.FromSeconds(2));

            var records = (await File.ReadAllLinesAsync(recoveryFilename))
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('#'))
                .Select(JsonConvert.DeserializeObject<Share>)
                .ToArray();
            Assert.Equal(2, records.Length);
            Assert.Single(records, x => x.BlockHash == candidate.BlockHash &&
                x.Miner == candidate.Miner && x.BlockOnly);
            Assert.Single(records, x => x.Miner == ordinaryShare.Miner &&
                !x.BlockOnly);
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_PartialAppendFailureRollsBackBeforeNextWrite()
    {
        var original = Encoding.UTF8.GetBytes(
            "# existing journal\n{\"poolId\":\"ltc-solo\",\"miner\":\"first\"}\n");
        var payload = Encoding.UTF8.GetBytes(
            "{\"poolId\":\"ltc-solo\",\"miner\":\"second\"}\n");
        await using var stream = new PartialWriteFailureStream(original, 19);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ShareRecorder.AppendRecoveryJournalAsync(stream, payload));

        Assert.Contains("simulated recovery-journal write failure", error.Message);
        Assert.Equal(original, stream.ToArray());

        await ShareRecorder.AppendRecoveryJournalAsync(stream, payload);

        Assert.Equal(original.Concat(payload), stream.ToArray());
    }

    [Fact]
    public async Task RecoveryJournal_IncompleteExistingTailIsNotExtended()
    {
        var recoveryFilename = Path.GetTempFileName();
        var incomplete = Encoding.UTF8.GetBytes(
            "# existing journal\n{\"poolId\":\"ltc-solo\",\"miner\"");
        await File.WriteAllBytesAsync(recoveryFilename, incomplete);
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>());

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share
                    {
                        PoolId = "ltc-solo",
                        Miner = "LNextMiner",
                        Created = DateTime.UtcNow,
                    },
                }));

            Assert.Contains("does not end at a newline boundary",
                error.Message);
            Assert.Equal(incomplete, await File.ReadAllBytesAsync(recoveryFilename));
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_RollbackFailurePreservesBothErrors()
    {
        var original = Encoding.UTF8.GetBytes("# existing journal\n");
        var payload = Encoding.UTF8.GetBytes(
            "{\"poolId\":\"ltc-solo\",\"miner\":\"next\"}\n");
        await using var stream = new RollbackFailureStream(original, 7);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ShareRecorder.AppendRecoveryJournalAsync(stream, payload));

        Assert.Contains("partial write could not be rolled back", error.Message);
        var causes = Assert.IsType<AggregateException>(error.InnerException)
            .InnerExceptions;
        Assert.Contains(causes, ex => ex.Message.Contains(
            "simulated recovery-journal write failure"));
        Assert.Contains("simulated recovery-journal rollback failure",
            Assert.Single(causes, ex => ex.Message.Contains(
                "simulated recovery-journal rollback failure")).Message);
    }

    [Fact]
    public void RecoveryJournal_PrefixEndingAtInternalNewlineIsRejected()
    {
        var framedPrefix = Encoding.UTF8.GetBytes(
            "# miningcore-recovery-batch-v1 start count=2 sha256=" +
            new string('A', 64) + "\n" +
            "{\"poolId\":\"ltc-solo\",\"miner\":\"first\"}\n");
        using var stream = new MemoryStream(framedPrefix);

        var error = Assert.Throws<InvalidDataException>(() =>
            ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                "/recovery/recovered-shares.txt"));

        Assert.Contains("incomplete framed batch", error.Message);
        Assert.Equal(framedPrefix, stream.ToArray());
    }

    [Fact]
    public async Task RecoveryJournal_FirstAppendPrefixesAreNeverAcceptedAsLegacy()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(),
            Path.GetRandomFileName());
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LFirst" },
                new Share { PoolId = "btc-solo", Miner = "BSecond" },
            });
            var complete = await File.ReadAllTextAsync(recoveryFilename);
            Assert.StartsWith(ShareRecorder.RecoveryJournalMagic + "\n", complete);
            var trailerStart = complete.LastIndexOf(
                "# miningcore-recovery-batch-v2 end ", StringComparison.Ordinal);
            Assert.True(trailerStart > 0);

            foreach(var boundary in complete.Select((character, index) =>
                        (character, index))
                        .Where(x => x.character == '\n' && x.index < trailerStart)
                        .Select(x => x.index))
            {
                var prefix = Encoding.UTF8.GetBytes(complete[..(boundary + 1)]);
                using var stream = new MemoryStream(prefix);
                Assert.Throws<InvalidDataException>(() =>
                    ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                        recoveryFilename));
            }

            var frameStart = complete.IndexOf(
                "# miningcore-recovery-batch-v2 start ", StringComparison.Ordinal);
            var partialFrame = Encoding.UTF8.GetBytes(
                complete[..(frameStart + 12)]);
            using(var partialStream = new MemoryStream(partialFrame))
            {
                Assert.Throws<InvalidDataException>(() =>
                    ShareRecorder.EnsureRecoveryJournalAppendBoundary(partialStream,
                        recoveryFilename));
            }

            using var completeStream = new MemoryStream(
                Encoding.UTF8.GetBytes(complete));
            ShareRecorder.EnsureRecoveryJournalAppendBoundary(completeStream,
                recoveryFilename);
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_EarlierCorruptFrameIsRejectedByAppendStartupAndImport()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
        };
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LFirst" },
            });
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "btc-solo", Miner = "BSecond" },
            });

            var lines = (await File.ReadAllLinesAsync(recoveryFilename)).ToList();
            var firstStart = lines.FindIndex(line => line.StartsWith(
                "# miningcore-recovery-batch-v2 start ", StringComparison.Ordinal));
            var firstEnd = lines.FindIndex(firstStart + 1, line => line.StartsWith(
                "# miningcore-recovery-batch-v2 end ", StringComparison.Ordinal));
            Assert.True(firstStart >= 0 && firstEnd > firstStart + 1);
            lines.RemoveAt(firstStart + 1);
            await File.WriteAllLinesAsync(recoveryFilename, lines,
                new UTF8Encoding(false));

            var appendError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "doge-solo", Miner = "DThird" },
                }));
            Assert.Contains("changed outside Miningcore", appendError.Message);

            var processStatus = new ProcessStatus();
            var fatalState = new ShareRecoveryFatalState(config, processStatus,
                Path.Combine(directory, "state"));
            var startupError = Assert.Throws<PoolStartupException>(() =>
                fatalState.EnsureStartupAllowed());
            Assert.Contains("record count, content hash, or chain digest", startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);

            var importFixture = CreateRecoveryFixture();
            var importError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                importFixture.Recorder.RecoverSharesAsync(recoveryFilename));
            Assert.Contains("record count, content hash, or chain digest", importError.Message);
            await importFixture.ConnectionFactory.DidNotReceive()
                .OpenConnectionAsync();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryJournal_LegacyPrefixCanReceiveMultipleValidatedFrames()
    {
        var recoveryFilename = Path.GetTempFileName();
        await File.WriteAllTextAsync(recoveryFilename,
            "# legacy recovery journal\n" + RecoveryShareJson(1) + "\n",
            new UTF8Encoding(false));
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LSecond" },
            });
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "btc-solo", Miner = "BThird" },
            });

            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
            var lines = await File.ReadAllLinesAsync(recoveryFilename);
            Assert.Equal(3, lines.Count(line =>
                !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')));
            Assert.Equal(2, lines.Count(line => line.StartsWith(
                "# miningcore-recovery-batch-v2 end ", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("delete")]
    [InlineData("swap")]
    public async Task RecoveryJournal_ChainedFramesRejectStructuralReplayAtEveryBoundary(
        string mutation)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.txt");
        var mutated = Path.Combine(directory, $"{mutation}.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = original,
        };
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>());

        try
        {
            foreach(var miner in new[] { "first", "middle", "last" })
            {
                await recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = miner },
                });
            }

            var lines = (await File.ReadAllLinesAsync(original)).ToList();
            var ranges = new List<(int Start, int End)>();

            for(var index = 0; index < lines.Count; index++)
            {
                if(!lines[index].StartsWith(
                       "# miningcore-recovery-batch-v2 start ",
                       StringComparison.Ordinal))
                    continue;

                var end = lines.FindIndex(index + 1, line => line.StartsWith(
                    "# miningcore-recovery-batch-v2 end ",
                    StringComparison.Ordinal));
                ranges.Add((index, end));
                index = end;
            }

            Assert.Equal(3, ranges.Count);
            var middle = lines.GetRange(ranges[1].Start,
                ranges[1].End - ranges[1].Start + 1);

            switch(mutation)
            {
                case "duplicate":
                    lines.InsertRange(ranges[1].End + 1, middle);
                    break;

                case "delete":
                    lines.RemoveRange(ranges[1].Start, middle.Count);
                    break;

                case "swap":
                {
                    var last = lines.GetRange(ranges[2].Start,
                        ranges[2].End - ranges[2].Start + 1);
                    lines.RemoveRange(ranges[1].Start,
                        middle.Count + last.Count);
                    lines.InsertRange(ranges[1].Start, last.Concat(middle));
                    break;
                }
            }

            await File.WriteAllLinesAsync(mutated, lines,
                new UTF8Encoding(false));
            var appendRecorder = new ShareRecorder(
                Substitute.For<IConnectionFactory>(),
                AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
                Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
                new ClusterConfig
                {
                    Pools = Array.Empty<PoolConfig>(),
                    ShareRecoveryFile = mutated,
                }, Substitute.For<IMessageBus>());
            var appendError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                appendRecorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "doge-solo", Miner = "new" },
                }));
            Assert.Contains("missing, duplicate, or reordered", appendError.Message);

            var status = new ProcessStatus();
            var fatalState = new ShareRecoveryFatalState(new ClusterConfig
            {
                ShareRecoveryFile = mutated,
            }, status, Path.Combine(directory, "state"));
            var startupError = Assert.Throws<PoolStartupException>(() =>
                fatalState.EnsureStartupAllowed());
            Assert.Contains("missing, duplicate, or reordered", startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                status.ExitCode);

            var importFixture = CreateRecoveryFixture();
            var importError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                importFixture.Recorder.RecoverSharesAsync(mutated));
            Assert.Contains("missing, duplicate, or reordered", importError.Message);
            await importFixture.ConnectionFactory.DidNotReceive()
                .OpenConnectionAsync();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryJournal_ThousandProductionBatchesValidateExistingContentOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings
            {
                DefaultValueHandling = DefaultValueHandling.Ignore,
                NullValueHandling = NullValueHandling.Ignore,
            }, Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>())
        {
            RecoveryDirectorySync = _ => { },
            RecoveryJournalFlush = _ => Task.CompletedTask,
        };
        var batch = Enumerable.Range(0, 250)
            .Select(index => new Share
            {
                PoolId = "ltc-solo",
                Miner = $"miner-{index}",
            })
            .ToArray();

        try
        {
            for(var index = 0; index < 1_000; index++)
                await recorder.WriteRecoveryJournalAsync(batch);

            var finalLength = new FileInfo(recoveryFilename).Length;
            Assert.True(recorder.RecoveryValidationBytesRead < finalLength / 100,
                $"Validated {recorder.RecoveryValidationBytesRead} bytes for a {finalLength}-byte journal");
            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryJournal_RejectsRecordLinesAboveBoundedLimit()
    {
        var content = Encoding.UTF8.GetBytes(
            new string('x', ShareRecorder.MaxRecoveryRecordLineLength + 1) + "\n");
        using var stream = new MemoryStream(content);

        var error = Assert.Throws<InvalidDataException>(() =>
            ShareRecorder.EnsureRecoveryJournalAppendBoundary(stream,
                "/recovery/recovered-shares.txt"));

        Assert.Contains("record line longer", error.Message);
    }

    [Fact]
    public async Task RecoveryJournal_SameLengthFileReplacementInvalidatesTrustedTail()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(),
            Path.GetRandomFileName());
        var recorder = new ShareRecorder(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>());

        try
        {
            await recorder.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "first" },
            });
            var bytes = await File.ReadAllBytesAsync(recoveryFilename);
            File.Delete(recoveryFilename);
            await File.WriteAllBytesAsync(recoveryFilename, bytes);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "second" },
                }));
            Assert.Contains("changed outside Miningcore", error.Message);
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_StableIdentitySurvivesIntentionalRename()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-stable-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "recovered-shares.txt");
        var archive = source + ".imported";
        await File.WriteAllTextAsync(source, "identity-test");

        try
        {
            RecoveryJournalFileIdentity before;
            await using(var stream = File.OpenRead(source))
                before = RecoveryJournalFileIdentity.ReadStable(stream);

            File.Move(source, archive);

            await using var archived = File.OpenRead(archive);
            var after = RecoveryJournalFileIdentity.ReadStable(archived);
            Assert.Equal(before, after);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryState_GenuinelyAbsentEntriesRequireSuccessfulEnumeration()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-absent-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            stateDirectory);
        var import = new ShareRecoveryImportState(recoveryFilename,
            stateDirectory);

        try
        {
            terminal.EnsureJournalMayBeMissing();
            Assert.Null(import.TryRead());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryState_EnumerationFailureCannotProveMarkerAbsence()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-enumeration-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            stateDirectory)
        {
            EnumerateEntries = _ => throw new IOException(
                "injected terminal enumeration failure"),
        };
        var import = new ShareRecoveryImportState(recoveryFilename,
            stateDirectory)
        {
            EnumerateEntries = _ => throw new UnauthorizedAccessException(
                "injected import enumeration denial"),
        };

        try
        {
            Assert.Throws<IOException>(terminal.EnsureJournalMayBeMissing);
            Assert.Throws<UnauthorizedAccessException>(() => import.TryRead());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveryState_DirectoryAtMarkerPathIsRejected(bool terminalMarker)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-directory-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            stateDirectory);
        var import = new ShareRecoveryImportState(recoveryFilename,
            stateDirectory);
        var marker = terminalMarker ? terminal.Filename : import.Filename;

        try
        {
            Directory.CreateDirectory(marker);

            var error = terminalMarker
                ? Assert.Throws<InvalidDataException>(
                    terminal.EnsureJournalMayBeMissing)
                : Assert.Throws<InvalidDataException>(() => import.TryRead());

            Assert.Contains("directory, not a regular file", error.Message);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveryState_DirectoryAtMarkerPathBlocksStartupWithStatus74(
        bool terminalMarker)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-startup-directory-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "missing-recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var terminal = new ShareRecoveryTerminalState(config.ShareRecoveryFile,
            config.ShareRecoveryStateDirectory);
        var import = new ShareRecoveryImportState(config.ShareRecoveryFile,
            config.ShareRecoveryStateDirectory);
        var marker = terminalMarker ? terminal.Filename : import.Filename;
        var processStatus = new ProcessStatus();

        try
        {
            Directory.CreateDirectory(marker);
            var state = new ShareRecoveryFatalState(config, processStatus,
                config.ShareRecoveryStateDirectory);

            var error = Assert.Throws<PoolStartupException>(
                state.EnsureStartupAllowed);

            Assert.Contains("directory, not a regular file", error.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryState_DanglingTerminalSymlinkIsRejectedOnLinux()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-symlink-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(terminal.Filename)!);
            File.CreateSymbolicLink(terminal.Filename,
                Path.Combine(directory, "missing-anchor-target"));

            var error = Assert.Throws<InvalidDataException>(
                terminal.EnsureJournalMayBeMissing);
            Assert.Contains("symbolic link", error.Message);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryState_InaccessibleTerminalEntryIsRejectedOnWindows()
    {
        if(!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-inaccessible-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(terminal.Filename)!);
            File.WriteAllText(terminal.Filename, "unreadable while exclusively held");

            using var exclusive = new FileStream(terminal.Filename, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            Assert.Throws<IOException>(terminal.EnsureJournalMayBeMissing);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecoveryState_MalformedRegularMarkerIsRejected(bool terminalMarker)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-malformed-{Guid.NewGuid():N}");
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var terminal = new ShareRecoveryTerminalState(recoveryFilename,
            stateDirectory);
        var import = new ShareRecoveryImportState(recoveryFilename,
            stateDirectory);
        var marker = terminalMarker ? terminal.Filename : import.Filename;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "malformed");

            Assert.Throws<InvalidDataException>(() =>
            {
                if(terminalMarker)
                    terminal.EnsureConsistent(1, new string('A', 64), true);
                else
                    import.TryRead();
            });
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void MiningFailStopGate_RejectsPostSignalShareBeforeQueueOrAcknowledgement()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus,
            applicationLifetime);
        var messageBus = new MessageBus(coordinator);
        var queued = new List<Share>();
        using var subscription = messageBus.Listen<Share>().Subscribe(queued.Add);
        var acknowledged = false;

        messageBus.SendMessage(new Share { PoolId = "ltc-solo", Miner = "before" });
        Assert.True(coordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss));

        Assert.Throws<OperationCanceledException>(() =>
        {
            messageBus.SendMessage(new Share
            {
                PoolId = "ltc-solo",
                Miner = "after",
            });
            acknowledged = true;
        });

        Assert.False(acknowledged);
        Assert.Equal("before", Assert.Single(queued).Miner);
        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
    }

    [Fact]
    public void MiningAcceptance_FailStopImmediatelyBeforePublicationDoesNotAcknowledge()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var published = false;
        var acknowledged = false;
        using var subscription = bus.Listen<Share>()
            .Subscribe(_ => published = true);

        coordinator.BeginFailStop(ProcessExitCodes.UnreconciledShareDurabilityLoss);

        Assert.Throws<OperationCanceledException>(() =>
            acceptance.PublishShare(bus, new Share()));
        Assert.Throws<OperationCanceledException>(() =>
            acceptance.QueueResponse(() => acknowledged = true));
        Assert.False(published);
        Assert.False(acknowledged);
    }

    [Fact]
    public void MiningAcceptance_FailStopBetweenPublicationAndResponseDoesNotAcknowledge()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var published = false;
        var acknowledged = false;
        using var subscription = bus.Listen<Share>()
            .Subscribe(_ => published = true);

        acceptance.PublishShare(bus, new Share());
        coordinator.BeginFailStop(ProcessExitCodes.UnreconciledShareDurabilityLoss);

        Assert.Throws<OperationCanceledException>(() =>
            acceptance.QueueResponse(() => acknowledged = true));
        Assert.True(published);
        Assert.False(acknowledged);
    }

    [Fact]
    public async Task MiningAcceptance_ResponseQueueIsAtomicWithFailStopTransition()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        using var queueEntered = new ManualResetEventSlim();
        using var releaseQueue = new ManualResetEventSlim();
        var published = false;
        var responseQueued = false;
        using var subscription = bus.Listen<Share>()
            .Subscribe(_ => published = true);

        acceptance.PublishShare(bus, new Share());
        var queueTask = Task.Run(() =>
            acceptance.QueueResponse(() =>
            {
                queueEntered.Set();
                releaseQueue.Wait();
                responseQueued = true;
            }));
        Assert.True(queueEntered.Wait(TimeSpan.FromSeconds(2)));

        var failStopTask = Task.Run(() => coordinator.BeginFailStop(
            ProcessExitCodes.UnreconciledShareDurabilityLoss));
        await Task.Delay(25);
        Assert.False(failStopTask.IsCompleted);

        releaseQueue.Set();
        await queueTask;
        Assert.True(await failStopTask);
        Assert.True(published);
        Assert.True(responseQueued);
    }

    [Fact]
    public async Task MiningFailStopCapture_WaitsForActivePublicationRegistration()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        var unresolved = new List<string>();
        using var publicationEntered = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var subscription = bus.Listen<Share>().Subscribe(share =>
        {
            publicationEntered.Set();
            releasePublication.Wait();
            lock(unresolved)
                unresolved.Add(share.Miner);
        });
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var publication = Task.Run(() => acceptance.PublishShare(bus,
            new Share { Miner = "registered-before-capture" }));
        Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(2)));

        var failStop = Task.Run(() => coordinator.BeginFailStopAndCapture(
            ProcessExitCodes.UnreconciledShareDurabilityLoss, () =>
            {
                lock(unresolved)
                    return unresolved.ToArray();
            }));
        await Task.Delay(25);
        Assert.False(failStop.IsCompleted);

        releasePublication.Set();
        await publication;
        Assert.Equal(new[] { "registered-before-capture" }, await failStop);
    }

    [Fact]
    public async Task MiningFailStopCapture_ExcludesResolutionCompletedBeforeGateCloses()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        var unresolved = new List<string> { "known-committed" };
        using var publicationEntered = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var subscription = bus.Listen<Share>().Subscribe(_ =>
        {
            publicationEntered.Set();
            releasePublication.Wait();
        });
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var publication = Task.Run(() => acceptance.PublishShare(bus, new Share()));
        Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(2)));

        var failStop = Task.Run(() => coordinator.BeginFailStopAndCapture(
            ProcessExitCodes.UnreconciledShareDurabilityLoss, () =>
            {
                lock(unresolved)
                    return unresolved.ToArray();
            }));
        await Task.Delay(25);
        lock(unresolved)
            unresolved.Remove("known-committed");
        releasePublication.Set();

        await publication;
        Assert.Empty(await failStop);
    }

    [Fact]
    public async Task MiningAcceptance_HealthySubmissionsRunConcurrentlyAcrossPools()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        const int submissionCount = 32;
        using var allEntered = new CountdownEvent(submissionCount);
        using var release = new ManualResetEventSlim();
        var active = 0;
        var peak = 0;
        var buses = Enumerable.Range(0, submissionCount)
            .Select(_ => new MessageBus())
            .ToArray();
        var subscriptions = buses.Select(bus => bus.Listen<Share>().Subscribe(_ =>
            {
                var current = Interlocked.Increment(ref active);

                while(true)
                {
                    var previous = Volatile.Read(ref peak);
                    if(previous >= current ||
                       Interlocked.CompareExchange(ref peak, current,
                           previous) == previous)
                        break;
                }

                allEntered.Signal();
                release.Wait();
                Interlocked.Decrement(ref active);
            }))
            .ToArray();

        try
        {
            // Dedicated workers avoid thread-pool starvation from the deliberately blocked
            // publication callbacks. The property under test is the admission gate, not the
            // runner's hill-climbing latency under 32 synchronously blocked work items.
            var submissions = Enumerable.Range(0, submissionCount)
                .Select(index => Task.Factory.StartNew(() =>
                {
                    using var acceptance = coordinator.AcquireSubmissionAcceptance();
                    acceptance.PublishShare(buses[index], new Share());
                }, CancellationToken.None, TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            try
            {
                Assert.True(allEntered.Wait(TimeSpan.FromSeconds(5)),
                    $"Only {submissionCount - allEntered.CurrentCount} concurrent submissions entered");
                Assert.True(peak > 1,
                    "Healthy share submissions were globally serialized");

                var failStop = Task.Run(() => coordinator.BeginFailStop(
                    ProcessExitCodes.UnreconciledShareDurabilityLoss));
                await Task.Delay(25);
                Assert.False(failStop.IsCompleted);

                release.Set();
                await Task.WhenAll(submissions);
                Assert.True(await failStop);
            }
            finally
            {
                release.Set();
            }
        }
        finally
        {
            foreach(var subscription in subscriptions)
                subscription.Dispose();
        }
    }

    [Fact]
    public async Task RecoveryJournal_FlushFailureRollsBackSuccessfulWrite()
    {
        var original = Encoding.UTF8.GetBytes("# existing journal\n");
        var payload = Encoding.UTF8.GetBytes("{\"poolId\":\"ltc-solo\"}\n");
        await using var stream = new MemoryStream();
        await stream.WriteAsync(original);
        var flushCalls = 0;

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ShareRecorder.AppendRecoveryJournalAsync(stream, payload, _ =>
            {
                if(Interlocked.Increment(ref flushCalls) == 1)
                    throw new IOException("simulated durable flush failure");

                return Task.CompletedTask;
            }));

        Assert.Contains("simulated durable flush failure", error.Message);
        Assert.Equal(2, flushCalls);
        Assert.Equal(original, stream.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryJournal_FileFlushStageFailureRollsBackSuccessfulWrite(
        bool failAsyncFlush)
    {
        var filename = Path.GetTempFileName();
        var original = Encoding.UTF8.GetBytes("# existing journal\n");
        var payload = Encoding.UTF8.GetBytes("{\"poolId\":\"ltc-solo\"}\n");

        try
        {
            await using(var stream = new FlushFailureFileStream(filename))
            {
                await stream.WriteAsync(original);
                await stream.FlushAsync();
                stream.Flush(true);
                stream.FailAsyncFlushOnce = failAsyncFlush;
                stream.FailDurableFlushOnce = !failAsyncFlush;

                var error = await Assert.ThrowsAsync<IOException>(() =>
                    ShareRecorder.AppendRecoveryJournalAsync(stream, payload));

                Assert.Contains(failAsyncFlush
                    ? "simulated FlushAsync failure"
                    : "simulated Flush(true) failure", error.Message);
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(filename));
        }
        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public async Task RecoveryJournal_RollbackFlushFailurePreservesWriteAndFlushErrors()
    {
        var original = Encoding.UTF8.GetBytes("# existing journal\n");
        var payload = Encoding.UTF8.GetBytes("{\"poolId\":\"ltc-solo\"}\n");
        await using var stream = new PartialWriteFailureStream(original, 8);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ShareRecorder.AppendRecoveryJournalAsync(stream, payload, _ =>
                throw new IOException("simulated rollback flush failure")));
        var causes = Assert.IsType<AggregateException>(error.InnerException)
            .InnerExceptions;

        Assert.Contains(causes, ex => ex.Message.Contains(
            "simulated recovery-journal write failure"));
        Assert.Contains(causes, ex => ex.Message.Contains(
            "simulated rollback flush failure"));
    }

    [Fact]
    public async Task RecoveryFallback_DatabaseAndJournalFailureSignalsFailStop()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var missingDirectory = Path.Combine(directory, "missing-journal-directory");
        var recoveryFilename = Path.Combine(missingDirectory,
            "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var recoveryFailureHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var messageBus = Substitute.For<IMessageBus>();
        var databaseError = new BrokenCircuitException(
            "PostgreSQL persistence circuit is open");
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw databaseError);
        var share = new Share
        {
            PoolId = "ltc-solo",
            Miner = "LRetainedMiner",
            Difficulty = 1,
            Created = DateTime.UtcNow,
        };
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
            }, messageBus, recoveryFailureHandler,
            Substitute.For<IMiningFailStopCoordinator>());

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                recorder.PersistSharesAsync(new[] { share }));

            Assert.Contains("PostgreSQL or the recovery journal", error.Message);
            var causes = Assert.IsType<AggregateException>(error.InnerException)
                .InnerExceptions;
            Assert.Contains(databaseError, causes);
            await recoveryFailureHandler.Received(1).StopClusterAsync(
                Arg.Is<IReadOnlyCollection<Share>>(shares =>
                    shares.Count == 1 && shares.Single().Miner == share.Miner),
                recoveryFilename, databaseError,
                Arg.Is<DirectoryNotFoundException>(ex =>
                    ex.Message.Contains(missingDirectory)));
            messageBus.DidNotReceive().SendMessage(
                Arg.Is<AdminNotification>(notification =>
                    notification.Subject == "Share Recorder Policy Fallback"),
                Arg.Any<string>());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryFallback_FirstCreateDirectorySyncFailureEntersFatalFailStop()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        };
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var fatalState = new ShareRecoveryFatalState(config, processStatus,
            stateDirectory);
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        notificationSender.SendCriticalAdminNotificationAsync(
                Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var recoveryFailureHandler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() => notificationSender),
            fatalState);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw new BrokenCircuitException("PostgreSQL circuit is open"));
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, Substitute.For<IMessageBus>(), recoveryFailureHandler,
            Substitute.For<IMiningFailStopCoordinator>())
        {
            RecoveryDirectorySync = _ => throw new IOException(
                "simulated recovery-directory fsync failure"),
        };

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                recorder.PersistSharesAsync(new[]
                {
                    new Share
                    {
                        PoolId = "ltc-solo",
                        Miner = "LDirectorySync",
                        Created = DateTime.UtcNow,
                    },
                }));

            Assert.Contains("PostgreSQL or the recovery journal", error.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
            applicationLifetime.Received(1).StopApplication();
            Assert.True(File.Exists(fatalState.FatalStateFilename));
            Assert.True(File.Exists(recoveryFilename));
            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryFallback_DurableWriteEmitsNormalFallbackNotification()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(),
            Path.GetRandomFileName());
        var connectionFactory = Substitute.For<IConnectionFactory>();
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw new BrokenCircuitException(
                "PostgreSQL persistence circuit is open"));
        var recoveryFailureHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var messageBus = Substitute.For<IMessageBus>();
        var share = new Share
        {
            PoolId = "ltc-solo",
            Miner = "LDurableFallbackMiner",
            Difficulty = 1,
            Created = DateTime.UtcNow,
        };
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = Path.GetDirectoryName(recoveryFilename),
                Notifications = new NotificationsConfig
                {
                    Admin = new AdminNotifications
                    {
                        Enabled = true,
                        NotifyPaymentSuccess = true,
                    },
                },
            }, messageBus, recoveryFailureHandler,
            Substitute.For<IMiningFailStopCoordinator>());

        try
        {
            await recorder.PersistSharesAsync(new[] { share });

            var persisted = (await File.ReadAllLinesAsync(recoveryFilename))
                .Where(line => !string.IsNullOrWhiteSpace(line) &&
                    !line.StartsWith('#'))
                .Select(JsonConvert.DeserializeObject<Share>)
                .Single();
            Assert.Equal(share.Miner, persisted.Miner);
            await recoveryFailureHandler.DidNotReceive().StopClusterAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<string>(),
                Arg.Any<Exception>(), Arg.Any<Exception>());
            messageBus.Received(1).SendMessage(
                Arg.Is<AdminNotification>(notification =>
                    notification.Subject == "Share Recorder Policy Fallback" &&
                    notification.Message.Contains(recoveryFilename)),
                Arg.Any<string>());
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task PersistSharesCoreAsync_DuplicateBlockDoesNotNotifyAgain()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(false);

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new[] { pool } }, messageBus);
        var candidate = new Share
        {
            PoolId = pool.Id,
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        messageBus.DidNotReceive().SendMessage(Arg.Any<BlockFoundNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PersistSharesCoreAsync_EmitsBlockFoundOnlyAfterCommit()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new[] { pool } }, messageBus);
        var candidate = new Share
        {
            PoolId = pool.Id,
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        Received.InOrder(() =>
        {
            transaction.Commit();
            messageBus.SendMessage(Arg.Any<BlockFoundNotification>(), Arg.Any<string>());
        });
    }

    [Fact]
    public async Task PersistSharesCoreAsync_FailedCommitDoesNotEmitBlockFound()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        transaction.When(x => x.Commit()).Do(_ => throw new DataException("commit failed"));
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new[] { pool } }, messageBus);
        var candidate = new Share
        {
            PoolId = pool.Id,
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        var error = await Assert.ThrowsAsync<TransactionCommitOutcomeUncertainException>(() =>
            recorder.PersistSharesCoreAsync(new List<Share> { candidate }));

        Assert.Equal("commit failed", error.InnerException?.Message);

        messageBus.DidNotReceive().SendMessage(Arg.Any<BlockFoundNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PersistSharesCoreAsync_PostCommitNotificationFailureDoesNotFailPersistence()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);
        messageBus.When(x => x.SendMessage(Arg.Any<BlockFoundNotification>(), Arg.Any<string>()))
            .Do(_ => throw new IOException("subscriber failed"));

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new[] { pool } }, messageBus);
        var candidate = new Share
        {
            PoolId = pool.Id,
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = "auxpow-block:doge-block-hash",
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        transaction.Received(1).Commit();
        transaction.DidNotReceive().Rollback();
    }

    [Theory]
    [InlineData("auxpow-claim")]
    [InlineData("merged-parent-uncertain")]
    public async Task PersistSharesCoreAsync_UncertainBlockDoesNotEmitBlockFoundNotification(
        string blockType)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        blockRepository.InsertAsync(connection, transaction, Arg.Any<Block>()).Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper, new JsonSerializerSettings(),
            shareRepository, blockRepository, new ClusterConfig { Pools = new[] { pool } }, messageBus);
        var candidate = new Share
        {
            PoolId = pool.Id,
            Miner = "DExampleAddress",
            BlockHeight = 123,
            BlockHash = "doge-block-hash",
            BlockType = blockType,
            IsBlockCandidate = true,
            BlockOnly = true,
            TransactionConfirmationData = $"{blockType}:doge-block-hash",
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        await blockRepository.Received(1).InsertAsync(connection, transaction,
            Arg.Any<Block>());
        messageBus.DidNotReceive().SendMessage(Arg.Any<BlockFoundNotification>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_UnexpectedDatabaseFailureJournalsAndStopsCluster()
    {
        var recoveryFilename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var failureHandler = Substitute.For<ICandidatePersistenceFailureHandler>();
        var mapper = AutoMapperFactory.CreateMapper();
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw new InvalidOperationException("unexpected persistence pipeline failure"));
        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, Substitute.For<IMessageBus>(), failureHandler);
        var candidate = CreateDurableCandidate("unexpected-db-block", "auxpow");

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.PersistBlockCandidateAsync(candidate));

            Assert.Contains("unexpected persistence", ex.Message);
            var failureCall = Assert.Single(failureHandler.ReceivedCalls());
            var failureArguments = failureCall.GetArguments();
            var failedCandidates = Assert.IsAssignableFrom<IReadOnlyCollection<Share>>(
                failureArguments[0]);
            Assert.Equal(candidate.BlockHash, Assert.Single(failedCandidates).BlockHash);
            Assert.Contains("unexpected persistence",
                Assert.IsType<InvalidOperationException>(failureArguments[1]).Message);
            Assert.Null(failureArguments[2]);
            Assert.True(Assert.IsType<bool>(failureArguments[3]));
            var persisted = (await File.ReadAllLinesAsync(recoveryFilename))
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
                .Select(JsonConvert.DeserializeObject<Share>)
                .Single();
            Assert.Equal(candidate.BlockHash, persisted.BlockHash);
            Assert.Equal(candidate.Miner, persisted.Miner);
            Assert.Equal(candidate.BlockType, persisted.BlockType);
            Assert.Equal(candidate.TransactionConfirmationData,
                persisted.TransactionConfirmationData);
        }
        finally
        {
            File.Delete(recoveryFilename);
        }
    }

    [Fact]
    public async Task PersistBlockCandidateAsync_DatabaseAndJournalFailureStopsCluster()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var missingDirectory = Path.Combine(directory, "missing-journal-directory");
        var stateDirectory = Path.Combine(directory, "state");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var failureHandler = Substitute.For<ICandidatePersistenceFailureHandler>();
        var mapper = AutoMapperFactory.CreateMapper();
        connectionFactory.OpenConnectionAsync().Returns<Task<IDbConnection>>(_ =>
            throw new InvalidOperationException("unexpected database failure"));
        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = Path.Combine(missingDirectory, "recovery.txt"),
                ShareRecoveryStateDirectory = stateDirectory,
            }, Substitute.For<IMessageBus>(), failureHandler);
        var candidate = CreateDurableCandidate("lost-candidate-block");

        try
        {
            var persistenceError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.PersistBlockCandidateAsync(candidate));

            Assert.IsType<DirectoryNotFoundException>(
                persistenceError.Data["RecoveryJournalException"]);

            var failureCall = Assert.Single(failureHandler.ReceivedCalls());
            var failureArguments = failureCall.GetArguments();
            Assert.Equal(candidate.BlockHash,
                Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<Share>>(
                    failureArguments[0])).BlockHash);
            Assert.Contains("database failure",
                Assert.IsType<InvalidOperationException>(failureArguments[1]).Message);
            Assert.Contains(missingDirectory,
                Assert.IsType<DirectoryNotFoundException>(failureArguments[2]).Message);
            Assert.False(Assert.IsType<bool>(failureArguments[3]));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CandidatePersistenceFailureHandler_LaterDualTargetFailureUpgradesStatusAndLatches()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/state/share-recovery.fatal");
        var handler = new CandidatePersistenceFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() => notificationSender),
            fatalState);

        await handler.StopClusterAsync(new[] { CreateDurableCandidate("ltc-parent") },
            new InvalidOperationException("parent failed"), null, true);
        await handler.StopClusterAsync(new[] { CreateDurableCandidate("doge-aux", "auxpow") },
            new InvalidOperationException("aux failed"),
            new IOException("journal failed"), false);

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
        fatalState.Received(1).MarkFatalShares(
            Arg.Is<IReadOnlyCollection<Share>>(shares =>
                shares.Count == 1 && shares.Single().PoolId == "doge-solo"),
            Arg.Is<InvalidOperationException>(ex => ex.Message == "aux failed"),
            Arg.Is<IOException>(ex => ex.Message == "journal failed"));
        await notificationSender.Received(2).SendCriticalAdminNotificationAsync(
            Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>());
        await notificationSender.Received(1).SendCriticalAdminNotificationAsync(
            Arg.Is<AdminNotification>(notification =>
                notification.Subject == "Escalated block-candidate durability loss" &&
                notification.Message.Contains("doge-aux") &&
                notification.Message.Contains(fatalState.FatalStateFilename)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CandidatePersistenceFailureHandler_DualTargetFailureUsesNonRestartStatus()
    {
        var processStatus = new ProcessStatus();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        var handler = new CandidatePersistenceFailureHandler(
            new MiningFailStopCoordinator(processStatus,
                Substitute.For<IHostApplicationLifetime>()),
            new Lazy<ICriticalNotificationSender>(() =>
                Substitute.For<ICriticalNotificationSender>()), fatalState);

        await handler.StopClusterAsync(new[] { CreateDurableCandidate("doge-aux", "auxpow") },
            new IOException("postgres failed"), new IOException("journal failed"),
            false);

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        fatalState.Received(1).MarkFatalShares(
            Arg.Is<IReadOnlyCollection<Share>>(shares =>
                shares.Count == 1 && shares.Single().PoolId == "doge-solo"),
            Arg.Any<IOException>(), Arg.Any<IOException>());
    }

    [Fact]
    public void ShareRecorder_PublicConstructionRequiresRecoveryFailureHandler()
    {
        var constructor = Assert.Single(typeof(ShareRecorder).GetConstructors());
        var recoveryParameter = Assert.Single(constructor.GetParameters(), x =>
            x.ParameterType == typeof(IShareRecoveryFailureHandler));
        var coordinatorParameter = Assert.Single(constructor.GetParameters(), x =>
            x.ParameterType == typeof(IMiningFailStopCoordinator));

        Assert.False(recoveryParameter.IsOptional);
        Assert.False(recoveryParameter.HasDefaultValue);
        Assert.False(coordinatorParameter.IsOptional);
        Assert.False(coordinatorParameter.HasDefaultValue);
    }

    [Fact]
    public void CandidateFailureHandler_PublicConstructionRequiresFatalState()
    {
        var constructor = Assert.Single(
            typeof(CandidatePersistenceFailureHandler).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters(), x =>
            x.ParameterType == typeof(IShareRecoveryFatalState));

        Assert.False(parameter.IsOptional);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_StopsAndNotifiesOnlyOnce()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/recovery/recovered-shares.txt.fatal");
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() =>
                notificationSender), fatalState);
        var shares = new[]
        {
            new Share { PoolId = "ltc-solo", Miner = "LFirst" },
            new Share { PoolId = "btc-solo", Miner = "BSecond" },
        };

        await handler.StopClusterAsync(shares, "/recovery/recovered-shares.txt",
            new IOException("database full"), new IOException("journal full"));
        await handler.StopClusterAsync(shares, "/recovery/recovered-shares.txt",
            new IOException("database still full"),
            new IOException("journal still full"));

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
        fatalState.Received(2).MarkFatalShares(
            Arg.Is<IReadOnlyCollection<Share>>(records =>
                records.Select(x => x.PoolId).OrderBy(x => x)
                    .SequenceEqual(new[] { "btc-solo", "ltc-solo" })),
            Arg.Any<Exception>(), Arg.Any<Exception>());
        await notificationSender.Received(1).SendCriticalAdminNotificationAsync(
            Arg.Is<AdminNotification>(notification =>
                notification.Subject == "Fatal share-recovery fallback failure" &&
                notification.Message.Contains("2 share(s)") &&
                notification.Message.Contains("btc-solo, ltc-solo") &&
                notification.Message.Contains("exit status 74")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_UncertainCommitLatchesExactSharesWithStatus74()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/state/uncertain.fatal");
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() => notificationSender),
            fatalState);
        var shares = new[]
        {
            new Share { PoolId = "ltc-solo", Miner = "LUncertain" },
        };
        var commitError = new IOException("commit acknowledgement lost");

        await handler.StopClusterForUncertainCommitAsync(shares,
            "/recovery/recovered-shares.txt", commitError);

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
        fatalState.Received(1).MarkFatalShares(shares, commitError,
            Arg.Is<InvalidOperationException>(ex =>
                ex.Message.Contains("intentionally suppressed")),
            "postgresql-commit-outcome-uncertain");
        await notificationSender.Received(1).SendCriticalAdminNotificationAsync(
            Arg.Is<AdminNotification>(notification =>
                notification.Subject == "Uncertain PostgreSQL share commit" &&
                notification.Message.Contains("not extended") &&
                notification.Message.Contains("reconcile",
                    StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_JournalledPipelineFailureUsesGeneralExit()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() => notificationSender),
            fatalState);
        var shares = new[]
        {
            new Share { PoolId = "ltc-solo", Miner = "LJournalled" },
        };

        await handler.StopClusterAfterJournalAsync(shares,
            "/recovery/recovered-shares.txt",
            new InvalidOperationException("mapper failure"));

        Assert.Equal(ProcessExitCodes.GeneralFailure, processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
        fatalState.DidNotReceiveWithAnyArgs().MarkFatalShares(default, default,
            default);
        await notificationSender.Received(1).SendCriticalAdminNotificationAsync(
            Arg.Is<AdminNotification>(notification =>
                notification.Subject == "Share persistence pipeline stopped"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_NotificationFailureStillStops()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        notificationSender.SendCriticalAdminNotificationAsync(
                Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException(
                "notification transport unavailable"));
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns(
            "/recovery/recovered-shares.txt.fatal");
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() =>
                notificationSender), fatalState);

        await handler.StopClusterAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LFirst" },
            }, "/recovery/recovered-shares.txt",
            new IOException("database full"), new IOException("journal full"));

        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
        applicationLifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_QuiescesBeforeCriticalDelivery()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/recovery/recovered-shares.txt.fatal");
        var delivered = false;
        var stoppedBeforeDelivery = false;
        notificationSender.SendCriticalAdminNotificationAsync(
                Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var ct = call.Arg<CancellationToken>();
                Assert.False(ct.IsCancellationRequested);
                await Task.Delay(25, ct);
                delivered = true;
            });
        applicationLifetime.When(x => x.StopApplication())
            .Do(_ => stoppedBeforeDelivery = !delivered);
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() =>
                notificationSender), fatalState);

        await handler.StopClusterAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LFirst" },
            }, "/recovery/recovered-shares.txt",
            new IOException("database full"), new IOException("journal full"));

        Assert.True(delivered);
        Assert.True(stoppedBeforeDelivery);
        applicationLifetime.Received(1).StopApplication();
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_BoundsCriticalDeliveryBeforeShutdown()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        notificationSender.SendCriticalAdminNotificationAsync(
                Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.InfiniteTimeSpan,
                call.Arg<CancellationToken>()));
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
        fatalState.FatalStateFilename.Returns("/recovery/recovered-shares.txt.fatal");
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus, applicationLifetime),
            new Lazy<ICriticalNotificationSender>(() =>
                notificationSender), fatalState)
        {
            CriticalNotificationTimeout = TimeSpan.FromMilliseconds(25),
        };

        await handler.StopClusterAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LFirst" },
            }, "/recovery/recovered-shares.txt",
            new IOException("database full"), new IOException("journal full"))
            .WaitAsync(TimeSpan.FromSeconds(1));

        applicationLifetime.Received(1).StopApplication();
        Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
            processStatus.ExitCode);
    }

    [Fact]
    public async Task ShareRecoveryFailureHandler_LogsDatabaseWriteAndRollbackCauses()
    {
        var previousConfiguration = LogManager.Configuration;
        var target = new MemoryTarget
        {
            Layout = "${message}|${exception:format=tostring}",
        };
        var configuration = new LoggingConfiguration();
        configuration.AddRuleForAllLevels(target);
        LogManager.Configuration = configuration;
        LogManager.ReconfigExistingLoggers();

        try
        {
            var fatalState = Substitute.For<IShareRecoveryFatalState>();
            fatalState.FatalStateFilename.Returns(
                "/recovery/recovered-shares.txt.fatal");
            var processStatus = new ProcessStatus();
            var handler = new ShareRecoveryFailureHandler(
                new MiningFailStopCoordinator(processStatus,
                    Substitute.For<IHostApplicationLifetime>()),
                new Lazy<ICriticalNotificationSender>(() =>
                    Substitute.For<ICriticalNotificationSender>()), fatalState);
            var journalError = new IOException("journal rollback failed",
                new AggregateException(
                    new IOException("journal write failed"),
                    new IOException("journal rollback flush failed")));

            await handler.StopClusterAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LFirst" },
                }, "/recovery/recovered-shares.txt",
                new IOException("postgres insert failed"), journalError);
            LogManager.Flush();
            var output = string.Join("\n", target.Logs);

            Assert.Contains("postgres insert failed", output);
            Assert.Contains("journal write failed", output);
            Assert.Contains("journal rollback flush failed", output);
        }
        finally
        {
            LogManager.Configuration = previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }
    }

    [Fact]
    public async Task ShareRecoveryFatalState_ResolvesRelativePathAndBlocksStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-state-{Guid.NewGuid():N}");
        var absoluteRecoveryFile = Path.Combine(directory, "recovered-shares.txt");
        var relativeRecoveryFile = Path.GetRelativePath(Environment.CurrentDirectory,
            absoluteRecoveryFile);
        var processStatus = new ProcessStatus();
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = relativeRecoveryFile,
        }, processStatus, Path.Combine(directory, "state"));
        var sender = Substitute.For<ICriticalNotificationSender>();
        AdminNotification delivered = null;
        sender.SendCriticalAdminNotificationAsync(Arg.Any<AdminNotification>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                delivered = call.Arg<AdminNotification>();
                return Task.CompletedTask;
            });
        var handler = new ShareRecoveryFailureHandler(
            new MiningFailStopCoordinator(processStatus,
                Substitute.For<IHostApplicationLifetime>()),
            new Lazy<ICriticalNotificationSender>(() => sender), state);

        try
        {
            Assert.Equal(Path.GetFullPath(absoluteRecoveryFile),
                state.RecoveryFilename);
            state.EnsureStartupAllowed();

            await handler.StopClusterAsync(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LFirst" },
                }, relativeRecoveryFile, new IOException("database full"),
                new IOException("journal full"));

            Assert.True(File.Exists(state.FatalStateFilename));
            Assert.True(state.FatalStateFilename.StartsWith(
                Path.Combine(directory, "state"),
                StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(state.RecoveryFilename + ".fatal",
                state.FatalStateFilename);
            var marker = await File.ReadAllTextAsync(state.FatalStateFilename);
            var recoveryPathHash = ShareRecoveryFatalState.ComputeRecoveryPathHash(
                state.RecoveryFilename);
            Assert.Contains($"recoveryFile={state.RecoveryFilename}", marker);
            Assert.Contains($"recoveryPathSha256={recoveryPathHash}", marker);
            var startupError = Assert.Throws<PoolStartupException>(() =>
                state.EnsureStartupAllowed());
            Assert.Contains(state.FatalStateFilename, startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
            Assert.Contains(state.RecoveryFilename, delivered.Message);
            Assert.Contains(state.FatalStateFilename, delivered.Message);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShareRecoveryFatalState_PreservesAppendOnlyIncidentIdentifiers()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-incidents-{Guid.NewGuid():N}");
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
        }, new ProcessStatus(), Path.Combine(directory, "state"));

        try
        {
            state.MarkFatal(1, new[] { "ltc-solo" },
                new IOException("database one"), new IOException("journal one"));
            state.MarkFatal(2, new[] { "btc-solo" },
                new IOException("database two"), new IOException("journal two"));

            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var stem = Path.GetFileNameWithoutExtension(state.FatalStateFilename);
            var incidents = Directory.GetFiles(fatalDirectory,
                $"{stem}.*.incident");
            var incidentContents = await Task.WhenAll(incidents.Select(
                filename => File.ReadAllTextAsync(filename)));
            var incidentIds = incidentContents
                .SelectMany(marker => marker.Split('\n'))
                .Where(line => line.StartsWith("incidentId=",
                    StringComparison.Ordinal))
                .Select(line => line["incidentId=".Length..])
                .ToArray();
            Assert.Equal(2, incidentIds.Length);
            Assert.Equal(2, incidentIds.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(incidentContents, marker => marker.Contains(
                "database one", StringComparison.Ordinal));
            Assert.Contains(incidentContents, marker => marker.Contains(
                "database two", StringComparison.Ordinal));
            Assert.True(new FileInfo(state.FatalStateFilename).Length < 16 * 1024);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoverySafetyState_FirstUseDurablyPublishesEveryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-directories-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var stateDirectory = Path.Combine(directory, "state");
        var synced = new List<string>();
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
        }, new ProcessStatus(), stateDirectory)
        {
            DirectorySync = path => synced.Add(Path.GetFullPath(path)),
        };

        try
        {
            state.EnsureStartupAllowed();

            Assert.True(Directory.Exists(Path.Combine(stateDirectory,
                "share-recovery-fatal")));
            Assert.True(Directory.Exists(Path.Combine(stateDirectory,
                "share-recovery-terminal")));
            Assert.True(Directory.Exists(Path.Combine(stateDirectory,
                "share-recovery-import")));
            Assert.Contains(Path.GetFullPath(directory), synced);
            Assert.Equal(3, synced.Count(path => string.Equals(path,
                Path.GetFullPath(stateDirectory),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShareRecoveryFatalState_StoresExactSharesForUncertainCommit()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-exact-shares-{Guid.NewGuid():N}");
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
        }, new ProcessStatus(), Path.Combine(directory, "state"));
        var shares = new[]
        {
            new Share
            {
                PoolId = "ltc-solo",
                Miner = "LExactReconciliation",
                Difficulty = 42.5,
                Created = DateTime.UtcNow,
            },
        };

        try
        {
            state.MarkFatalShares(shares, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"),
                "postgresql-commit-outcome-uncertain");

            var lines = await File.ReadAllLinesAsync(state.FatalStateFilename);
            var detailFilename = Assert.Single(lines.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            var expectedHash = Assert.Single(lines.Where(line =>
                line.StartsWith("detailSha256=", StringComparison.Ordinal)))[
                "detailSha256=".Length..];
            var detailLines = await File.ReadAllLinesAsync(detailFilename);
            var encoded = Assert.Single(detailLines.Where(line =>
                line.StartsWith("shareJsonBase64=", StringComparison.Ordinal)));
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(
                encoded["shareJsonBase64=".Length..]));
            var restored = JsonConvert.DeserializeObject<Share>(json);

            Assert.Equal("ltc-solo", restored.PoolId);
            Assert.Equal("LExactReconciliation", restored.Miner);
            Assert.Equal(42.5, restored.Difficulty);
            Assert.Contains("shareCount=1", lines);
            Assert.Contains("detailState=complete", lines);
            Assert.Contains("failureCategory=postgresql-commit-outcome-uncertain",
                lines);
            await using var detail = File.OpenRead(detailFilename);
            Assert.Equal(expectedHash,
                Convert.ToHexString(await SHA256.HashDataAsync(detail)));
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShareRecoveryFatalState_MidSidecarFailureLeavesSmallDurableStartupLatch()
    {
        const int nearPrimaryQueueCapacity = 65_536;
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-large-incident-{Guid.NewGuid():N}");
        var processStatus = new ProcessStatus();
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
        }, processStatus, Path.Combine(directory, "state"));
        var shares = Enumerable.Range(0, nearPrimaryQueueCapacity)
            .Select(index => new Share
            {
                PoolId = "ltc-solo",
                Miner = $"LNearCapacity{index:D8}",
                Worker = "recovery-pressure-test",
                Difficulty = 16.25,
                NetworkDifficulty = 12_345_678,
                Created = DateTime.UtcNow.AddTicks(index),
                IpAddress = "192.0.2.10",
                UserAgent = "Miningcore recovery sidecar fault injection",
            })
            .ToArray();
        state.ExactShareWriteCheckpoint = index =>
        {
            if(index == nearPrimaryQueueCapacity / 2)
                throw new IOException("injected sidecar write failure");
        };

        try
        {
            var error = Assert.Throws<IOException>(() =>
                state.MarkFatalShares(shares, new IOException("commit uncertain"),
                    new InvalidOperationException("journal suppressed"),
                    "postgresql-commit-outcome-uncertain"));
            Assert.Contains("sidecar write failure", error.Message);

            Assert.True(File.Exists(state.FatalStateFilename));
            var latchInfo = new FileInfo(state.FatalStateFilename);
            Assert.True(latchInfo.Length < 16 * 1024,
                $"Fatal latch unexpectedly grew to {latchInfo.Length} bytes");
            var latch = await File.ReadAllLinesAsync(state.FatalStateFilename);
            Assert.Contains($"shareCount={nearPrimaryQueueCapacity}", latch);
            Assert.Contains("pools=(pending)", latch);
            Assert.Contains("detailState=hash-pending", latch);
            var detailFilename = Assert.Single(latch.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            var expectedHash = Assert.Single(latch.Where(line =>
                line.StartsWith("detailSha256=", StringComparison.Ordinal)))[
                "detailSha256=".Length..];
            Assert.Equal("(none)", expectedHash);
            Assert.False(File.Exists(detailFilename));
            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var stem = Path.GetFileNameWithoutExtension(state.FatalStateFilename);
            var incidentFile = Assert.Single(Directory.GetFiles(fatalDirectory,
                $"{stem}.*.incident"));
            Assert.Contains("detailState=hash-pending",
                await File.ReadAllTextAsync(incidentFile));

            var startupError = Assert.Throws<PoolStartupException>(() =>
                state.EnsureStartupAllowed());
            Assert.Contains(state.FatalStateFilename, startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryFatalState_PublishesLatchBeforeFirstShareSerialization()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-pre-serialization-{Guid.NewGuid():N}");
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
        }, new ProcessStatus(), Path.Combine(directory, "state"));
        state.ExactShareWriteCheckpoint = _ =>
            throw new JsonSerializationException("injected serialization failure");

        try
        {
            Assert.Throws<JsonSerializationException>(() =>
                state.MarkFatalShares(new[]
                    {
                        new Share
                        {
                            PoolId = "ltc-solo",
                            Miner = "LNeverSerialized",
                        },
                    }, new IOException("commit uncertain"),
                    new InvalidOperationException("journal suppressed"),
                    "postgresql-commit-outcome-uncertain"));

            var latch = File.ReadAllLines(state.FatalStateFilename);
            Assert.Contains("shareCount=1", latch);
            Assert.Contains("pools=(pending)", latch);
            Assert.Contains("detailSha256=(none)", latch);
            Assert.Contains("detailState=hash-pending", latch);
            Assert.Throws<PoolStartupException>(() => state.EnsureStartupAllowed());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_VerifiesCompleteSidecarReadOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share
                    {
                        PoolId = "ltc-solo",
                        Miner = "LVerified",
                        Difficulty = 12.5,
                        Created = DateTime.UtcNow,
                    },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"),
                "postgresql-commit-outcome-uncertain");
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.True(result.IsSuccessful);
            Assert.Equal(1, result.IncidentCount);
            Assert.Equal(1, result.CompleteCount);
            Assert.Equal(0, result.IncompleteCount);
            Assert.Equal(0, result.InvalidCount);
            Assert.Contains("status=COMPLETE", output.ToString());
            Assert.Contains("decodedRecords=1", output.ToString());
            Assert.Contains("does not prove PostgreSQL reconciliation",
                output.ToString());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_RejectsTamperedSidecar()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-tamper-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "bc1verified" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"),
                "postgresql-commit-outcome-uncertain");
            var latch = File.ReadAllLines(state.FatalStateFilename);
            var detailFilename = Assert.Single(latch.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            File.AppendAllText(detailFilename, "tampered\n");
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.False(result.IsSuccessful);
            Assert.Equal(1, result.InvalidCount);
            Assert.Contains("status=INVALID", output.ToString());
            Assert.True(output.ToString().Contains("SHA-256 does not match") ||
                        output.ToString().Contains("invalid prefix"),
                "Tampered evidence must fail either structural validation or its digest check.");
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_ReportsHashPendingEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-pending-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory)
        {
            ExactShareWriteCheckpoint = _ =>
                throw new IOException("injected sidecar failure"),
        };

        try
        {
            Assert.Throws<IOException>(() => state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LPending" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed")));
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.False(result.IsSuccessful);
            Assert.Equal(1, result.IncompleteCount);
            Assert.Equal(0, result.InvalidCount);
            Assert.Contains("status=INCOMPLETE", output.ToString());
            Assert.Contains("do not clear the fatal latch", output.ToString());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_RejectsOversizedLineWithBoundedReader()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-oversized-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LOversized" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"));
            var latch = File.ReadAllLines(state.FatalStateFilename);
            var detailFilename = Assert.Single(latch.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            File.WriteAllText(detailFilename, new string('A', 1_048_577));
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.False(result.IsSuccessful);
            Assert.Contains("record line longer than 1048576 characters",
                output.ToString());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_RejectsOversizedMetadataLine()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-metadata-line-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LMetadataBound" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"));
            var incident = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(state.FatalStateFilename)!, "*.incident"));
            File.WriteAllText(incident, "oversized=" + new string('A', 20_000));
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.False(result.IsSuccessful);
            Assert.Contains("record line longer than 16384 characters",
                output.ToString());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_UsesStableMetadataHandle()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-metadata-identity-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);
        var replacementBlocked = false;
        var replacementPublished = false;

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "bc1metadata" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"));
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output,
                null, filename =>
                {
                    if(!filename.EndsWith(".incident", StringComparison.Ordinal) ||
                       replacementBlocked || replacementPublished)
                        return;

                    var replacement = filename + ".replacement";
                    File.Copy(filename, replacement);

                    try
                    {
                        File.Move(replacement, filename, true);
                        replacementPublished = true;
                    }
                    catch(Exception ex) when(ex is IOException or
                                              UnauthorizedAccessException)
                    {
                        replacementBlocked = true;
                        File.Delete(replacement);
                    }
                });

            if(replacementPublished)
            {
                Assert.False(result.IsSuccessful);
                Assert.True(output.ToString().Contains("changed while it was being read") ||
                            output.ToString().Contains("was replaced while it was being read"));
            }
            else
            {
                Assert.True(replacementBlocked);
                Assert.True(result.IsSuccessful);
            }
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_UsesStableHandleAcrossHashAndParse()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-verifier-identity-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);
        var replacementBlocked = false;
        var replacementPublished = false;

        try
        {
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "bc1identity" },
                }, new IOException("commit uncertain"),
                new InvalidOperationException("journal suppressed"));
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output,
                filename =>
                {
                    var replacement = filename + ".replacement";
                    var bytes = File.ReadAllBytes(filename);
                    bytes[Math.Min(32, bytes.Length - 2)] ^= 1;
                    File.WriteAllBytes(replacement, bytes);

                    try
                    {
                        File.Move(replacement, filename, true);
                        replacementPublished = true;
                    }
                    catch(Exception ex) when(ex is IOException or
                                              UnauthorizedAccessException)
                    {
                        replacementBlocked = true;
                        File.Delete(replacement);
                    }
                });

            if(replacementPublished)
            {
                Assert.False(result.IsSuccessful);
                Assert.True(
                    output.ToString().Contains("sidecar path was replaced") ||
                    output.ToString().Contains("sidecar changed"),
                    "Path replacement must fail either the open-handle identity check " +
                    "or the final path identity check.");
            }
            else
            {
                Assert.True(replacementBlocked);
                Assert.True(result.IsSuccessful);
            }
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryIncidentVerifier_ConvertsOutOfMemoryToFailure()
    {
        var result = ShareRecoveryIncidentVerifier.Verify(new ClusterConfig(),
            new OutOfMemoryTextWriter());

        Assert.False(result.IsSuccessful);
        Assert.Equal(1, result.InvalidCount);
    }

    [Fact]
    public async Task ShareRecoveryFatalState_BlocksStartupOnIncompleteFramedBatch()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-boundary-{Guid.NewGuid():N}");
        var recoveryFile = Path.Combine(directory, "recovered-shares.txt");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(recoveryFile,
            "# miningcore-recovery-batch-v1 start count=2 sha256=" +
            new string('A', 64) + "\n" +
            "{\"poolId\":\"ltc-solo\",\"miner\":\"first\"}\n");
        var processStatus = new ProcessStatus();
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = recoveryFile,
        }, processStatus, Path.Combine(directory, "state"));

        try
        {
            var error = Assert.Throws<PoolStartupException>(() =>
                state.EnsureStartupAllowed());

            Assert.Contains("startup validation failed", error.Message);
            Assert.Contains("incomplete framed batch", error.Message);
            Assert.False(File.Exists(state.FatalStateFilename));
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryFatalState_BlocksStartupWhenStateCannotBeDetermined()
    {
        var inaccessibleStateRoot = Path.GetTempFileName();
        var processStatus = new ProcessStatus();
        var state = new ShareRecoveryFatalState(new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(Path.GetTempPath(),
                Path.GetRandomFileName()),
        }, processStatus, inaccessibleStateRoot);

        try
        {
            var error = Assert.Throws<PoolStartupException>(() =>
                state.EnsureStartupAllowed());

            Assert.Contains("Unable to determine the share-recovery fatal state",
                error.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);
        }
        finally
        {
            File.Delete(inaccessibleStateRoot);
        }
    }

    private static async Task AssertUnexpectedQueueFailureJournalsAsync(
        string failureStage)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-unexpected-{failureStage}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = new[]
            {
                new PoolConfig
                {
                    Id = "ltc-solo",
                    Template = new BitcoinTemplate
                    {
                        Symbol = "LTC",
                        Name = "Litecoin",
                    },
                },
            },
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var processStatus = new ProcessStatus();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var coordinator = new MiningFailStopCoordinator(processStatus,
            lifetime);
        var bus = new MessageBus(coordinator);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var recoveryHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var handled = new TaskCompletionSource<IReadOnlyCollection<Share>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IMapper mapper = AutoMapperFactory.CreateMapper();
        var injected = new InvalidOperationException(
            $"injected {failureStage} failure");

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        blockRepository.InsertAsync(connection, transaction,
                Arg.Any<Block>(), Arg.Any<CancellationToken>())
            .Returns(true);
        recoveryHandler.StopClusterAfterJournalAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                handled.TrySetResult(call.Arg<IReadOnlyCollection<Share>>());
                return Task.CompletedTask;
            });

        switch(failureStage)
        {
            case "mapper":
                mapper = Substitute.For<IMapper>();
                mapper.Map<PersistedShare>(Arg.Any<object>())
                    .Returns(_ => throw injected);
                break;
            case "open":
                connectionFactory.OpenConnectionAsync()
                    .Returns(Task.FromException<IDbConnection>(injected));
                break;
            case "begin":
                connection.When(x => x.BeginTransaction(
                        Arg.Any<IsolationLevel>()))
                    .Do(_ => throw injected);
                break;
            case "batch":
                shareRepository.BatchInsertAsync(connection, transaction,
                        Arg.Any<IEnumerable<PersistedShare>>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(injected));
                break;
            case "block":
                blockRepository.InsertAsync(connection, transaction,
                        Arg.Any<Block>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<bool>(injected));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failureStage));
        }

        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), shareRepository, blockRepository,
            config, bus, recoveryHandler, coordinator);
        var submitted = new Share
        {
            PoolId = "ltc-solo",
            Miner = $"unexpected-{failureStage}",
            IsBlockCandidate = failureStage == "block",
            BlockOnly = failureStage == "block",
            BlockHeight = 123,
            BlockHash = failureStage == "block" ? "block-insert-failure" : null,
            Created = DateTime.UtcNow,
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(submitted);

            var journalled = await handled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(submitted, Assert.Single(journalled));
            Assert.Equal(ProcessExitCodes.GeneralFailure, processStatus.ExitCode);
            Assert.True(coordinator.IsFailStopRequested);
            Assert.Contains(submitted.Miner,
                await File.ReadAllTextAsync(recoveryFilename));
            await recoveryHandler.Received(1).StopClusterAfterJournalAsync(
                Arg.Is<IReadOnlyCollection<Share>>(shares =>
                    shares.Count == 1 && ReferenceEquals(shares.Single(), submitted)),
                recoveryFilename,
                Arg.Is<InvalidOperationException>(ex => ex == injected));
            await recoveryHandler.DidNotReceive().StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), Arg.Any<string>(),
                Arg.Any<Exception>());
        }
        finally
        {
            try
            {
                await recorder.StopAsync(CancellationToken.None);
            }
            catch
            {
            }

            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    private static void CreateHardLinkForTest(string linkFilename,
        string existingFilename)
    {
        int error;

        if(OperatingSystem.IsWindows())
        {
            if(CreateHardLinkWindows(linkFilename, existingFilename,
                   IntPtr.Zero))
                return;
            error = Marshal.GetLastPInvokeError();
        }
        else if(OperatingSystem.IsLinux())
        {
            if(CreateHardLinkLinux(existingFilename, linkFilename) == 0)
                return;
            error = Marshal.GetLastPInvokeError();
        }
        else
            throw new PlatformNotSupportedException(
                "Recovery hard-link identity tests require Windows or Linux");

        throw new IOException($"Unable to create recovery test hard link (error {error})");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string linkFilename,
        string existingFilename, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkLinux(string existingFilename,
        string linkFilename);

    private static Share CreateDurableCandidate(string hash,
        string type = "merged-parent") => new()
    {
        PoolId = type == "auxpow" ? "doge-solo" : "ltc-solo",
        Miner = type == "auxpow" ? "DFinancialBeneficiary" : "LFinancialBeneficiary",
        BlockHeight = 123,
        BlockHash = hash,
        BlockType = type,
        IsBlockCandidate = true,
        BlockOnly = true,
        TransactionConfirmationData = type == "auxpow"
            ? $"auxpow-block:{hash}"
            : "coinbase-transaction",
    };

    private static RecoveryFixture CreateRecoveryFixture(IMessageBus messageBusOverride = null)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = messageBusOverride ?? Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var pool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.TryRegisterRecoveryImportAsync(connection, transaction,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), shareRepository, blockRepository,
            new ClusterConfig { Pools = new[] { pool } }, messageBus);

        return new RecoveryFixture(recorder, connectionFactory, connection,
            transaction, shareRepository, blockRepository, messageBus);
    }

    private static RecoveryFixture CreateConfiguredRecoveryFixture(ClusterConfig config)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.TryRegisterRecoveryImportAsync(connection, transaction,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, blockRepository, config, messageBus);

        return new RecoveryFixture(recorder, connectionFactory, connection,
            transaction, shareRepository, blockRepository, messageBus);
    }

    private static string RecoveryShareJson(int index, bool blockOnly = false)
    {
        return JsonConvert.SerializeObject(new Share
        {
            PoolId = blockOnly ? "doge-solo" : "ltc-solo",
            Miner = $"miner-{index}",
            Difficulty = index + 1,
            Created = DateTime.UnixEpoch.AddSeconds(index),
            BlockOnly = blockOnly,
            IsBlockCandidate = blockOnly,
            BlockType = blockOnly ? "auxpow" : null,
            BlockHash = blockOnly ? $"doge-block-{index}" : null,
            BlockHeight = blockOnly ? index + 1 : 0,
            TransactionConfirmationData = blockOnly
                ? $"auxpow-block:doge-block-{index}"
                : null,
        });
    }

    private static async Task<string> WriteRecoveryFileAsync(
        IEnumerable<string> records)
    {
        var filename = Path.GetTempFileName();
        await File.WriteAllLinesAsync(filename, records);
        return filename;
    }

    private sealed record RecoveryFixture(ShareRecorder Recorder,
        IConnectionFactory ConnectionFactory, IDbConnection Connection,
        IDbTransaction Transaction, IShareRepository ShareRepository,
        IBlockRepository BlockRepository, IMessageBus MessageBus);

    private class PartialWriteFailureStream : MemoryStream
    {
        public PartialWriteFailureStream(byte[] initialContent,
            int bytesBeforeFailure)
        {
            this.bytesBeforeFailure = bytesBeforeFailure;
            Write(initialContent, 0, initialContent.Length);
            Position = 0;
        }

        private readonly int bytesBeforeFailure;
        private bool shouldFail = true;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if(!shouldFail)
                return base.WriteAsync(buffer, cancellationToken);

            shouldFail = false;
            var partialLength = Math.Min(bytesBeforeFailure, buffer.Length);
            base.Write(buffer.Span[..partialLength]);

            return ValueTask.FromException(new IOException(
                "simulated recovery-journal write failure"));
        }
    }

    private sealed class RollbackFailureStream : PartialWriteFailureStream
    {
        public RollbackFailureStream(byte[] initialContent,
            int bytesBeforeFailure) : base(initialContent, bytesBeforeFailure)
        {
        }

        public override void SetLength(long value)
        {
            throw new IOException(
                "simulated recovery-journal rollback failure");
        }
    }

    private sealed class FlushFailureFileStream : FileStream
    {
        public FlushFailureFileStream(string filename) : base(filename,
            FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
        {
        }

        public bool FailAsyncFlushOnce { get; set; }
        public bool FailDurableFlushOnce { get; set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if(FailAsyncFlushOnce)
            {
                FailAsyncFlushOnce = false;
                return Task.FromException(new IOException(
                    "simulated FlushAsync failure"));
            }

            return base.FlushAsync(cancellationToken);
        }

        public override void Flush(bool flushToDisk)
        {
            if(flushToDisk && FailDurableFlushOnce)
            {
                FailDurableFlushOnce = false;
                throw new IOException("simulated Flush(true) failure");
            }

            base.Flush(flushToDisk);
        }
    }

    private sealed class OutOfMemoryTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string value) =>
            throw new OutOfMemoryException("simulated verifier memory pressure");
    }

    [ProtoContract]
    private sealed class LegacyRelayShare
    {
        [ProtoMember(1)]
        public string PoolId { get; set; }

        [ProtoMember(12)]
        public bool IsBlockCandidate { get; set; }

        [ProtoMember(15)]
        public DateTime Created { get; set; }
    }
}
