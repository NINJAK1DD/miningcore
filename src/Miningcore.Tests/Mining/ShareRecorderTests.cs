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
        var recorder = CreateRecoveryRecorder();
        var filename = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            recorder.RecoverSharesAsync(filename));
    }

    [Fact]
    public async Task RecoverSharesAsync_InvalidRecordsThrow()
    {
        var recorder = CreateRecoveryRecorder();
        var filename = Path.GetTempFileName();

        try
        {
            await File.WriteAllTextAsync(filename, "this is not a share record");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                recorder.RecoverSharesAsync(filename));
        }

        finally
        {
            File.Delete(filename);
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

    private static ShareRecorder CreateRecoveryRecorder()
    {
        var mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile()))
            .CreateMapper();

        return new ShareRecorder(Substitute.For<IConnectionFactory>(), mapper,
            new JsonSerializerSettings(), Substitute.For<IShareRepository>(),
            Substitute.For<IBlockRepository>(),
            new ClusterConfig { Pools = new PoolConfig[0] },
            Substitute.For<IMessageBus>());
    }
}
