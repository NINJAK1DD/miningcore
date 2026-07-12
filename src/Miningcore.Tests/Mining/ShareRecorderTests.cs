using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json;
using NSubstitute;
using ProtoBuf;
using Xunit;
using Block = Miningcore.Persistence.Model.Block;
using PersistedShare = Miningcore.Persistence.Model.Share;

namespace Miningcore.Tests.Mining;

public class ShareRecorderTests
{
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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();

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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();

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
    public async Task PersistSharesCoreAsync_DuplicateBlockDoesNotNotifyAgain()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();
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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();
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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();
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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();
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
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();
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

    private static RecoveryFixture CreateRecoveryFixture()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var messageBus = Substitute.For<IMessageBus>();
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile()))
            .CreateMapper();
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
}
