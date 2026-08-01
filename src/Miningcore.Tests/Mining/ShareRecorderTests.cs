using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
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
    public async Task RecoverSharesAsync_SuccessfulOrdinaryReplay_IsRejectedByManifest()
    {
        var fixture = CreateRecoveryFixture();
        var filename = await WriteRecoveryFileAsync(new[] { RecoveryShareJson(1) });
        fixture.ShareRepository.TryRegisterRecoveryImportAsync(fixture.Connection,
                fixture.Transaction, Arg.Any<string>(), Arg.Any<string>(), 1,
                Arg.Any<CancellationToken>())
            .Returns(true, false);
        string archiveFilename = null;

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);
            File.Copy(archiveFilename, filename);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            await fixture.ShareRepository.Received(1).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_SemanticallyEquivalentReplay_IsRejectedByManifest()
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

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            Assert.Single(registeredHashes);
            await fixture.ShareRepository.Received(1).BatchInsertAsync(
                fixture.Connection, fixture.Transaction,
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
        }
    }

    [Fact]
    public async Task RecoverSharesAsync_BlockOnlyReplay_IsRejectedByManifest()
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

        try
        {
            archiveFilename = await fixture.Recorder.RecoverSharesAsync(filename);
            File.Copy(archiveFilename, filename);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Recorder.RecoverSharesAsync(filename));

            await fixture.ShareRepository.DidNotReceive().BatchInsertAsync(
                Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(),
                Arg.Any<IEnumerable<PersistedShare>>(), Arg.Any<CancellationToken>());
            await fixture.BlockRepository.Received(1).InsertAsync(
                fixture.Connection, fixture.Transaction, Arg.Any<Block>());
            fixture.MessageBus.Received(1).SendMessage(
                Arg.Any<BlockFoundNotification>(), Arg.Any<string>());
        }

        finally
        {
            File.Delete(filename);
            if(archiveFilename != null)
                File.Delete(archiveFilename);
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
                "# miningcore-recovery-batch-v1 end ", StringComparison.Ordinal);
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
                "# miningcore-recovery-batch-v1 start ", StringComparison.Ordinal);
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
                "# miningcore-recovery-batch-v1 start ", StringComparison.Ordinal));
            var firstEnd = lines.FindIndex(firstStart + 1, line => line.StartsWith(
                "# miningcore-recovery-batch-v1 end ", StringComparison.Ordinal));
            Assert.True(firstStart >= 0 && firstEnd > firstStart + 1);
            lines.RemoveAt(firstStart + 1);
            await File.WriteAllLinesAsync(recoveryFilename, lines,
                new UTF8Encoding(false));

            var appendError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.WriteRecoveryJournalAsync(new[]
                {
                    new Share { PoolId = "doge-solo", Miner = "DThird" },
                }));
            Assert.Contains("record count or hash", appendError.Message);

            var processStatus = new ProcessStatus();
            var fatalState = new ShareRecoveryFatalState(config, processStatus,
                Path.Combine(directory, "state"));
            var startupError = Assert.Throws<PoolStartupException>(() =>
                fatalState.EnsureStartupAllowed());
            Assert.Contains("record count or hash", startupError.Message);
            Assert.Equal(ProcessExitCodes.UnreconciledShareDurabilityLoss,
                processStatus.ExitCode);

            var importFixture = CreateRecoveryFixture();
            var importError = await Assert.ThrowsAsync<InvalidDataException>(() =>
                importFixture.Recorder.RecoverSharesAsync(recoveryFilename));
            Assert.Contains("record count or hash", importError.Message);
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
            Assert.False(ShareRecorder.ValidateRecoveryJournal(stream,
                recoveryFilename));
            var lines = await File.ReadAllLinesAsync(recoveryFilename);
            Assert.Equal(3, lines.Count(line =>
                !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#')));
            Assert.Equal(2, lines.Count(line => line.StartsWith(
                "# miningcore-recovery-batch-v1 end ", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(recoveryFilename);
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
    public async Task MiningAcceptance_FailStopImmediatelyBeforePublicationDoesNotAcknowledge()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var published = false;
        var acknowledged = false;

        coordinator.BeginFailStop(ProcessExitCodes.UnreconciledShareDurabilityLoss);

        Assert.Throws<OperationCanceledException>(() =>
            acceptance.PublishShare(() => published = true));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            acceptance.QueueResponseAsync(() =>
            {
                acknowledged = true;
                return Task.CompletedTask;
            }));
        Assert.False(published);
        Assert.False(acknowledged);
    }

    [Fact]
    public async Task MiningAcceptance_FailStopBetweenPublicationAndResponseDoesNotAcknowledge()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        var published = false;
        var acknowledged = false;

        acceptance.PublishShare(() => published = true);
        coordinator.BeginFailStop(ProcessExitCodes.UnreconciledShareDurabilityLoss);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            acceptance.QueueResponseAsync(() =>
            {
                acknowledged = true;
                return Task.CompletedTask;
            }));
        Assert.True(published);
        Assert.False(acknowledged);
    }

    [Fact]
    public async Task MiningAcceptance_ResponseQueueIsAtomicWithFailStopTransition()
    {
        using var coordinator = new MiningFailStopCoordinator(new ProcessStatus(),
            Substitute.For<IHostApplicationLifetime>());
        using var acceptance = coordinator.AcquireSubmissionAcceptance();
        using var queueEntered = new ManualResetEventSlim();
        using var releaseQueue = new ManualResetEventSlim();
        var published = false;
        var responseQueued = false;

        acceptance.PublishShare(() => published = true);
        var queueTask = Task.Run(async () =>
            await acceptance.QueueResponseAsync(() =>
            {
                queueEntered.Set();
                releaseQueue.Wait();
                responseQueued = true;
                return Task.CompletedTask;
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
        var missingDirectory = Path.Combine(Path.GetTempPath(),
            Path.GetRandomFileName());
        var recoveryFilename = Path.Combine(missingDirectory,
            "recovered-shares.txt");
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
            }, messageBus, recoveryFailureHandler);

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
            config, Substitute.For<IMessageBus>(), recoveryFailureHandler)
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
                Notifications = new NotificationsConfig
                {
                    Admin = new AdminNotifications
                    {
                        Enabled = true,
                        NotifyPaymentSuccess = true,
                    },
                },
            }, messageBus, recoveryFailureHandler);

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

        await Assert.ThrowsAsync<DataException>(() =>
            recorder.PersistSharesCoreAsync(new List<Share> { candidate }));

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
        var missingDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
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
            }, Substitute.For<IMessageBus>(), failureHandler);
        var candidate = CreateDurableCandidate("lost-candidate-block");

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

    [Fact]
    public async Task CandidatePersistenceFailureHandler_LaterDualTargetFailureUpgradesStatusAndLatches()
    {
        var processStatus = new ProcessStatus();
        var applicationLifetime = Substitute.For<IHostApplicationLifetime>();
        var notificationSender = Substitute.For<ICriticalNotificationSender>();
        var fatalState = Substitute.For<IShareRecoveryFatalState>();
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
        fatalState.Received(1).MarkFatal(1,
            Arg.Is<IReadOnlyCollection<string>>(pools =>
                pools.SequenceEqual(new[] { "doge-solo" })),
            Arg.Is<InvalidOperationException>(ex => ex.Message == "aux failed"),
            Arg.Is<IOException>(ex => ex.Message == "journal failed"));
        await notificationSender.Received(1).SendCriticalAdminNotificationAsync(
            Arg.Any<AdminNotification>(), Arg.Any<CancellationToken>());
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
        fatalState.Received(1).MarkFatal(1,
            Arg.Is<IReadOnlyCollection<string>>(pools =>
                pools.SequenceEqual(new[] { "doge-solo" })),
            Arg.Any<IOException>(), Arg.Any<IOException>());
    }

    [Fact]
    public void ShareRecorder_PublicConstructionRequiresRecoveryFailureHandler()
    {
        var constructor = Assert.Single(typeof(ShareRecorder).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters(), x =>
            x.ParameterType == typeof(IShareRecoveryFailureHandler));

        Assert.False(parameter.IsOptional);
        Assert.False(parameter.HasDefaultValue);
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
        fatalState.Received(2).MarkFatal(2,
            Arg.Is<IReadOnlyCollection<string>>(pools =>
                pools.SequenceEqual(new[] { "btc-solo", "ltc-solo" })),
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
