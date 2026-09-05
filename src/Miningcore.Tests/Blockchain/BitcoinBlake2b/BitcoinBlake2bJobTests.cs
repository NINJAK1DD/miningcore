using System;
using System.Buffers.Binary;
using System.IO;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.BitcoinBlake2b;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bJobTests : TestBase
{
    [Theory]
    [InlineData(1e-9)]
    [InlineData(1)]
    [InlineData(1000)]
    public void AssignedDifficulty_BindsCreditedDifficultyToProofAndWireTarget(double difficulty)
    {
        var assignment = BitcoinBlake2bJobManager.ValidateDifficultyForNetwork(difficulty, Network.RegTest);
        Assert.Equal(difficulty, assignment.Difficulty);
        Assert.Equal(BitcoinBlake2bHeader.TargetForDifficulty(difficulty), assignment.Target);
        Assert.Equal(BitcoinBlake2bHeader.EncodeCompactTarget(assignment.Target), assignment.Bits);
        var (job, _) = CreateJob();
        var issued = job.ForDifficulty(assignment);
        Assert.Equal(job.ForDifficulty(difficulty).JobId, issued.JobId);
        Assert.Equal(assignment.Bits.ToString("x8"), ((object[]) issued.GetJobParams(false))[6]);
    }

    [Fact]
    public void AssignedDifficulty_CannotAcceptAnIndependentTargetOrInvalidDefault()
    {
        var type = typeof(BitcoinBlake2bDifficulty);
        Assert.True(type.IsSealed);
        Assert.All(type.GetConstructors(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.All(type.GetProperties(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            property => Assert.Null(property.SetMethod));
        Assert.Throws<ArgumentOutOfRangeException>(() => BitcoinBlake2bDifficulty.Create(0));
        var (job, _) = CreateJob();
        Assert.Throws<ArgumentNullException>(() => job.ForDifficulty(null));
    }

    [Fact]
    public void IssuedDifficulty_RemainsImmutableAcrossVarDiffChanges()
    {
        var (job, worker) = CreateJob("1d00ffff");
        var issued = job.ForDifficulty(1e-9);
        worker.ContextAs<BitcoinWorkerContext>().SetDifficulty(1000);
        var share = FindShare(issued, worker);
        Assert.Equal(1e-9, share.Difficulty);
        Assert.False(share.IsBlockCandidate);
        Assert.NotEqual(issued.JobId, job.ForDifficulty(1000).JobId);
        Assert.Equal(issued.JobId, job.ForDifficulty(1e-9).JobId);
    }

    [Fact]
    public void NetworkCandidate_IsNotSuppressedByHarderAssignedTarget()
    {
        var (job, worker) = CreateJob();
        var share = FindShare(job.ForDifficulty(1000), worker);
        Assert.True(share.IsBlockCandidate);
        Assert.Equal(1000, share.Difficulty);
        Assert.Equal(64, share.BlockHash.Length);
    }

    [Fact]
    public void JobDiscriminator_SeparatesIdenticalTemplatesAcrossRestarts()
    {
        var (first, _) = CreateJob();
        var (second, _) = CreateJob();
        var firstWork = (object[]) first.ForDifficulty(1).GetJobParams(true);
        var secondWork = (object[]) second.ForDifficulty(1).GetJobParams(true);
        Assert.NotEqual(firstWork[2], secondWork[2]);
        Assert.Equal(firstWork[1], secondWork[1]);
    }

    [Theory]
    [InlineData("00000000", "0000000000000000", "0000000000000000")]
    [InlineData("0000000000000000", "00000000", "0000000000000000")]
    [InlineData("0000000000000000", "0000000000000000", "00000000")]
    [InlineData("gg00000000000000", "0000000000000000", "0000000000000000")]
    [InlineData("0000000000000000", " 000000000000000", "0000000000000000")]
    [InlineData("0000000000000000", "0000000000000000", "-000000000000001")]
    [InlineData(null, "0000000000000000", "0000000000000000")]
    public void MalformedShareFields_AreRejectedBeforeProofOrAccounting(string extra, string time, string nonce)
    {
        var (job, worker) = CreateJob();
        var error = Assert.Throws<StratumException>(() => job.ForDifficulty(1).ProcessShare(worker, extra, time, nonce));
        Assert.Equal(StratumError.Other, error.Code);
    }

    [Fact]
    public void NoVersionRolling_AndCaseInsensitiveDuplicateIdentity()
    {
        var (job, worker) = CreateJob();
        var issued = job.ForDifficulty(1e-9);
        Assert.Throws<StratumException>(() => issued.ProcessShare(worker,
            "0000000000000000", "0000000000000000", "0000000000000000", "00000000"));
        try { issued.ProcessShare(worker, "abcdefabcdefabcd", "0000000000000000", "0000000000000000"); }
        catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        var duplicate = Assert.Throws<StratumException>(() => job.ForDifficulty(2e-9).ProcessShare(worker,
            "ABCDEFABCDEFABCD", "0000000000000000", "0000000000000000"));
        Assert.Equal(StratumError.DuplicateShare, duplicate.Code);
    }

    [Fact]
    public void PreActivationTemplate_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CreateJob(height: 19));
    }

    private Share FindShare(BitcoinBlake2bJob issued, StratumConnection worker)
    {
        var notify = (object[]) issued.GetJobParams(true);
        var nonceBytes = new byte[8];
        for(ulong nonce = 0; nonce < 100000; nonce++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(nonceBytes, nonce);
            try
            {
                return issued.ProcessShare(worker, "0000000000000000", (string) notify[7], nonceBytes.ToHexString()).Share;
            }
            catch(StratumException ex) when(ex.Code == StratumError.LowDifficultyShare) { }
        }
        throw new InvalidOperationException("No low-difficulty test proof found");
    }

    private (BitcoinBlake2bJob, StratumConnection) CreateJob(string bits = "207fffff", uint height = 21)
    {
        var coin = (BitcoinBlake2bTemplate) ModuleInitializer.CoinTemplates["bitcoin-blake2b"];
        var target = BitcoinBlake2bHeader.DecodeCompactTarget(BitcoinBlake2bHeader.ParseCompactBits(bits));
        var template = new BlockTemplate
        {
            Height = height, Version = 0xa0000000, CurTime = 1700000000, Bits = bits,
            Target = target.ToString("x").PadLeft(64, '0'),
            PreviousBlockhash = new string('0', 64), CoinbaseValue = 5000000000,
            Transactions = Array.Empty<BitcoinBlockTransaction>(), Rules = new[] { "!blake2b" },
        };
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var pool = new PoolConfig { Id = "blake2b-test", Coin = "bitcoin-blake2b", Template = coin };
        var job = new BitcoinBlake2bJob();
        job.InitBlake2b(template, "1234", pool, new BitcoinPoolConfigExtra(), new ClusterConfig(),
            clock, address, Network.RegTest, coin.ShareMultiplier,
            coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);
        var worker = new StratumConnection(new NLog.NullLogger(NLog.LogManager.LogFactory),
            container.Resolve<Microsoft.IO.RecyclableMemoryStreamManager>(), clock, "blake2b-test", false);
        worker.SetContext(new BitcoinWorkerContext { ExtraNonce1 = "00000001", Difficulty = 1e-9 });
        return (job, worker);
    }
}
