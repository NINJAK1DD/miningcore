using System;
using System.IO;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using ProtoBuf;
using Xunit;

namespace Miningcore.Tests.Mining;

public class PpsArithmeticTests
{
    private static readonly DateTime Cutover = new(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
    private static PoolConfig Pool() => new()
    {
        Id = "bitcoin-pps-lab",
        Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
        RewardRecipients = new[] { new RewardRecipient { Percentage = 1m } },
        PaymentProcessing = new PoolPaymentProcessingConfig
        {
            Enabled = true, PayoutScheme = PayoutScheme.PPS, PpsBinary64Activation = Cutover,
        },
    };
    private static Share Sample(DateTime time) => new()
    {
        PoolId = "bitcoin-pps-lab", AccountingId = "10000000000040008000000000000001",
        Miner = "regtest-miner", Created = time, Difficulty = 1e-10,
        NetworkDifficulty = 4.65e-10, RewardBasisSatoshis = 5_000_000_000,
    };

    [Fact]
    public void RealServiceCase_HasExplicitLegacyAndExactResults()
    {
        var pool = Pool();
        var old = Sample(Cutover.AddTicks(-1));
        ShareAccounting.AttachPpsCreditEvidence(pool, old);
        Assert.Equal(0, old.PpsArithmeticVersion);
        Assert.Equal(10.64516129032258064516129m, old.PpsCalculatedAmount);
        var current = Sample(Cutover);
        ShareAccounting.AttachPpsCreditEvidence(pool, current);
        Assert.Equal(1, current.PpsArithmeticVersion);
        Assert.Equal(10.645161290322581103779573m, current.PpsCalculatedAmount);
        Assert.Equal(old.PpsCalculatedAmount, ShareAccounting.CreatePpsCredit(pool, old).CalculatedAmount);
        Assert.Equal(current.PpsCalculatedAmount, ShareAccounting.CreatePpsCredit(pool, current).CalculatedAmount);
        Assert.Equal(0.00000000000001834473132m,
            40 * (current.PpsCalculatedAmount.Value - old.PpsCalculatedAmount.Value));
    }

    [Fact]
    public void VersionTravelsThroughWireJournalAndSanitizedRecovery()
    {
        var pool = Pool(); var share = Sample(Cutover);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var wire = Serializer.DeepClone(share);
        var journal = JsonConvert.DeserializeObject<Share>(JsonConvert.SerializeObject(wire));
        pool.PaymentProcessing = null; pool.RewardRecipients = null;
        var recovered = ShareAccounting.CreatePpsCredit(pool, journal, true);
        Assert.Equal(1, recovered.ArithmeticVersion);
        Assert.Equal(10.645161290322581103779573m, recovered.CalculatedAmount);
        journal.PpsCalculatedAmount = 100;
        Assert.Throws<InvalidDataException>(() => ShareAccounting.CreatePpsCredit(pool, journal, true));
    }

    [Fact]
    public void UnknownVersionOrChangedCutoverCannotReinterpretEvidence()
    {
        var pool = Pool(); var share = Sample(Cutover);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        pool.PaymentProcessing.PpsBinary64Activation = Cutover.AddDays(1);
        Assert.Throws<InvalidDataException>(() => ShareAccounting.CreatePpsCredit(pool, share));
        share.PpsArithmeticVersion = 7;
        Assert.Throws<InvalidDataException>(() => ShareAccounting.CreatePpsCredit(pool, share));
    }

    [Fact]
    public void MissingActivationKeepsLegacyDefault()
    {
        var pool = Pool(); pool.PaymentProcessing.PpsBinary64Activation = null;
        var share = Sample(Cutover.AddDays(1));
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        Assert.Equal(0, share.PpsArithmeticVersion);
        Assert.Equal(10.64516129032258064516129m, share.PpsCalculatedAmount);
    }

    [Fact]
    public void EqualAmountsWithDifferentVersionsHaveDifferentReceiptHashes()
    {
        var pool = Pool(); var share = Sample(Cutover);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var credit = ShareAccounting.CreatePpsCredit(pool, share);
        var shares = Array.Empty<Miningcore.Persistence.Model.Share>();
        var id = Guid.ParseExact(share.AccountingId, "N");
        var legacy = credit with { ArithmeticVersion = 0 };
        Assert.NotEqual(ShareAccounting.ComputePayloadHash(id, shares, new[] { legacy }),
            ShareAccounting.ComputePayloadHash(id, shares, new[] { credit }));
    }

    [Theory]
    [InlineData(1, 2, 100, "25")]
    [InlineData(1, 2, 99, "24.75")]
    [InlineData(double.Epsilon, double.Epsilon, 100, "50")]
    [InlineData(double.MaxValue, double.MaxValue, 100, "50")]
    public void ExactRatioHandlesInputsWithoutIntermediateDecimalConversion(double assigned,
        double network, int retained, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            PpsArithmetic.Calculate(5_000_000_000, assigned, network, retained));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidBinary64IsRejected(double difficulty) =>
        Assert.Throws<InvalidDataException>(() => PpsArithmetic.Calculate(1, difficulty, 1, 100));

    [Fact]
    public void UnrepresentableDecimalNeverRoundsSilently()
    {
        Assert.Throws<InvalidDataException>(() => PpsArithmetic.Calculate(long.MaxValue, 1, 13, 99));
        Assert.Throws<InvalidDataException>(() => PpsArithmetic.Calculate(1, double.Epsilon, 1, 100));
    }
}
