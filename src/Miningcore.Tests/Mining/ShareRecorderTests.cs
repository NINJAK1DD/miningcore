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
    public void BlockOnly_RoundTripsThroughShareRelayWireFormat()
    {
        var share = new Share
        {
            PoolId = "doge-solo",
            Miner = "DExampleAddress",
            IsBlockCandidate = true,
            BlockOnly = true,
        };

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, share);
        stream.Position = 0;

        var result = Serializer.Deserialize<Share>(stream);

        Assert.True(result.BlockOnly);
        Assert.True(result.IsBlockCandidate);
        Assert.Equal(share.PoolId, result.PoolId);
        Assert.Equal(share.Miner, result.Miner);
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
        };

        await recorder.PersistSharesCoreAsync(new List<Share> { candidate });

        await shareRepository.DidNotReceive().BatchInsertAsync(
            Arg.Any<IDbConnection>(), Arg.Any<IDbTransaction>(), Arg.Any<IEnumerable<PersistedShare>>(),
            Arg.Any<CancellationToken>());
        await blockRepository.Received(1).InsertAsync(connection, transaction,
            Arg.Is<Block>(x => x.PoolId == candidate.PoolId &&
                x.BlockHeight == (ulong) candidate.BlockHeight &&
                x.Hash == candidate.BlockHash));
        transaction.Received(1).Commit();
    }
}
