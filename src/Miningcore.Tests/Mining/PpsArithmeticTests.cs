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
    [Theory]
    [InlineData("99.999999999999999999999999", "49.999999999999999999999999")]
    [InlineData("99.00000000000000000000000000", "49.5")]
    [InlineData("0.000000000000000000000001", "0.000000000000000000000001")]
    public void CanonicalRetainedPrecisionAccepts24PlacesAndInsignificantZeroes(string retained, string expected)
    {
        var percent = decimal.Parse(retained, System.Globalization.CultureInfo.InvariantCulture);
        var reward = percent < 1 ? 10_000_000_000L : 5_000_000_000L;
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            PpsArithmetic.Calculate(reward, 1, 1, percent));
    }

    [Theory]
    [InlineData("99.9999999999999999999999999")]
    [InlineData("0.0000000000000000000000001")]
    public void CanonicalRetainedPrecisionRejects25SignificantPlaces(string retained) =>
        Assert.Throws<InvalidDataException>(() => PpsArithmetic.Calculate(5_000_000_000, 1, 1,
            decimal.Parse(retained, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void AdmissionRejectsUnknownVersion(short version)
    {
        var share = Sample(Cutover);
        share.AccountingRole = ShareAccountingRole.Single;
        share.SessionId = "session";
        share.IpAddress = "127.0.0.1";
        share.ShareDifficulty = share.ActualDifficulty = 1;
        share.PpsCalculatedAmount = 1;
        var pools = new System.Collections.Generic.Dictionary<string, PoolConfig> { [share.PoolId] = Pool() };
        Assert.Single(ShareAccounting.ValidateAndFlatten(share, pools));
        share.PpsArithmeticVersion = version;
        Assert.Throws<InvalidDataException>(() => ShareAccounting.ValidateAndFlatten(share, pools));
    }

    // Frozen independent reference: Python random.Random(194), IEEE-754 bits,
    // Fraction(reward, 10**8) * Fraction(assigned) / Fraction(network)
    // * Fraction(retained) / 100, then integer floor at 10**24.
    [Theory]
    [InlineData(3550599586L, 0x3fc2707194d566f0UL, 0x400cfbf26300e836UL, "99.999999999999999999999999", "1.411769329827483321361968")]
    [InlineData(4756949341L, 0x40168c22fddf4f91UL, 0x400a2d2b24a09738UL, "99", "81.129683803074650060560588")]
    [InlineData(4001289520L, 0x401c710c1fdbe8eaUL, 0x3fe1a73f8c3cf7aaUL, "97.5", "502.831224484037356317632051")]
    [InlineData(1968178627L, 0x402fa3d546554b5eUL, 0x3fc36700366234f5UL, "99.999999999999999999999999", "2054.122422072762919786599430")]
    [InlineData(395667904L, 0x3feeef85786dc14bUL, 0x3fef4310b2e55133UL, "99.999999999999999999999999", "3.915375335256831950781089")]
    [InlineData(3364946663L, 0x40169d43432d71a0UL, 0x4028f870ad2fca72UL, "99.999999999999999999999999", "15.237186361036696345613374")]
    [InlineData(1152899703L, 0x4029faeb4c73ed8eUL, 0x400c01ec697193f5UL, "97.5", "41.708376080412665601475684")]
    [InlineData(531411855L, 0x400e665f6a37ca77UL, 0x402f20134787eccbUL, "99.999999999999999999999999", "1.297567153767395163192928")]
    [InlineData(401099132L, 0x3fd64133c3fb032dUL, 0x400ae5ae17622d91UL, "97.5", "0.404465186276076138683873")]
    [InlineData(4981815097L, 0x3fc673ce1195c19bUL, 0x3fc1d2b395b789ceUL, "97.5", "61.188835048868757371624325")]
    [InlineData(4469021565L, 0x40011f54b922446eUL, 0x3fd8bbb155aec7cfUL, "100", "247.506648570496205208785366")]
    [InlineData(4581835293L, 0x3fdee1c35cb9cfe4UL, 0x3fc6a2b335b42b00UL, "99.999999999999999999999999", "125.020809722549748345870485")]
    [InlineData(3536479019L, 0x402c3c05891cafdbUL, 0x3fdc964865f6dd87UL, "99.999999999999999999999999", "1117.715617096035244504714659")]
    [InlineData(935415754L, 0x4022996b698f6278UL, 0x3fb1182e58740c35UL, "100", "1302.734225499684953160367152")]
    [InlineData(3077095275L, 0x4020fcd55cd5939cUL, 0x4007b18693ffe246UL, "99", "87.365598761538485431322107")]
    [InlineData(4170617061L, 0x4019a99c18a34786UL, 0x3fe060f5c79d0a9fUL, "99.999999999999999999999999", "522.768183002289986530829055")]
    [InlineData(4511794770L, 0x402cd21f111f0fffUL, 0x3feccd06d44703ddUL, "99.999999999999999999999999", "722.385979100491900988682671")]
    [InlineData(3114762494L, 0x3fbbd2d302757f28UL, 0x3fcd941d3a754f6dUL, "97.5", "14.283499762244394690543218")]
    [InlineData(4627459007L, 0x3fe4ef83a6732494UL, 0x3ff5f21aea2f7be9UL, "99.999999999999999999999999", "22.072328595768492186819997")]
    [InlineData(2156139345L, 0x3fb3153d2819658cUL, 0x3fb1f05395f51038UL, "99", "22.707277484719433195265904")]
    [InlineData(347148725L, 0x4026767f0586785aUL, 0x3fda75fa87b3696eUL, "97.5", "91.945751179217103766395696")]
    [InlineData(3338954087L, 0x3fea39748dca20b1UL, 0x3fb8a9833ad4f903UL, "99.999999999999999999999999", "284.037369817027381861833191")]
    [InlineData(4238112085L, 0x3fe8e0c212f2fa2dUL, 0x3fcdd134e3ec1c67UL, "97.5", "137.906514414863659698438753")]
    [InlineData(1361587296L, 0x3ff2baa9bb55755bUL, 0x3ff6342567ebf68bUL, "97.5", "11.198064427351354969111282")]
    [InlineData(3401256054L, 0x3ff8d6ba755eee55UL, 0x3fbe79f90b6648a8UL, "97.5", "432.443889444281263317299674")]
    [InlineData(3758210347L, 0x40162259dca5c2aaUL, 0x3fd5d586876b607eUL, "99", "603.482657401091114505492495")]
    [InlineData(1854028190L, 0x3ff006afaad5957aUL, 0x401449af361c3aa1UL, "99.999999999999999999999999", "3.661416085458054919888990")]
    [InlineData(734785719L, 0x402bdce0c8f72ee0UL, 0x3fe54d44b94f0040UL, "100", "153.776018450490041030396412")]
    [InlineData(3527471412L, 0x3fd47759b97f0ff0UL, 0x4016b1d9998dc89fUL, "97.5", "1.938477067499927913298744")]
    [InlineData(2459982525L, 0x402f8418afb8fdafUL, 0x3fb4ab6370f1c42fUL, "99.999999999999999999999999", "4801.129581302199315695159962")]
    [InlineData(1190496207L, 0x3fb5aea86cc48feeUL, 0x4004a99d3f4246a9UL, "100", "0.390389832785201433235197")]
    [InlineData(2987667009L, 0x4009aeb51a23aaeeUL, 0x3fc088c84091fd01UL, "99", "735.085531437345075743993648")]
    public void FrozenRationalReferenceVectors(long reward, ulong assignedBits, ulong networkBits,
        string retained, string expected)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, culture), PpsArithmetic.Calculate(reward,
            BitConverter.UInt64BitsToDouble(assignedBits), BitConverter.UInt64BitsToDouble(networkBits),
            decimal.Parse(retained, culture)));
    }

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
