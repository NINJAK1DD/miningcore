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
            Assert.Equal(2, recorder.PersistenceQueueDepth);
            Assert.True(recorder.PersistenceQueueOverflowCount > 0);
            Assert.InRange(recorder.EmergencyJournalQueueHighWatermark, 1,
                recorder.EmergencyJournalQueueCapacity);
            Assert.Equal(0, recorder.EmergencyJournalQueueDepth);
            Assert.Equal(0, recorder.EmergencyJournalQueueOverflowCount);
            var journal = await File.ReadAllTextAsync(recoveryFilename,
                timeout.Token);
            Assert.Contains("journal-overflow", journal);
            Assert.DoesNotContain("queued-one", journal);
            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
        }
        finally
        {
            releaseDatabase.TrySetResult();
            await recorder.StopAsync(CancellationToken.None);
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EmergencyJournal_SustainedOverflowDrainsInForceFlushedBatches()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-emergency-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
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
        var flushCalls = 0;
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                databaseEntered.TrySetResult();
                await releaseDatabase.Task;
            });
        var bus = new MessageBus();
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, bus)
        {
            PersistenceQueueCapacity = 250,
            EmergencyJournalQueueCapacity = 128,
            RecoveryJournalFlush = async stream =>
            {
                var call = Interlocked.Increment(ref flushCalls);
                if(call == 1)
                {
                    journalEntered.TrySetResult();
                    await releaseJournal.Task;
                }

                await stream.FlushAsync();
                if(stream is FileStream fileStream)
                    fileStream.Flush(true);
                else
                    stream.Flush();
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
                    { PoolId = "ltc-solo", Miner = $"primary-{index}" });

            var overflow = Enumerable.Range(0, 101)
                .Select(index => new Share
                    { PoolId = "ltc-solo", Miner = $"overflow-{index}" })
                .ToArray();
            bus.SendMessage(overflow[0]);
            await journalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            foreach(var share in overflow.Skip(1))
                bus.SendMessage(share);

            releaseJournal.TrySetResult();
            await Task.WhenAll(overflow.Select(x => x.PersistenceAdmission))
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, Volatile.Read(ref flushCalls));
            Assert.Equal(0, recorder.EmergencyJournalQueueDepth);
            Assert.Equal(0, recorder.EmergencyJournalQueueOverflowCount);
            var journal = await File.ReadAllTextAsync(recoveryFilename);
            Assert.Contains("overflow-0", journal);
            Assert.Contains("overflow-100", journal);
        }
        finally
        {
            releaseJournal.TrySetResult();
            releaseDatabase.TrySetResult();
            await recorder.StopAsync(CancellationToken.None);
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
        using var competingOwnership = new ShareRecoveryPathOwnership(config);
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
                Assert.Throws<IOException>(competingOwnership.Acquire);
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
            competingOwnership.Acquire();
            competingOwnership.Release();
        }
        finally
        {
            releaseJournal.TrySetResult();
            // An assertion or timeout after a successful competing Acquire must not leave the
            // Windows ownership handle open and replace the real failure during directory cleanup.
            competingOwnership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_SharesOneDeadlineAcrossRecoveryAndDeferredEvidence()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var messageBus = new MessageBus();
        var recoveryFailureHandler = Substitute.For<IShareRecoveryFailureHandler>();
        recoveryFailureHandler.StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(Task.CompletedTask);
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                databaseEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                    call.Arg<CancellationToken>());
            });
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, messageBus, recoveryFailureHandler,
            Substitute.For<IMiningFailStopCoordinator>())
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(200),
            ShutdownRecoveryCompletionTimeout = TimeSpan.FromMilliseconds(1000),
            RecoveryJournalFlush = async stream =>
            {
                // Consume most of the recovery phase. The deferred evidence wait must receive only
                // the shared remainder, not a fresh one-second allowance.
                await Task.Delay(TimeSpan.FromMilliseconds(600));
                await stream.FlushAsync();
                if(stream is FileStream fileStream)
                    fileStream.Flush(true);
            },
        };
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 250; index++)
                messageBus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"shared-deadline-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            GetDeferredFailStopTasks(recorder).Add(neverCompletes.Task);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() =>
                recorder.StopAsync(CancellationToken.None));
            elapsed.Stop();

            Assert.InRange(elapsed.Elapsed,
                TimeSpan.FromMilliseconds(950), TimeSpan.FromMilliseconds(1600));
        }
        finally
        {
            neverCompletes.TrySetCanceled();
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
    public async Task StopAsync_DeferredEvidenceFaultReleasesRecorderScopedOwnership()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        var recorder = CreateRecorderWithOwnership(config, ownership);

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            GetDeferredFailStopTasks(recorder).Add(Task.FromException(
                new IOException("simulated deferred evidence failure")));

            var error = await Assert.ThrowsAsync<IOException>(() =>
                recorder.StopAsync(CancellationToken.None));
            Assert.Contains("simulated deferred evidence failure", error.Message);

            using var contender = new ShareRecoveryPathOwnership(config);
            contender.Acquire();
            contender.Release();
        }
        finally
        {
            ownership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(config.ShareRecoveryFile);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_FaultedExecuteTaskReleasesRecorderScopedOwnership()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var pipelineFailure = new InvalidOperationException(
            "simulated persistence-pipeline failure");
        connectionFactory.OpenConnectionAsync()
            .Returns(Task.FromException<IDbConnection>(pipelineFailure));
        var recoveryHandler = Substitute.For<IShareRecoveryFailureHandler>();
        var handled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        recoveryHandler.StopClusterAfterJournalAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                pipelineFailure)
            .Returns(_ =>
            {
                handled.TrySetResult();
                return Task.CompletedTask;
            });
        var messageBus = new MessageBus();
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, messageBus, recoveryHandler,
            Substitute.For<IMiningFailStopCoordinator>(), ownership);

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            messageBus.SendMessage(new Share
                { PoolId = "ltc-solo", Miner = "faulted-execute-task" });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recorder.StopAsync(CancellationToken.None));
            Assert.Same(pipelineFailure, error);
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var contender = new ShareRecoveryPathOwnership(config);
            contender.Acquire();
            contender.Release();
        }
        finally
        {
            ownership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task StopAsync_TimedOutExecuteTaskRetainsRecorderScopedOwnership()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDatabase = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                databaseEntered.TrySetResult();
                // Deliberately model a provider operation that does not honour cancellation.
                return releaseDatabase.Task;
            });
        var recoveryHandler = Substitute.For<IShareRecoveryFailureHandler>();
        recoveryHandler.StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(Task.CompletedTask);
        var messageBus = new MessageBus();
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(), config,
            messageBus, recoveryHandler, Substitute.For<IMiningFailStopCoordinator>(),
            ownership)
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(50),
            ShutdownRecoveryCompletionTimeout = TimeSpan.FromMilliseconds(100),
        };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            messageBus.SendMessage(new Share
                { PoolId = "ltc-solo", Miner = "timed-out-execute-task" });
            var stopping = recorder.StopAsync(CancellationToken.None);
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<TimeoutException>(() => stopping);

            using var contender = new ShareRecoveryPathOwnership(config);
            Assert.Throws<IOException>(contender.Acquire);
        }
        finally
        {
            // The assertion above deliberately exercises very small production deadlines.
            // Cleanup has a different contract: after releasing the fake provider, wait long
            // enough for the persistence worker to relinquish every recovery-journal handle.
            recorder.ShutdownPersistenceDrainTimeout = TimeSpan.FromSeconds(5);
            recorder.ShutdownRecoveryCompletionTimeout = TimeSpan.FromSeconds(5);
            releaseDatabase.TrySetResult();
            await StopRecorderBeforeFixtureCleanupAsync(recorder);

            ownership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FixtureCleanup_RejectsServiceTimeoutThatMayLeaveWorkerActive()
    {
        var timeout = new TimeoutException(
            "simulated completed stop with an active persistence worker");

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            AwaitRecorderStopBeforeFixtureCleanupAsync(
                Task.FromException(timeout)));

        Assert.Same(timeout, error);
    }

    [Fact]
    public async Task StopAsync_DeferredEvidenceTimeoutExplicitlyRetainsOwnership()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        var recorder = CreateRecorderWithOwnership(config, ownership);
        recorder.ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(50);
        recorder.ShutdownRecoveryCompletionTimeout = TimeSpan.FromMilliseconds(100);
        var neverCompletes = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            GetDeferredFailStopTasks(recorder).Add(neverCompletes.Task);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                recorder.StopAsync(CancellationToken.None));

            using var contender = new ShareRecoveryPathOwnership(config);
            Assert.Throws<IOException>(contender.Acquire);
        }
        finally
        {
            neverCompletes.TrySetCanceled();
            ownership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(config.ShareRecoveryFile);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Recorder_DoesNotNestOrReleaseProcessPreflightOwnership()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var ownership = new ShareRecoveryPathOwnership(config);
        ownership.Acquire();
        var recorder = CreateRecorderWithOwnership(config, ownership);

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            await recorder.StopAsync(CancellationToken.None);

            Assert.True(ownership.IsHeld);
            using var contender = new ShareRecoveryPathOwnership(config);
            Assert.Throws<IOException>(contender.Acquire);

            ownership.Release();
            contender.Acquire();
            contender.Release();
        }
        finally
        {
            ownership.Release();
            ShareRecorder.ForgetRecoveryWriteStateForTests(config.ShareRecoveryFile);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ShutdownJournal_CompletesEmergencyPersistenceAdmission()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-shutdown-admission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var databaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.BatchInsertAsync(connection, transaction,
                Arg.Any<IEnumerable<PersistedShare>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                databaseEntered.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan,
                    call.Arg<CancellationToken>());
            });
        var bus = new MessageBus();
        var recorder = new ShareRecorder(connectionFactory,
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            shareRepository, Substitute.For<IBlockRepository>(),
            new ClusterConfig
            {
                Pools = Array.Empty<PoolConfig>(),
                ShareRecoveryFile = recoveryFilename,
            }, bus)
        {
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(50),
        };
        Task stopping = null;

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            for(var index = 0; index < 250; index++)
                bus.SendMessage(new Share
                    { PoolId = "ltc-solo", Miner = $"database-blocked-{index}" });
            await databaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Inject the otherwise timing-sensitive state reached when an overflow share has
            // received an emergency-lane completion but remains in the unresolved registry as
            // the shutdown snapshot takes ownership of it.
            var share = new Share
                { PoolId = "ltc-solo", Miner = "shutdown-emergency" };
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            share.SetPersistenceAdmission(completion.Task);
            var queuedType = typeof(ShareRecorder).GetNestedType("QueuedShare",
                System.Reflection.BindingFlags.NonPublic)!;
            var queued = Activator.CreateInstance(queuedType,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null, new object[] { long.MaxValue, share }, null)!;
            queuedType.GetProperty("JournalCompletion")!
                .SetValue(queued, completion);
            var unresolved = typeof(ShareRecorder).GetField("unresolvedShares",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.GetValue(recorder)!;
            var added = (bool) unresolved.GetType().GetMethod("TryAdd")!
                .Invoke(unresolved, new[] { (object) long.MaxValue, queued })!;
            Assert.True(added);

            stopping = recorder.StopAsync(CancellationToken.None);
            await stopping.WaitAsync(TimeSpan.FromSeconds(10));

            await share.PersistenceAdmission.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(share.PersistenceAdmission.IsCompletedSuccessfully);
            Assert.Contains("shutdown-emergency",
                await File.ReadAllTextAsync(recoveryFilename));
        }
        finally
        {
            stopping ??= recorder.StopAsync(CancellationToken.None);
            await AwaitRecorderStopBeforeFixtureCleanupAsync(stopping);

            if(Directory.Exists(directory))
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

    [Theory]
    [InlineData("transaction", false)]
    [InlineData("connection", false)]
    [InlineData("transaction", true)]
    [InlineData("connection", true)]
    public async Task PersistenceQueue_CleanupCompletingAtTimeoutNeverMakesClassifiedBatchReplayable(
        string cleanupStage, bool uncertainCommit)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-cleanup-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var handler = Substitute.For<IShareRecoveryFailureHandler>();
        var completed = new TaskCompletionSource<(IReadOnlyCollection<Share> Shares,
            Exception Error, bool Uncertain)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFailure = new IOException($"{cleanupStage} late cleanup failure");
        var commitFailure = new IOException("commit transport failure");
        var bus = new MessageBus();
        connectionFactory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        if(uncertainCommit)
            transaction.When(x => x.Commit()).Do(_ => throw commitFailure);

        void BlockDisposal()
        {
            disposeEntered.TrySetResult();
            releaseDispose.Task.GetAwaiter().GetResult();
            throw cleanupFailure;
        }

        if(cleanupStage == "transaction")
            transaction.When(x => x.Dispose()).Do(_ => BlockDisposal());
        else
            connection.When(x => x.Dispose()).Do(_ => BlockDisposal());

        handler.StopClusterForUncertainCommitAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                completed.TrySetResult((call.Arg<IReadOnlyCollection<Share>>(),
                    call.Arg<Exception>(), true));
                return Task.CompletedTask;
            });
        handler.StopClusterAfterJournalAsync(
                Arg.Any<IReadOnlyCollection<Share>>(), recoveryFilename,
                Arg.Any<Exception>())
            .Returns(call =>
            {
                completed.TrySetResult((call.Arg<IReadOnlyCollection<Share>>(),
                    call.Arg<Exception>(), false));
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
            Miner = "classified-before-cleanup",
            BlockOnly = true,
            Created = DateTime.UtcNow,
        };
        ConnectionFactoryExtensions.ResourceCleanupWaitOverride.Value =
            async (cleanupTask, ignoredTimeout) =>
            {
                await disposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                releaseDispose.TrySetResult();
                _ = await cleanupTask;
                throw new TimeoutException(
                    "injected deadline before cleanup completion observation");
            };

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(submitted);

            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(uncertainCommit, result.Uncertain);
            var timeout = uncertainCommit
                ? Assert.IsType<TransactionResourceCleanupTimeoutException>(
                    Assert.IsType<TransactionCommitOutcomeUncertainException>(result.Error)
                        .Data[ConnectionFactoryExtensions.CleanupExceptionDataKey])
                : Assert.IsType<TransactionResourceCleanupTimeoutException>(
                    Assert.IsType<TransactionCommittedCleanupException>(result.Error)
                        .InnerException);
            if(uncertainCommit)
            {
                Assert.Same(submitted, Assert.Single(result.Shares));
                Assert.Same(commitFailure, result.Error.InnerException);
            }
            else
                Assert.Empty(result.Shares);
            Assert.Same(cleanupFailure,
                timeout.Data[ConnectionFactoryExtensions.LateCleanupExceptionDataKey]);
            Assert.False(File.Exists(recoveryFilename));
        }
        finally
        {
            ConnectionFactoryExtensions.ResourceCleanupWaitOverride.Value = null;
            releaseDispose.TrySetResult();
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
        Task stopping = null;

        try
        {
            await recorder.StartAsync(CancellationToken.None);
            bus.SendMessage(candidate);
            await blockInsertEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            stopping = recorder.StopAsync(CancellationToken.None);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("block-insert-stall",
                await File.ReadAllTextAsync(recoveryFilename));
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
        }
        finally
        {
            stopping ??= recorder.StopAsync(CancellationToken.None);
            await AwaitRecorderStopBeforeFixtureCleanupAsync(stopping);
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
            await StopRecorderBeforeFixtureCleanupAsync(recorder);

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
        var completedIncidentPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fatalState.CompletedIncidentPublishedCheckpoint = () =>
            completedIncidentPublished.TrySetResult();
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
            // This fixture validates fail-stop capture, not production shutdown
            // timing. Keep its internal terminal boundaries well inside the outer
            // cleanup guard so Windows never sees a live owner during deletion.
            ShutdownPersistenceDrainTimeout = TimeSpan.FromMilliseconds(250),
            ShutdownRecoveryCompletionTimeout = TimeSpan.FromSeconds(1),
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

            // The exact-share sidecar uses durable writes and can take longer than
            // a generic polling window on a loaded Windows runner. Wait for the
            // production publication boundary, then poll only the final latch handoff.
            await completedIncidentPublished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(
                () => FatalStateIsComplete(fatalState.FatalStateFilename),
                TimeSpan.FromSeconds(5));
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
            Assert.Equal(recorder.PersistenceQueueCapacity,
                recorder.PersistenceQueueHighWatermark);
            Assert.True(recorder.PersistenceQueueOverflowCount >= 3);
            Assert.Equal(recorder.EmergencyJournalQueueCapacity,
                recorder.EmergencyJournalQueueHighWatermark);
            Assert.Equal(1, recorder.EmergencyJournalQueueOverflowCount);
        }
        finally
        {
            releaseJournal.TrySetResult();
            releaseDatabase.TrySetResult();
            await StopRecorderBeforeFixtureCleanupAsync(recorder);

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
            Pools = new[] { new PoolConfig { Id = "ltc-solo" } },
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
        shareRepository.HasMatchingRecoveryImportAsync(connection,
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
    public async Task RecoverConfiguredJournal_MarkerChangedBeforeMoveFailsClosed()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-marker-race-{Guid.NewGuid():N}");
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
                new Share { PoolId = "ltc-solo", Miner = "marker-race" },
            });
            fixture.Recorder.RecoveryArchiveMoveCheckpoint = () =>
            {
                var content = File.ReadAllText(
                    fixture.Recorder.RecoveryImportStateFilename);
                var archiveLine = content.Split('\n').Single(x =>
                    x.StartsWith("archivePathBase64=", StringComparison.Ordinal));
                var encoded = archiveLine["archivePathBase64=".Length..]
                    .TrimEnd('\r');
                var originalArchive = Encoding.UTF8.GetString(
                    Convert.FromBase64String(encoded));
                var changedArchive = recoveryFilename + ".imported-tampered";
                File.WriteAllText(fixture.Recorder.RecoveryImportStateFilename,
                    content.Replace(encoded,
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(changedArchive)),
                        StringComparison.Ordinal));
                Assert.NotEqual(originalArchive, changedArchive);
            };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("changed during source retirement", error.Message);
            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.Empty(Directory.GetFiles(directory,
                "recovered-shares.txt.imported-*"));
        }
        finally
        {
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
            Pools = new[] { new PoolConfig { Id = "ltc-solo" } },
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
        shareRepository.HasMatchingRecoveryImportAsync(connection,
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RecoverConfiguredJournal_CommittedDatabaseRetirementDoesNotRequireHistoricalPoolId(
        bool committedMarkerWasDurable, bool auxPowIndexesRemovedAfterCommit)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-retirement-pool-change-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var importConfig = new ClusterConfig
        {
            Pools = new[] { new PoolConfig { Id = "ltc-solo" } },
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = stateDirectory,
        };
        var fixture = CreateConfiguredRecoveryFixture(importConfig);

        try
        {
            var recoveryRecord = auxPowIndexesRemovedAfterCommit
                ? CreateDurableCandidate("committed-auxpow-before-pool-removal", "auxpow")
                : new Share
                {
                    PoolId = "ltc-solo",
                    Miner = "committed-before-pool-removal",
                };
            recoveryRecord.PoolId = "ltc-solo";
            fixture.BlockRepository.HasMergedMiningBlockIndexesAsync(
                    fixture.Connection, Arg.Any<CancellationToken>())
                .Returns(true);
            await fixture.Recorder.WriteRecoveryJournalAsync(new[] { recoveryRecord });
            if(committedMarkerWasDurable)
            {
                fixture.Recorder.RecoveryArchiveMove = (_, _) =>
                    throw new IOException(
                        "retain committed source before pool change");
            }
            else
            {
                fixture.Recorder.RecoveryImportStateWriteCheckpoint = phase =>
                {
                    if(phase == ShareRecoveryImportState.ImportPhase.Committed)
                        throw new IOException(
                            "crash after database commit before marker advance");
                };
            }

            await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            var marker = new ShareRecoveryImportState(recoveryFilename,
                stateDirectory).TryRead();
            Assert.Equal(committedMarkerWasDurable
                    ? ShareRecoveryImportState.ImportPhase.Committed
                    : ShareRecoveryImportState.ImportPhase.Pending,
                marker.Phase);
            fixture.Transaction.Received(1).Commit();
            fixture.ShareRepository.TryRegisterRecoveryImportAsync(
                    fixture.Connection, fixture.Transaction,
                    Arg.Any<string>(), Path.GetFileName(recoveryFilename), 1,
                    Arg.Any<CancellationToken>())
                .Returns(false);
            if(auxPowIndexesRemovedAfterCommit)
            {
                // The database manifest proves this pending marker already committed. Removing
                // the indexes afterwards must not block evidence retirement or replay the block.
                fixture.BlockRepository.HasMergedMiningBlockIndexesAsync(
                        fixture.Connection, Arg.Any<CancellationToken>())
                    .Returns(false);
            }

            // Simulate the restart using a configuration from which the already-imported
            // historical pool has since been removed. Retirement must authenticate the committed
            // evidence rather than reapplying the pre-import attribution allowlist.
            var resumeConfig = new ClusterConfig
            {
                Pools = new[] { new PoolConfig { Id = "current-pool" } },
                Persistence = new PersistenceConfig
                {
                    Postgres = new PostgresConfig
                    {
                        Host = "127.0.0.1",
                        Port = 5432,
                        Database = "miningcore",
                        User = "miningcore",
                    },
                },
                ShareRecoveryFile = recoveryFilename,
                ShareRecoveryStateDirectory = stateDirectory,
            };
            fixture.ShareRepository.GetMissingSharePartitionsAsync(
                    fixture.Connection,
                    Arg.Is<IEnumerable<string>>(ids =>
                        ids.SequenceEqual(new[] { "current-pool" })),
                    Arg.Any<CancellationToken>())
                .Returns(Array.Empty<string>());
            fixture.ShareRepository.HasRecoveryImportSchemaAsync(
                    fixture.Connection, Arg.Any<CancellationToken>())
                .Returns(true);

            // Exercise the same database guards that production -rs runs before invoking the
            // recorder. They validate today's configured pool, not the retired journal's
            // historical attribution boundary.
            await Program.EnsureSharePartitionsAsync(true, resumeConfig,
                fixture.ConnectionFactory, fixture.ShareRepository,
                CancellationToken.None);
            await Program.EnsureShareRecoverySchemaAsync(true, resumeConfig,
                fixture.ConnectionFactory, fixture.ShareRepository,
                CancellationToken.None);

            var resumed = new ShareRecorder(fixture.ConnectionFactory,
                AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
                fixture.ShareRepository, fixture.BlockRepository, resumeConfig,
                fixture.MessageBus);

            var archive = await resumed.RecoverSharesAsync(recoveryFilename);

            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(recoveryFilename));
            Assert.False(File.Exists(resumed.RecoveryImportStateFilename));
            Assert.False(File.Exists(resumed.RecoveryTerminalStateFilename));
            await fixture.ShareRepository.Received(1)
                .TryRegisterRecoveryImportAsync(fixture.Connection,
                    fixture.Transaction, Arg.Any<string>(),
                    Path.GetFileName(recoveryFilename), 1,
                    Arg.Any<CancellationToken>());
            if(auxPowIndexesRemovedAfterCommit)
            {
                await fixture.BlockRepository.Received(1)
                    .HasMergedMiningBlockIndexesAsync(fixture.Connection,
                        Arg.Any<CancellationToken>());
                await fixture.BlockRepository.Received(1).InsertAsync(
                    fixture.Connection, fixture.Transaction,
                    Arg.Any<Block>(), Arg.Any<CancellationToken>());
                await fixture.ShareRepository.DidNotReceive().BatchInsertAsync(
                    fixture.Connection, fixture.Transaction,
                    Arg.Any<IEnumerable<PersistedShare>>(),
                    Arg.Any<CancellationToken>());
            }
            else
            {
                await fixture.ShareRepository.Received(1).BatchInsertAsync(
                    fixture.Connection, fixture.Transaction,
                    Arg.Any<IEnumerable<PersistedShare>>(),
                    Arg.Any<CancellationToken>());
            }
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverConfiguredJournal_RefusesRetirementWhenManifestCannotBeProven()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-manifest-proof-{Guid.NewGuid():N}");
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
                new Share { PoolId = "ltc-solo", Miner = "manifest-required" },
            });
            fixture.Recorder.RecoveryArchiveMove = (_, _) =>
                throw new IOException("retain committed source before retirement");
            await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));
            var marker = new ShareRecoveryImportState(recoveryFilename,
                config.ShareRecoveryStateDirectory).TryRead();
            Assert.Equal(ShareRecoveryImportState.ImportPhase.Committed,
                marker.Phase);
            fixture.ShareRepository.HasMatchingRecoveryImportAsync(
                    fixture.Connection, marker.FileHash,
                    Path.GetFileName(recoveryFilename),
                    marker.RecordCount, Arg.Any<CancellationToken>())
                .Returns(false);
            fixture.Recorder.RecoveryArchiveMove = File.Move;

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("PostgreSQL cannot prove the committed recovery import",
                error.Message);
            Assert.True(File.Exists(recoveryFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            Assert.False(File.Exists(marker.ArchiveFilename));
            await fixture.ShareRepository.Received(2)
                .HasMatchingRecoveryImportAsync(fixture.Connection,
                    marker.FileHash, Path.GetFileName(recoveryFilename),
                    marker.RecordCount,
                    Arg.Any<CancellationToken>());
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

            Assert.Contains("terminal state no longer matches its durable import marker",
                error.Message);
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
    public async Task RecoverAlternateSource_IdentityInspectionFailureCannotBypassConfiguredState()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-alias-access-{Guid.NewGuid():N}");
        var alternateDirectory = Path.Combine(directory, "review");
        Directory.CreateDirectory(alternateDirectory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var alternateFilename = Path.Combine(alternateDirectory, "reviewed-copy.txt");
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
                new Share { PoolId = "ltc-solo", Miner = "identity-uncertain" },
            });
            File.Copy(recoveryFilename, alternateFilename);
            fixture.Recorder.RecoveryAliasEnumerateEntries = inspectedDirectory =>
            {
                if(string.Equals(Path.GetFullPath(inspectedDirectory),
                       Path.GetFullPath(directory),
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal))
                    throw new UnauthorizedAccessException(
                        "injected configured-source identity failure");

                return Directory.EnumerateFileSystemEntries(inspectedDirectory);
            };

            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.Recorder.RecoverSharesAsync(alternateFilename));

            Assert.Contains("identity failure", error.Message);
            Assert.True(File.Exists(alternateFilename));
            Assert.True(File.Exists(recoveryFilename));
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
    public async Task RecoverConfiguredJournal_StableSymlinkParentCompletesDurableRetirement()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-linked-parent-{Guid.NewGuid():N}");
        var physicalDirectory = Path.Combine(directory, "physical");
        var linkedDirectory = Path.Combine(directory, "configured");
        Directory.CreateDirectory(physicalDirectory);
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, physicalDirectory);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }

        var recoveryFilename = Path.Combine(linkedDirectory,
            "recovered-shares.txt");
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
                new Share { PoolId = "ltc-solo", Miner = "linked-parent" },
            });

            var archive = await fixture.Recorder.RecoverSharesAsync(
                recoveryFilename);

            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(recoveryFilename));
            Assert.False(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            await fixture.ShareRepository.Received(1)
                .TryRegisterRecoveryImportAsync(fixture.Connection,
                    fixture.Transaction, Arg.Any<string>(),
                    "recovered-shares.txt", 1, Arg.Any<CancellationToken>());

            var status = new ProcessStatus();
            new ShareRecoveryFatalState(config, status,
                    config.ShareRecoveryStateDirectory)
                .EnsureStartupAllowed();
            Assert.Equal(0, status.ExitCode);
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_RejectsFinalSymbolicLinkBeforeDatabaseAccess()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-final-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "target.txt");
        var source = Path.Combine(directory, "reviewed.txt");
        await File.WriteAllTextAsync(target, RecoveryShareJson(0));
        try
        {
            File.CreateSymbolicLink(source, target);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(directory, true);
            return;
        }
        var fixture = CreateRecoveryFixture();

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(source));
            await fixture.ShareRepository.DidNotReceiveWithAnyArgs()
                .TryRegisterRecoveryImportAsync(default, default, default,
                    default, default, default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LinuxRecoverSharesAsync_RejectsFifoWithoutBlockingOrDatabaseAccess()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-fifo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "reviewed.txt");
        if(mkfifo(source, Convert.ToUInt32("600", 8)) != 0)
            throw new IOException(
                $"Unable to create FIFO fixture (error {Marshal.GetLastPInvokeError()})");
        var fixture = CreateRecoveryFixture();

        try
        {
            var import = fixture.Recorder.RecoverSharesAsync(source);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                import.WaitAsync(TimeSpan.FromSeconds(2)));
            await fixture.ShareRepository.DidNotReceiveWithAnyArgs()
                .TryRegisterRecoveryImportAsync(default, default, default,
                    default, default, default);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("after-archive-phase", false)]
    [InlineData("after-anchor-removal", false)]
    [InlineData("during-marker-deletion", false)]
    [InlineData("during-marker-directory-sync", false)]
    [InlineData("after-archive-phase", true)]
    [InlineData("after-anchor-removal", true)]
    [InlineData("during-marker-deletion", true)]
    [InlineData("during-marker-directory-sync", true)]
    public async Task RecoverConfiguredJournal_EachDurableRetirementPhaseIsSafeToResume(
        string failurePoint, bool useLinkedParent)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-phase-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryDirectory = directory;
        if(useLinkedParent)
        {
            var physical = Path.Combine(directory, "physical");
            recoveryDirectory = Path.Combine(directory, "configured");
            Directory.CreateDirectory(physical);
            try
            {
                Directory.CreateSymbolicLink(recoveryDirectory, physical);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                Directory.Delete(directory, true);
                return;
            }
        }
        var recoveryFilename = Path.Combine(recoveryDirectory,
            "recovered-shares.txt");
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
                new Share { PoolId = "ltc-solo", Miner = failurePoint },
            });

            switch(failurePoint)
            {
                case "after-archive-phase":
                    fixture.Recorder.RecoveryImportStateWriteCheckpoint = phase =>
                    {
                        if(phase == ShareRecoveryImportState.ImportPhase
                               .AnchorRetirementAuthorised)
                            throw new IOException("injected after archive phase");
                    };
                    break;
                case "after-anchor-removal":
                    fixture.Recorder.RecoveryAnchorRemovedCheckpoint = () =>
                        throw new IOException("injected after anchor removal");
                    break;
                case "during-marker-deletion":
                    fixture.Recorder.RecoveryImportStateRemoveCheckpoint = () =>
                        throw new IOException("injected marker deletion");
                    break;
                case "during-marker-directory-sync":
                    fixture.Recorder.RecoveryImportStateRemoveDirectorySyncCheckpoint = () =>
                        throw new IOException("injected marker directory sync");
                    break;
            }

            var error = await Assert.ThrowsAsync<IOException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));
            Assert.Contains("injected", error.Message);
            Assert.False(File.Exists(recoveryFilename));

            var marker = new ShareRecoveryImportState(recoveryFilename,
                config.ShareRecoveryStateDirectory).TryRead();
            if(failurePoint == "during-marker-directory-sync")
            {
                Assert.Null(marker);
                Assert.False(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
                var processStatus = new ProcessStatus();
                new ShareRecoveryFatalState(config, processStatus,
                        config.ShareRecoveryStateDirectory)
                    .EnsureStartupAllowed();
                Assert.Equal(0, processStatus.ExitCode);
                return;
            }

            Assert.NotNull(marker);
            Assert.True(File.Exists(marker.ArchiveFilename));
            var expectedPhase = failurePoint switch
            {
                "after-archive-phase" =>
                    ShareRecoveryImportState.ImportPhase.ArchiveDurable,
                "after-anchor-removal" =>
                    ShareRecoveryImportState.ImportPhase.AnchorRetirementAuthorised,
                _ => ShareRecoveryImportState.ImportPhase.AnchorRetired,
            };
            Assert.Equal(expectedPhase, marker.Phase);

            fixture.Recorder.RecoveryImportStateWriteCheckpoint = _ => { };
            fixture.Recorder.RecoveryAnchorRemovedCheckpoint = () => { };
            fixture.Recorder.RecoveryImportStateRemoveCheckpoint = () => { };
            var archive = await fixture.Recorder.RecoverSharesAsync(recoveryFilename);

            Assert.Equal(marker.ArchiveFilename, archive);
            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(fixture.Recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(fixture.Recorder.RecoveryTerminalStateFilename));
            await fixture.ShareRepository.Received(1)
                .TryRegisterRecoveryImportAsync(fixture.Connection,
                    fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            if(Directory.Exists(directory))
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
            fixture.ShareRepository.HasMatchingRecoveryImportAsync(
                    fixture.Connection, Arg.Any<string>(),
                    Path.GetFileName(filename), 200,
                    Arg.Any<CancellationToken>())
                .Returns(false, true);

            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);

            fixture.Transaction.Received(1).Rollback();
            fixture.Transaction.Received(1).Commit();
            await fixture.ShareRepository.Received(2)
                .HasMatchingRecoveryImportAsync(fixture.Connection,
                    Arg.Any<string>(), Path.GetFileName(filename), 200,
                    Arg.Any<CancellationToken>());
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
        fixture.BlockRepository.HasMergedMiningBlockIndexesAsync(
                fixture.Connection, Arg.Any<CancellationToken>())
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
    public async Task RecoverSharesAsync_UnconfiguredPoolFailsBeforeMarkerOrTransaction()
    {
        var fixture = CreateRecoveryFixture();
        var record = JsonConvert.DeserializeObject<Share>(RecoveryShareJson(1));
        record.PoolId = "unknown-or-typo-pool";
        var filename = await WriteRecoveryFileAsync(new[]
        {
            JsonConvert.SerializeObject(record),
        });

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
            Assert.Contains("unconfigured pool ID \"unknown-or-typo-pool\"",
                error.Message, StringComparison.Ordinal);
            Assert.Contains("no recovery records were imported", error.Message,
                StringComparison.Ordinal);
            Assert.True(File.Exists(filename));
            fixture.Connection.DidNotReceive().BeginTransaction(
                Arg.Any<IsolationLevel>());
            await fixture.ShareRepository.DidNotReceive()
                .TryRegisterRecoveryImportAsync(Arg.Any<IDbConnection>(),
                    Arg.Any<IDbTransaction>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<int>(),
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(filename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_SanitizedAuxPowRecordRequiresIndexesBeforeImportTransaction()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var configFilename = Path.Combine(directory, "config.json");
        var recoveryFilename = Path.Combine(directory, "reviewed-recovery.txt");
        var activeRecoveryFilename = Path.Combine(directory,
            "active-recovery.txt");
        var stateDirectory = Path.Combine(directory, "state");
        var configDocument = new
        {
            instanceId = 0,
            persistence = new
            {
                postgres = new
                {
                    host = "127.0.0.1",
                    port = 5432,
                    database = "miningcore",
                    user = "miningcore",
                },
            },
            shareRecoveryFile = activeRecoveryFilename,
            shareRecoveryStateDirectory = stateDirectory,
            pools = new[]
            {
                new
                {
                    id = "doge-solo",
                    coin = "dogecoin",
                    enabled = true,
                    extra = new
                    {
                        mergedMining = new
                        {
                            enabled = true,
                        },
                    },
                },
            },
        };
        var candidate = CreateDurableCandidate("recovered-auxpow", "auxpow");

        try
        {
            await File.WriteAllTextAsync(configFilename,
                JsonConvert.SerializeObject(configDocument));
            await File.WriteAllTextAsync(recoveryFilename,
                JsonConvert.SerializeObject(candidate) + Environment.NewLine);
            var config = Program.ReadAndValidateConfig(configFilename, true);
            var recoveredPool = Assert.Single(config.Pools);
            Assert.False(recoveredPool.Enabled);
            Assert.Null(recoveredPool.Extra);
            Assert.Null(config.InstanceId);
            var fixture = CreateConfiguredRecoveryFixture(config);
            fixture.BlockRepository.HasMergedMiningBlockIndexesAsync(
                    fixture.Connection, Arg.Any<CancellationToken>())
                .Returns(false);

            var error = await Assert.ThrowsAsync<PoolStartupException>(() =>
                fixture.Recorder.RecoverSharesAsync(recoveryFilename));

            Assert.Contains("add_auxpow_block_idempotency.sql", error.Message,
                StringComparison.Ordinal);
            Assert.Contains("has not been imported", error.Message,
                StringComparison.Ordinal);
            Assert.True(File.Exists(recoveryFilename));
            fixture.Connection.DidNotReceive().BeginTransaction(
                Arg.Any<IsolationLevel>());
            await fixture.ShareRepository.DidNotReceive()
                .TryRegisterRecoveryImportAsync(Arg.Any<IDbConnection>(),
                    Arg.Any<IDbTransaction>(), Arg.Any<string>(),
                    Arg.Any<string>(), Arg.Any<int>(),
                    Arg.Any<CancellationToken>());
            await fixture.BlockRepository.Received(1)
                .HasMergedMiningBlockIndexesAsync(fixture.Connection,
                    Arg.Any<CancellationToken>());
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(
                activeRecoveryFilename);
            Directory.Delete(directory, true);
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
    public async Task RecoveryJournal_WindowsConcurrentWritersWaitBehindExclusiveHandle()
    {
        if(!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-windows-writers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var config = new ClusterConfig
        {
            Pools = Array.Empty<PoolConfig>(),
            ShareRecoveryFile = recoveryFilename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };

        ShareRecorder CreateRecorder() => new(
            Substitute.For<IConnectionFactory>(), AutoMapperFactory.CreateMapper(),
            new JsonSerializerSettings(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(), config,
            Substitute.For<IMessageBus>());

        var first = CreateRecorder();
        var second = CreateRecorder();
        var flushEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFlush = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LInitial" },
            });
            first.RecoveryJournalFlush = async stream =>
            {
                flushEntered.TrySetResult(true);
                await releaseFlush.Task;
                await stream.FlushAsync();
                Assert.IsType<FileStream>(stream).Flush(true);
            };

            var firstWrite = first.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "btc-solo", Miner = "BFirstWriter" },
            });
            await flushEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var secondWrite = second.WriteRecoveryJournalAsync(new[]
            {
                new Share { PoolId = "doge-solo", Miner = "DSecondWriter" },
            });
            await Task.Delay(50);
            Assert.False(secondWrite.IsCompleted);

            releaseFlush.TrySetResult(true);
            await Task.WhenAll(firstWrite, secondWrite)
                .WaitAsync(TimeSpan.FromSeconds(5));

            await using var stream = File.OpenRead(recoveryFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
            var records = (await File.ReadAllLinesAsync(recoveryFilename))
                .Count(line => !string.IsNullOrWhiteSpace(line) &&
                    !line.StartsWith('#'));
            Assert.Equal(3, records);
            new ShareRecoveryFatalState(config, new ProcessStatus(),
                    config.ShareRecoveryStateDirectory)
                .EnsureStartupAllowed();
        }
        finally
        {
            releaseFlush.TrySetResult(true);
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RecoveryJournal_NoFollowWriterRejectsSubstitutionAfterInspection()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-open-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var originalFilename = Path.Combine(directory, "original-journal.txt");
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
                new Share { PoolId = "ltc-solo", Miner = "LOriginal" },
            });
            recorder.RecoveryJournalPathValidatedCheckpoint = () =>
            {
                File.Move(recoveryFilename, originalFilename);
                File.CreateSymbolicLink(recoveryFilename, originalFilename);
            };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "BMustNotAppend" },
                }));

            Assert.Contains("without following links", error.Message);
            await using var original = File.OpenRead(originalFilename);
            Assert.True(ShareRecorder.ValidateRecoveryJournal(original,
                originalFilename));
            Assert.DoesNotContain("BMustNotAppend",
                await File.ReadAllTextAsync(originalFilename));
        }
        finally
        {
            ShareRecorder.ForgetRecoveryWriteStateForTests(recoveryFilename);
            Directory.Delete(directory, true);
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
    public void RecoveryImportState_RejectsNestedArchivePath()
    {
        AssertInvalidRecoveryArchivePath((directory, recoveryFilename) =>
            Path.Combine(recoveryFilename + ".imported-nested", "archive"));
    }

    [Fact]
    public void RecoveryImportState_RejectsSiblingDirectoryPrefixCollision()
    {
        AssertInvalidRecoveryArchivePath((directory, recoveryFilename) =>
            Path.Combine(directory + "-other",
                Path.GetFileName(recoveryFilename) + ".imported-collision"));
    }

    [Fact]
    public void RecoveryImportState_RejectsIntermediateArchiveSymlinkOnLinux()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-symlink-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var symlink = recoveryFilename + ".imported-link";
        Directory.CreateSymbolicLink(symlink, outside);
        var import = new ShareRecoveryImportState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            import.Begin(new string('A', 64), 1,
                Path.Combine(symlink, "archive"));
            Assert.Throws<InvalidDataException>(import.TryRead);
        }
        finally
        {
            Directory.Delete(symlink);
            Directory.Delete(directory, true);
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public void RecoveryImportState_DetectsMarkerChangedBeforeRetirement()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-marker-change-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var import = new ShareRecoveryImportState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            var original = import.Begin(new string('A', 64), 1,
                recoveryFilename + ".imported-original");
            var content = File.ReadAllText(import.Filename);
            var originalArchive = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                original.ArchiveFilename));
            var changedArchive = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                recoveryFilename + ".imported-changed"));
            File.WriteAllText(import.Filename,
                content.Replace(originalArchive, changedArchive,
                    StringComparison.Ordinal));

            var error = Assert.Throws<InvalidDataException>(() =>
                import.EnsureCurrent(original));
            Assert.Contains("changed during source retirement", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryImportState_AcceptsWindowsCaseInsensitiveSibling()
    {
        if(!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var import = new ShareRecoveryImportState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            import.Begin(new string('A', 64), 1,
                recoveryFilename.ToUpperInvariant() + ".IMPORTED-CASE");
            Assert.NotNull(import.TryRead());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AssertInvalidRecoveryArchivePath(
        Func<string, string, string> archiveFactory)
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-import-containment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recoveryFilename = Path.Combine(directory, "recovered-shares.txt");
        var import = new ShareRecoveryImportState(recoveryFilename,
            Path.Combine(directory, "state"));

        try
        {
            import.Begin(new string('A', 64), 1,
                archiveFactory(directory, recoveryFilename));
            Assert.Throws<InvalidDataException>(import.TryRead);
        }
        finally
        {
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
    public void RecoveryState_NoFollowOpenRejectsSymlinkSubstitutedAfterEnumeration()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-symlink-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "state.marker");
        var target = Path.Combine(directory, "target");
        File.WriteAllText(marker, "original");
        File.WriteAllText(target, "must-not-be-followed");

        IEnumerable<string> ReplaceAfterEnumeration(string inspectedDirectory)
        {
            yield return marker;
            File.Delete(marker);
            File.CreateSymbolicLink(marker, target);
        }

        try
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                RecoveryStateFile.TryOpenExactEntry(marker,
                    ReplaceAfterEnumeration));

            Assert.Contains("symbolic link", error.Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RecoveryState_CumulativeReadLimitBoundsConcurrentMetadataGrowth()
    {
        if(!OperatingSystem.IsLinux())
            return;

        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-state-growth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "state.marker");
        File.WriteAllText(marker, "first=value\r\n");

        try
        {
            using var stream = RecoveryStateFile.TryOpenExactEntry(marker,
                Directory.EnumerateFileSystemEntries);
            var appended = false;

            var error = Assert.Throws<InvalidDataException>(() =>
                RecoveryStateFile.ReadAllLinesStable(stream, marker,
                    Directory.EnumerateFileSystemEntries, null, lineIndex =>
                    {
                        if(lineIndex != 0 || appended)
                            return;

                        appended = true;
                        File.AppendAllText(marker,
                            string.Concat(Enumerable.Repeat("entry=value\r\n", 7_000)));
                    }));

            Assert.True(appended);
            Assert.Contains($"exceeds {RecoveryStateFile.MaximumBytes} bytes while being read",
                error.Message);
        }
        finally
        {
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
    public void MiningFailStopGate_RejectsRecursiveSharePublicationClearly()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus(coordinator);
        using var subscription = bus.Listen<Share>()
            .Subscribe(share => bus.SendMessage(share));

        var error = Assert.Throws<InvalidOperationException>(() =>
            bus.SendMessage(new Share { PoolId = "ltc-solo" }));

        Assert.Contains("Recursive share publication is not supported",
            error.Message);
        Assert.IsNotType<LockRecursionException>(error);
    }

    [Fact]
    public void MiningFailStopGate_DoesNotWaitForDeferredFatalHandling()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        var bus = new MessageBus();
        var releaseHandling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new DeferredAdmissionFailure(releaseHandling.Task);
        using var subscription = bus.Listen<Share>().Subscribe(_ => throw failure);
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var actual = Assert.Throws<DeferredAdmissionFailure>(() =>
            acceptance.PublishShare(bus, new Share()));

        elapsed.Stop();
        Assert.Same(failure, actual);
        Assert.True(failure.HandlerInvoked);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        releaseHandling.TrySetResult();
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
                notification.Message.Contains("exit status 74") &&
                notification.Message.Contains("--verify-share-recovery-state") &&
                notification.Message.Contains("--acknowledge-share-recovery-state") &&
                !notification.Message.Contains("remove only",
                    StringComparison.OrdinalIgnoreCase) &&
                !notification.Message.Contains("before removing",
                    StringComparison.OrdinalIgnoreCase)),
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
                    StringComparison.OrdinalIgnoreCase) &&
                notification.Message.Contains("--verify-share-recovery-state") &&
                notification.Message.Contains("--acknowledge-share-recovery-state") &&
                !notification.Message.Contains("removing that marker",
                    StringComparison.OrdinalIgnoreCase) &&
                !notification.Message.Contains("before removing",
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
    public void ShareRecoveryIncidentVerifier_DetectsDeletionFromIncidentChain()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-chain-delete-{Guid.NewGuid():N}");
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
                    new Share { PoolId = "ltc-solo", Miner = "LIncidentOne" },
                }, new IOException("database one"),
                new IOException("journal one"));
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "bc1incidenttwo" },
                }, new IOException("database two"),
                new IOException("journal two"));
            using(var completeOutput = new StringWriter())
                Assert.True(ShareRecoveryIncidentVerifier.Verify(config,
                    completeOutput).IsSuccessful);

            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var firstIncident = Directory.GetFiles(fatalDirectory, "*.incident")
                .Single(filename => File.ReadAllLines(filename).Contains(
                    "incidentSequence=1"));
            var firstDetail = File.ReadAllLines(firstIncident)
                .Single(line => line.StartsWith("detailFile=",
                    StringComparison.Ordinal))["detailFile=".Length..];
            File.Delete(firstDetail);
            File.Delete(firstIncident);
            using var output = new StringWriter();

            var result = ShareRecoveryIncidentVerifier.Verify(config, output);

            Assert.False(result.IsSuccessful);
            Assert.Contains("missing, duplicate or reordered sequence",
                output.ToString());
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_PreservesEvidenceAllowsRestartAndExtendsChain()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-acknowledge-{Guid.NewGuid():N}");
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
                    new Share { PoolId = "ltc-solo", Miner = "LAcknowledgedOne" },
                }, new IOException("database one"),
                new IOException("journal one"));
            state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "btc-solo", Miner = "bc1acknowledgedtwo" },
                }, new IOException("database two"),
                new IOException("journal two"));
            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var incidentsBefore = Directory.GetFiles(fatalDirectory, "*.incident");
            var sidecarsBefore = Directory.GetFiles(fatalDirectory, "*.shares");
            Assert.Equal(2, incidentsBefore.Length);
            Assert.Equal(2, sidecarsBefore.Length);
            foreach(var operatorEvidence in incidentsBefore.Append(
                        state.FatalStateFilename))
            {
                var text = File.ReadAllText(operatorEvidence);
                Assert.Contains("--verify-share-recovery-state", text);
                Assert.Contains("--acknowledge-share-recovery-state", text);
                Assert.DoesNotContain("before deleting this latch", text,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("before removing", text,
                    StringComparison.OrdinalIgnoreCase);
            }
            using var acknowledgementOutput = new StringWriter();

            Assert.True(state.Acknowledge(acknowledgementOutput));

            Assert.False(File.Exists(state.FatalStateFilename));
            Assert.Equal(incidentsBefore.OrderBy(x => x),
                Directory.GetFiles(fatalDirectory, "*.incident").OrderBy(x => x));
            Assert.Equal(sidecarsBefore.OrderBy(x => x),
                Directory.GetFiles(fatalDirectory, "*.shares").OrderBy(x => x));
            Assert.Single(Directory.GetFiles(fatalDirectory, "*.acknowledged"));
            Assert.Contains("ACKNOWLEDGED", acknowledgementOutput.ToString());
            new ShareRecoveryFatalState(config, new ProcessStatus(),
                    config.ShareRecoveryStateDirectory)
                .EnsureStartupAllowed();

            var firstIncident = incidentsBefore.Single(filename =>
                File.ReadAllLines(filename).Contains("incidentSequence=1"));
            var preservedBytes = File.ReadAllBytes(firstIncident);
            File.Delete(firstIncident);
            var tamperedStatus = new ProcessStatus();
            Assert.Throws<PoolStartupException>(() =>
                new ShareRecoveryFatalState(config, tamperedStatus,
                        config.ShareRecoveryStateDirectory)
                    .EnsureStartupAllowed());
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                tamperedStatus.ExitCode);
            File.WriteAllBytes(firstIncident, preservedBytes);

            var continued = new ShareRecoveryFatalState(config,
                new ProcessStatus(), config.ShareRecoveryStateDirectory);
            continued.MarkFatalShares(new[]
                {
                    new Share { PoolId = "doge-solo", Miner = "DLaterIncident" },
                }, new IOException("database three"),
                new IOException("journal three"));
            Assert.True(File.Exists(continued.FatalStateFilename));
            Assert.Equal(3, Directory.GetFiles(fatalDirectory, "*.incident").Length);
            Assert.True(ShareRecoveryIncidentVerifier.Verify(config,
                TextWriter.Null).IsSuccessful);
            Assert.True(continued.Acknowledge(TextWriter.Null));
            Assert.False(File.Exists(continued.FatalStateFilename));
            Assert.Equal(2,
                Directory.GetFiles(fatalDirectory, "*.acknowledged").Length);
            new ShareRecoveryFatalState(config, new ProcessStatus(),
                    config.ShareRecoveryStateDirectory)
                .EnsureStartupAllowed();
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_ManualLatchDeletionDoesNotAcknowledgeIncidentChain()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-manual-latch-delete-{Guid.NewGuid():N}");
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
                    new Share { PoolId = "ltc-solo", Miner = "LManualDelete" },
                }, new IOException("database"), new IOException("journal"));
            File.Delete(state.FatalStateFilename);
            var restartedStatus = new ProcessStatus();

            var error = Assert.Throws<PoolStartupException>(() =>
                new ShareRecoveryFatalState(config, restartedStatus,
                        config.ShareRecoveryStateDirectory)
                    .EnsureStartupAllowed());

            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                restartedStatus.ExitCode);
            Assert.Contains("acknowledged", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_MissingAcknowledgedSidecarBlocksStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-ack-sidecar-{Guid.NewGuid():N}");
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
                    new Share { PoolId = "ltc-solo", Miner = "LAuditSidecar" },
                }, new IOException("database"), new IOException("journal"));
            var sidecar = Directory.GetFiles(
                Path.GetDirectoryName(state.FatalStateFilename)!, "*.shares")
                .Single();
            Assert.True(state.Acknowledge(TextWriter.Null));
            File.Delete(sidecar);
            var restartedStatus = new ProcessStatus();

            var error = Assert.Throws<PoolStartupException>(() =>
                new ShareRecoveryFatalState(config, restartedStatus,
                        config.ShareRecoveryStateDirectory)
                    .EnsureStartupAllowed());

            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                restartedStatus.ExitCode);
            Assert.Contains("incomplete or corrupt", error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_LegacyV2EvidenceCanBeAcknowledgedAndExtended()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-ack-v2-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);
        var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
        Directory.CreateDirectory(fatalDirectory);
        const string incidentId = "legacy-v2-incident";
        var incidentFilename = Path.Combine(fatalDirectory,
            $"{Path.GetFileNameWithoutExtension(state.FatalStateFilename)}.{incidentId}.incident");
        var legacyContent = string.Join('\n',
            "Miningcore share-accounting durability failure",
            "formatVersion=2",
            $"incidentId={incidentId}",
            $"createdUtc={DateTimeOffset.UtcNow:O}",
            "failureCategory=database-and-journal-unavailable",
            $"recoveryFile={Path.GetFullPath(config.ShareRecoveryFile)}",
            $"recoveryPathSha256={ShareRecoveryFatalState.ComputeRecoveryPathHash(Path.GetFullPath(config.ShareRecoveryFile))}",
            "shareCount=1",
            "pools=ltc-solo",
            "detailFile=(none)",
            "detailSha256=(none)",
            "detailState=not-required",
            "databaseError=System.IO.IOException: database",
            "journalError=System.IO.IOException: journal",
            "Reconcile every immutable incident record and referenced sidecar before deleting this latch and restarting Miningcore.",
            string.Empty);
        File.WriteAllText(incidentFilename, legacyContent,
            new UTF8Encoding(false));
        File.WriteAllText(state.FatalStateFilename, legacyContent,
            new UTF8Encoding(false));

        try
        {
            Assert.True(ShareRecoveryIncidentVerifier.Verify(config,
                TextWriter.Null).IsSuccessful);
            Assert.True(state.Acknowledge(TextWriter.Null));

            Assert.False(File.Exists(state.FatalStateFilename));
            Assert.True(File.Exists(incidentFilename));
            var legacyAcknowledgement = Directory.GetFiles(fatalDirectory,
                "*.acknowledged").Single();
            var acknowledgementText = File.ReadAllText(legacyAcknowledgement);
            Assert.Contains("formatVersion=4", acknowledgementText);
            Assert.Contains("acknowledgementKind=legacy-v2-set",
                acknowledgementText);
            Assert.Contains("--acknowledge-share-recovery-state",
                acknowledgementText);
            Assert.DoesNotContain("before deleting this latch",
                acknowledgementText, StringComparison.OrdinalIgnoreCase);
            state.EnsureStartupAllowed();

            var continued = new ShareRecoveryFatalState(config,
                new ProcessStatus(), config.ShareRecoveryStateDirectory);
            continued.MarkFatal(1, new[] { "doge-solo" },
                new IOException("later database"),
                new IOException("later journal"));
            Assert.True(continued.Acknowledge(TextWriter.Null));
            Assert.True(File.Exists(incidentFilename));
            Assert.Equal(2, Directory.GetFiles(fatalDirectory,
                "*.acknowledged").Length);
            continued.EnsureStartupAllowed();
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_RefusesWhenMutationLockIsOwned()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-mutation-lock-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);

        try
        {
            state.MarkFatal(1, new[] { "ltc-solo" },
                new IOException("database"), new IOException("journal"));
            using(var owned = new FileStream(state.MutationLockFilename,
                      FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var acknowledgementError = Assert.Throws<InvalidOperationException>(() =>
                    new ShareRecoveryFatalState(config, new ProcessStatus(),
                            config.ShareRecoveryStateDirectory)
                        .Acknowledge(TextWriter.Null));
                Assert.Contains("still owns", acknowledgementError.Message,
                    StringComparison.OrdinalIgnoreCase);

                var publicationError = Assert.Throws<InvalidOperationException>(() =>
                    new ShareRecoveryFatalState(config, new ProcessStatus(),
                            config.ShareRecoveryStateDirectory)
                        .MarkFatal(1, new[] { "doge-solo" },
                            new IOException("later database"),
                            new IOException("later journal")));
                Assert.Contains("still owns", publicationError.Message,
                    StringComparison.OrdinalIgnoreCase);

                var startupStatus = new ProcessStatus();
                var startupError = Assert.Throws<PoolStartupException>(() =>
                    new ShareRecoveryFatalState(config, startupStatus,
                            config.ShareRecoveryStateDirectory)
                        .EnsureStartupAllowed());
                Assert.Contains("still owns", startupError.Message,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                    startupStatus.ExitCode);
            }

            Assert.True(state.Acknowledge(TextWriter.Null));
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryAcknowledgement_ResumesAfterAnchorPublication()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-ack-resume-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);
        state.MarkFatalShares(new[]
            {
                new Share { PoolId = "ltc-solo", Miner = "LResumeAck" },
            }, new IOException("database"), new IOException("journal"));
        state.AcknowledgementPublishedCheckpoint = () =>
            throw new IOException("injected after acknowledgement publication");

        try
        {
            Assert.Throws<IOException>(() => state.Acknowledge(TextWriter.Null));
            Assert.True(File.Exists(state.FatalStateFilename));
            Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(state.FatalStateFilename)!,
                "*.acknowledged"));

            state.AcknowledgementPublishedCheckpoint = () => { };
            Assert.True(state.Acknowledge(TextWriter.Null));
            Assert.False(File.Exists(state.FatalStateFilename));
            state.EnsureStartupAllowed();
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
    public void ShareRecoveryFatalState_ResumesCompletedIncidentLatchPublication()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-completion-resume-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory);
        state.CompletedIncidentPublishedCheckpoint = () =>
            throw new IOException("injected after completed incident publication");

        try
        {
            Assert.Throws<IOException>(() => state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LResumeCompletion" },
                }, new IOException("database"), new IOException("journal")));

            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var stem = Path.GetFileNameWithoutExtension(state.FatalStateFilename);
            var incidentFilename = Assert.Single(Directory.GetFiles(
                fatalDirectory, $"{stem}.*.incident"));
            var incident = File.ReadAllLines(incidentFilename);
            Assert.Contains("detailState=complete", incident);
            var detailFilename = Assert.Single(incident.Where(line =>
                line.StartsWith("detailFile=", StringComparison.Ordinal)))[
                "detailFile=".Length..];
            var incidentHash = SHA256.HashData(File.ReadAllBytes(incidentFilename));
            var sidecarHash = SHA256.HashData(File.ReadAllBytes(detailFilename));

            using(var output = new StringWriter())
            {
                var interrupted = ShareRecoveryIncidentVerifier.Verify(config,
                    output);
                Assert.False(interrupted.IsSuccessful);
                Assert.Equal(0, interrupted.InvalidCount);
                Assert.Equal(1, interrupted.IncompleteCount);
                Assert.Contains("RECOVERABLE: completed incident evidence",
                    output.ToString());
            }

            var restarted = new ShareRecoveryFatalState(config,
                new ProcessStatus(), config.ShareRecoveryStateDirectory);
            var startupError = Assert.Throws<PoolStartupException>(
                restarted.EnsureStartupAllowed);
            Assert.Contains("Share-accounting durability remains unreconciled",
                startupError.Message);

            using var resumedOutput = new StringWriter();
            var resumed = ShareRecoveryIncidentVerifier.Verify(config,
                resumedOutput);
            Assert.True(resumed.IsSuccessful, resumedOutput.ToString());
            Assert.Equal(incidentHash,
                SHA256.HashData(File.ReadAllBytes(incidentFilename)));
            Assert.Equal(sidecarHash,
                SHA256.HashData(File.ReadAllBytes(detailFilename)));
            Assert.True(restarted.Acknowledge(TextWriter.Null));
            Assert.False(File.Exists(restarted.FatalStateFilename));
            Assert.True(File.Exists(incidentFilename));
            Assert.True(File.Exists(detailFilename));
        }
        finally
        {
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ShareRecoveryFatalState_RejectsMismatchedCompletedIncidentTransition()
    {
        var directory = Path.Combine(Path.GetTempPath(),
            $"miningcore-recovery-completion-mismatch-{Guid.NewGuid():N}");
        var config = new ClusterConfig
        {
            ShareRecoveryFile = Path.Combine(directory, "recovered-shares.txt"),
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        var state = new ShareRecoveryFatalState(config, new ProcessStatus(),
            config.ShareRecoveryStateDirectory)
        {
            CompletedIncidentPublishedCheckpoint = () =>
                throw new IOException("injected completion boundary"),
        };

        try
        {
            Assert.Throws<IOException>(() => state.MarkFatalShares(new[]
                {
                    new Share { PoolId = "ltc-solo", Miner = "LMismatch" },
                }, new IOException("database"), new IOException("journal")));
            var fatalDirectory = Path.GetDirectoryName(state.FatalStateFilename)!;
            var stem = Path.GetFileNameWithoutExtension(state.FatalStateFilename);
            var incidentFilename = Assert.Single(Directory.GetFiles(
                fatalDirectory, $"{stem}.*.incident"));
            var content = File.ReadAllText(incidentFilename);
            File.WriteAllText(incidentFilename, content.Replace(
                "failureCategory=database-and-journal-unavailable",
                "failureCategory=tampered", StringComparison.Ordinal));

            using var output = new StringWriter();
            var verification = ShareRecoveryIncidentVerifier.Verify(config,
                output);
            Assert.False(verification.IsSuccessful);
            Assert.True(verification.InvalidCount > 0);
            Assert.Contains("failureCategory", output.ToString());

            var restarted = new ShareRecoveryFatalState(config,
                new ProcessStatus(), config.ShareRecoveryStateDirectory);
            Assert.Throws<InvalidDataException>(() =>
                restarted.Acknowledge(TextWriter.Null));
            Assert.True(File.Exists(restarted.FatalStateFilename));
            Assert.True(File.Exists(incidentFilename));
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
            Assert.Contains("do not acknowledge or delete the fatal latch",
                output.ToString());
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

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkfifo(string pathname, uint mode);

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

    private static List<Task> GetDeferredFailStopTasks(ShareRecorder recorder) =>
        (List<Task>) typeof(ShareRecorder)
            .GetField("deferredFailStopHandling",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(recorder)!;

    private static ShareRecorder CreateRecorderWithOwnership(ClusterConfig config,
        IShareRecoveryPathOwnership ownership) =>
        new(Substitute.For<IConnectionFactory>(),
            AutoMapperFactory.CreateMapper(), new JsonSerializerSettings(),
            Substitute.For<IShareRepository>(), Substitute.For<IBlockRepository>(),
            config, new MessageBus(), Substitute.For<IShareRecoveryFailureHandler>(),
            Substitute.For<IMiningFailStopCoordinator>(), ownership);

    private static RecoveryFixture CreateRecoveryFixture(IMessageBus messageBusOverride = null)
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = messageBusOverride ?? Substitute.For<IMessageBus>();
        var mapper = AutoMapperFactory.CreateMapper();
        var dogecoinPool = new PoolConfig
        {
            Id = "doge-solo",
            Template = new BitcoinTemplate { Symbol = "DOGE", Name = "Dogecoin" },
        };
        var litecoinPool = new PoolConfig
        {
            Id = "ltc-solo",
            Template = new BitcoinTemplate { Symbol = "LTC", Name = "Litecoin" },
        };

        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(connection));
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        shareRepository.TryRegisterRecoveryImportAsync(connection, transaction,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        shareRepository.HasMatchingRecoveryImportAsync(connection,
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var recorder = new ShareRecorder(connectionFactory, mapper,
            new JsonSerializerSettings(), shareRepository, blockRepository,
            new ClusterConfig { Pools = new[] { dogecoinPool, litecoinPool } },
            messageBus);

        return new RecoveryFixture(recorder, connectionFactory, connection,
            transaction, shareRepository, blockRepository, messageBus);
    }

    private static RecoveryFixture CreateConfiguredRecoveryFixture(ClusterConfig config)
    {
        if(config.Pools?.Length == 0)
        {
            // Recovery mechanics in these fixtures use ltc-solo journal records. Production
            // recovery now treats configured pool IDs as an explicit import allowlist.
            config.Pools = new[] { new PoolConfig { Id = "ltc-solo" } };
        }

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
        shareRepository.HasMatchingRecoveryImportAsync(connection,
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

    private static async Task WaitUntilAsync(Func<bool> condition,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while(!condition())
            await Task.Delay(10, deadline.Token);
    }

    private static Task StopRecorderBeforeFixtureCleanupAsync(
        ShareRecorder recorder) =>
        AwaitRecorderStopBeforeFixtureCleanupAsync(
            recorder.StopAsync(CancellationToken.None));

    private static async Task AwaitRecorderStopBeforeFixtureCleanupAsync(
        Task stopping)
    {
        try
        {
            await stopping.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch(Exception ex) when(stopping.IsCompleted && ex is not TimeoutException)
        {
            // These fixtures deliberately fault persistence and recovery targets.
            // Swallow only a terminal non-timeout service failure. A StopAsync
            // TimeoutException means its underlying worker may still be running even
            // though the stop task itself is complete. The outer 30-second timeout has
            // the same unsafe-cleanup meaning, so either timeout must escape before a
            // caller can delete a directory beneath a live recovery owner or worker.
        }
    }

    private static bool FatalStateIsComplete(string filename)
    {
        try
        {
            return File.ReadLines(filename).Any(line =>
                string.Equals(line, "detailState=complete",
                    StringComparison.Ordinal));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // The fail-closed latch is atomically advanced from hash-pending to complete.
            // Retry while either publication is in flight.
            return false;
        }
    }

    private sealed record RecoveryFixture(ShareRecorder Recorder,
        IConnectionFactory ConnectionFactory, IDbConnection Connection,
        IDbTransaction Transaction, IShareRepository ShareRepository,
        IBlockRepository BlockRepository, IMessageBus MessageBus);

    private sealed class DeferredAdmissionFailure : IOException,
        IMiningAdmissionFailure
    {
        public DeferredAdmissionFailure(Task handling) :
            base("deferred admission failure")
        {
            this.handling = handling;
        }

        private readonly Task handling;
        public bool HandlerInvoked { get; private set; }

        public Task HandleAfterAdmissionReleasedAsync()
        {
            HandlerInvoked = true;
            return handling;
        }
    }

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
