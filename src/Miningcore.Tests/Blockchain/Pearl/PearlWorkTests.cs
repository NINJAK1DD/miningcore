using System;
using System.IO;
using System.Numerics;
using Miningcore.Blockchain.Pearl;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlWorkTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("null")]
    [InlineData("\"two\"")]
    [InlineData("1,\"Factor\":0")]
    public void PplnsRejectsInvalidWindowBeforeAcceptingWork(string factor)
    {
        var pool = new Miningcore.Configuration.PoolConfig
        {
            PaymentProcessing = new Miningcore.Configuration.PoolPaymentProcessingConfig
            {
                PayoutScheme = Miningcore.Configuration.PayoutScheme.PPLNS,
                PayoutSchemeConfig = Newtonsoft.Json.Linq.JObject.Parse("{\"factor\":" + factor + "}")
            }
        };
        var error = Assert.Throws<InvalidDataException>(() => new PearlPoolConfig().Validate(pool));
        Assert.Contains("positive number", error.Message);
    }

    [Theory]
    [InlineData(Miningcore.Configuration.PayoutScheme.PPLNSBF)]
    public void RuntimeRejectsUncertifiedRewardSchemes(Miningcore.Configuration.PayoutScheme scheme)
    {
        var pool = new Miningcore.Configuration.PoolConfig
        {
            PaymentProcessing = new Miningcore.Configuration.PoolPaymentProcessingConfig {PayoutScheme = scheme}
        };
        var error = Assert.Throws<InvalidDataException>(() => new PearlPoolConfig().Validate(pool));
        Assert.Contains("PPS, SOLO, PPLNS and PROP", error.Message);
    }

    [Fact]
    public void RegisteredCoinUsesDedicatedPearlRuntimeAndAlgorithm()
    {
        ModuleInitializer.Initialize();
        var coin = Assert.IsType<Miningcore.Configuration.PearlCoinTemplate>(ModuleInitializer.CoinTemplates["pearl"]);
        Assert.Equal("PRL", coin.Symbol);
        Assert.Equal("PearlHash", coin.GetAlgorithmName());
        Assert.Equal(Miningcore.Configuration.CoinFamily.Pearl, coin.Family);
    }
    internal const string Address = "rprl1pjkt5h3mld9xj6ar2ys5xd2ny7062uqmthav3ayytr8yxhqxdr5rqlq2fzn";
    internal static PearlBlockJob Block()
    {
        using var stream = typeof(PearlWorkTests).Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.regtest-template.json");
        using var reader = new StreamReader(stream);
        return new PearlBlockJob(JsonConvert.DeserializeObject<PearlBlockTemplate>(reader.ReadToEnd()), Address, PearlNetwork.Regtest, new byte[24]);
    }
    private sealed class ManualTime : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.000001)]
    [InlineData(123.456789)]
    [InlineData(1e60)]
    public void ExactTargetNeverRoundsTowardEasierWork(double difficulty)
    {
        var target = PearlWork.Target(difficulty);
        var bits = (ulong) BitConverter.DoubleToInt64Bits(difficulty);
        var mantissa = new BigInteger((bits & ((1UL << 52) - 1)) | (1UL << 52));
        var exponent = (int) ((bits >> 52) & 0x7ff) - 1023 - 52;
        var numerator = exponent < 0 ? PearlWork.DifficultyOne << -exponent : PearlWork.DifficultyOne;
        var denominator = exponent > 0 ? mantissa << exponent : mantissa;
        Assert.True(target * denominator <= numerator);
        Assert.True((target + 1) * denominator > numerator);
        Assert.True(target * (1UL << 24) <= PearlConsensus.MaximumTarget);
    }

    [Fact]
    public void InvalidDifficultyCannotCreateTrivialWork()
    {
        foreach(var value in new[] {0d, -1, double.NaN, double.PositiveInfinity, 1e-10, 1e61})
            Assert.Throws<ArgumentOutOfRangeException>(() => PearlWork.Target(value));
    }

    [Fact]
    public void SessionJobsEnforceGraceTipLifetimeAndBoundedRetention()
    {
        var time = new ManualTime();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var worker = new PearlWorkerContext(time);
        worker.Init(1, null, clock, time);
        var first = worker.Issue(Block(), 2);
        Assert.Same(first, worker.Find(first.Id, first.Block.PreviousBlockHash, 120, 30));
        time.Seconds = 10;
        worker.Difficulty = 2;
        var second = worker.Issue(Block(), 2);
        time.Seconds = 39;
        Assert.Same(first, worker.Find(first.Id, first.Block.PreviousBlockHash, 120, 30));
        time.Seconds = 40;
        Assert.Throws<StratumException>(() => worker.Find(first.Id, first.Block.PreviousBlockHash, 120, 30));
        Assert.Throws<StratumException>(() => worker.Find(second.Id, new string('a',64), 120, 30));
        var other = new PearlWorkerContext(time);
        Assert.Throws<StratumException>(() => other.Find(second.Id, second.Block.PreviousBlockHash, 120, 30));
        worker.Issue(Block(), 2);
        Assert.Throws<StratumException>(() => worker.Find(first.Id, first.Block.PreviousBlockHash, 120, 30));
        time.Seconds = 130;
        Assert.Throws<StratumException>(() => worker.Find(second.Id, second.Block.PreviousBlockHash, 120, 30));
    }

    [Fact]
    public void ProofIdentityAndRequestBudgetsAreBounded()
    {
        var time = new ManualTime();
        var worker = new PearlWorkerContext(time) {Difficulty = 1};
        var job = worker.Issue(Block(), 2);
        Assert.True(worker.TakeProofBudget());
        Assert.False(worker.TakeProofBudget());
        time.Seconds++;
        Assert.True(worker.TakeProofBudget());
        var identity = PearlWork.Identity(job.Block.Header.Span, new byte[164]);
        worker.Remember(job, identity);
        Assert.Throws<StratumException>(() => worker.Remember(job, identity));
        for(var i = 1; i < 256; i++) worker.Remember(job, i.ToString());
        Assert.Throws<StratumException>(() => worker.Remember(job, "full"));
        Assert.Throws<ArgumentException>(() => PearlWork.Identity(new byte[75], new byte[164]));
    }
}
