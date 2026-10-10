using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Zano;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Native;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Mining;

public class CryptonoteDifficultyCreditTests : TestBase
{
    private sealed class LinuxNativeTheoryAttribute : TheoryAttribute
    {
        public LinuxNativeTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux()) Skip = "Actual CryptoNight proof requires the Linux native lab";
        }
    }

    [LinuxNativeTheory]
    [InlineData("Conceal", 0.5)]
    [InlineData("Conceal", 0.1)]
    [InlineData("Conceal", 0.00390625)]
    [InlineData("Cryptonote", 0.5)]
    [InlineData("Cryptonote", 0.1)]
    [InlineData("Cryptonote", 0.00390625)]
    public async Task NativeProof_AfterRejectedSubUnitAssignmentRetainsRepresentableCredit(string family, double invalid)
    {
        WorkerContextBase worker = family == "Conceal" ? new ConcealWorkerContext() : new CryptonoteWorkerContext();
        worker.Init(1, null, new MockMasterClock());
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.SetDifficulty(invalid));
        var connection = new StratumConnection(new NullLogger(LogManager.LogFactory), new RecyclableMemoryStreamManager(),
            new MockMasterClock(), "proof-credit", false);
        connection.SetContext(worker);
        var blob = NativeBlob.HexToByteArray();
        blob.AsSpan(ConcealConstants.BlobNonceOffset, 4).Clear();
        var offset = NativeBlob.IndexOf("020800000000012e7f76", StringComparison.Ordinal) / 2 + 2;
        Share accepted;
        string target, miningBlob;
        var hash = new byte[32];
        if(family == "Conceal")
        {
            var job = new ConcealJob(new Miningcore.Blockchain.Conceal.DaemonResponses.GetBlockTemplateResponse
                { Blob = blob.ToHexString(), Height = 101, Difficulty = 1_000_000_000, ReservedOffset = offset },
                new byte[3], "job", new ConcealCoinTemplate { Hash = CryptonightHashType.CryptonightCCX },
                new PoolConfig(), new ClusterConfig(), "previous");
            var assignment = new ConcealWorkerJob("job", worker.Difficulty);
            job.PrepareWorkerJob(assignment, out miningBlob, out target);
            Cryptonight.CryptonightHash(miningBlob.HexToByteArray(), hash, Cryptonight.Algorithm.CN_CCX, 101);
            accepted = job.ProcessShare("00000000", assignment.ExtraNonce, hash.ToHexString(), connection).Share;
            Assert.Equal(1, assignment.Difficulty);
        }
        else
        {
            var job = new CryptonoteJob(new Miningcore.Blockchain.Cryptonote.DaemonResponses.GetBlockTemplateResponse
                { Blob = blob.ToHexString(), Height = 101, Difficulty = 1_000_000_000, ReservedOffset = offset },
                new byte[3], "job", new CryptonoteCoinTemplate { Hash = CryptonightHashType.Cryptonight0 },
                new PoolConfig(), new ClusterConfig(), "previous", "credit-fixture");
            var assignment = new CryptonoteWorkerJob("job", worker.Difficulty);
            job.PrepareWorkerJob(assignment, out miningBlob, out target);
            Cryptonight.CryptonightHash(miningBlob.HexToByteArray(), hash, Cryptonight.Algorithm.CN_0, 101);
            accepted = job.ProcessShare("00000000", assignment.ExtraNonce, hash.ToHexString(), connection).Share;
            Assert.Equal(1, assignment.Difficulty);
        }
        Assert.Equal("ffffffff", target);
        Assert.NotEmpty(miningBlob);
        Assert.Equal(1, worker.Difficulty);
        Assert.Equal(1, accepted.Difficulty);
        await AssertRewardWeights(accepted, 1);
    }

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(0.1, 2)]
    [InlineData(0.00390625, 2)]
    public async Task Zano_AssignedTargetUnitRemainsDistinctFromPersistedMultiplierCredit(double invalid, double multiplier)
    {
        var worker = new ZanoWorkerContext();
        worker.Init(1, null, new MockMasterClock());
        Assert.Throws<ArgumentOutOfRangeException>(() => worker.SetDifficulty(invalid));
        var credit = CryptonoteDifficulty.NormalizeShareCredit(worker.Difficulty, multiplier);
        Assert.Equal(1 / multiplier, credit);
        Assert.Throws<ArgumentOutOfRangeException>(() => CryptonoteDifficulty.NormalizeShareCredit(invalid, multiplier));
        await AssertRewardWeights(new Share { Difficulty = credit }, 1 / multiplier);
    }

    private static async Task AssertRewardWeights(Share accepted, double expectedCredit)
    {
        accepted.Miner = "accepted";
        accepted.PoolId = "credit-pool";
        accepted.NetworkDifficulty = 1000;
        accepted.Created = DateTime.UnixEpoch.AddSeconds(10);
        var peer = new Share { Miner = "peer", PoolId = accepted.PoolId, Difficulty = expectedCredit,
            NetworkDifficulty = 1000, Created = DateTime.UnixEpoch.AddSeconds(9) };
        foreach(var type in new[] { typeof(PROPPaymentScheme), typeof(PPLNSPaymentScheme) })
        {
            var factory = Substitute.For<IConnectionFactory>();
            factory.OpenConnectionAsync().Returns(Substitute.For<IDbConnection>());
            var repo = Substitute.For<IShareRepository>();
            var mapper = AutoMapperFactory.CreateMapper();
            var persisted = new[] { mapper.Map<Miningcore.Persistence.Model.Share>(accepted), mapper.Map<Miningcore.Persistence.Model.Share>(peer) };
            repo.ReadSharesBeforeAsync(Arg.Any<IDbConnection>(), Arg.Any<string>(), Arg.Any<DateTime>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(persisted);
            var scheme = Activator.CreateInstance(type, factory, repo, Substitute.For<IBlockRepository>(), Substitute.For<IBalanceRepository>());
            var pool = Substitute.For<IMiningPool>();
            pool.Config.Returns(new PoolConfig { Id = accepted.PoolId });
            var handler = Substitute.For<IPayoutHandler>();
            handler.AdjustShareDifficulty(Arg.Any<double>()).Returns(c => c.Arg<double>());
            var shares = new Dictionary<string, double>();
            var rewards = new Dictionary<string, decimal>();
            var block = new Miningcore.Persistence.Model.Block { Created = DateTime.UnixEpoch.AddSeconds(20) };
            var method = type.GetMethod("CalculateRewardsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var args = type == typeof(PROPPaymentScheme)
                ? new object[] { pool, handler, block, 10m, shares, rewards, CancellationToken.None }
                : new object[] { pool, handler, (decimal) (2 * expectedCredit / 1000), block, 10m, shares, rewards, CancellationToken.None };
            await ((Task) method.Invoke(scheme, args)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(expectedCredit, shares["accepted"]);
            Assert.Equal(5m, rewards["accepted"]);
            Assert.Equal(5m, rewards["peer"]);
        }
    }

    // Same reviewed native-conversion KAT as PoolFamilyAssignmentTests.
    private const string NativeBlob = "0106e5b3afd505583cf50bcc743d04d831d2b119dc94ad88679e359076ee3f18d258ee138b3b421c0300a401d90101ff9d0106d6d6a88702023c62e43372a58cb588147e20be53a27083f5c522f33c722b082ab7518c48cda280b4c4c32102609ec96e2499ee267d70efefc49f26e330526d3ef455314b7b5ba268a6045f8c80c0fc82aa0202fe5cc0fa56c4277d1a47827edce4725571529d57f33c73ada481ef84c323f30a8090cad2c60e02d88bf5e72a611c8b8464ce29e3b1adbfe1ae163886d9150fe511171cada98fcb80e08d84ddcb0102441915aaf9fbaf70ff454c701a6ae2bd59bb94dc0b888bf7e5d06274ee9238ca80c0caf384a302024078526e2132def44bde2806242652f5944e632f7d94290dd6ee5dda1929f5ee2b016e29f25f07ec2a8df59f0e118a6c9a4b769b745dc0c729071f6e0399d2585745020800000000012e7f7600";
}
