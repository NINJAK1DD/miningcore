using System;
using System.Collections.Generic;
using System.IO;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;
using ProtoBuf;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareAccountingTests
{
    [Fact]
    public void PairedProof_PreservesOneEnvelopeAndTwoIndependentChains()
    {
        var envelope = CreatePair();
        var pools = CreatePools(PayoutScheme.PROP, PayoutScheme.PPLNS);

        var projections = ShareAccounting.ValidateAndFlatten(envelope, pools);

        Assert.Equal(2, projections.Length);
        Assert.Equal("ltc", projections[0].PoolId);
        Assert.Equal("doge", projections[1].PoolId);
        Assert.NotEqual(projections[0].Miner, projections[1].Miner);
        Assert.NotEqual(projections[0].BlockHeight, projections[1].BlockHeight);
        Assert.NotEqual(projections[0].NetworkDifficulty,
            projections[1].NetworkDifficulty);
        Assert.Equal(projections[0].AccountingId, projections[1].AccountingId);
    }

    [Fact]
    public void PairedProof_RoundTripsAsOneProtobufRecord()
    {
        var expected = CreatePair();
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, expected);
        stream.Position = 0;

        var actual = Serializer.Deserialize<Share>(stream);

        Assert.Equal(expected.AccountingId, actual.AccountingId);
        Assert.NotNull(actual.PairedShare);
        Assert.Equal("doge", actual.PairedShare.PoolId);
        Assert.Equal(ShareAccountingRole.Auxiliary,
            actual.PairedShare.AccountingRole);
    }

    [Fact]
    public void PpsEvidence_RoundTripsAndSurvivesSanitizedRecoveryConfiguration()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        var acceptingPool = CreatePool("ltc", PayoutScheme.PPS);
        ShareAccounting.AttachPpsCreditEvidence(acceptingPool, share);
        var expected = share.PpsCalculatedAmount;

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, share);
        stream.Position = 0;
        var recovered = Serializer.Deserialize<Share>(stream);
        var sanitizedPool = new PoolConfig
        {
            Id = "ltc",
            Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
        };

        var credit = ShareAccounting.CreatePpsCredit(sanitizedPool, recovered,
            allowMissingPpsConfiguration: true);

        Assert.Equal(expected, recovered.PpsCalculatedAmount);
        Assert.Equal(expected, credit.CalculatedAmount);
        Assert.Equal(share.Difficulty, credit.Difficulty);
        Assert.Equal(share.NetworkDifficulty, credit.NetworkDifficulty);
        Assert.Equal(share.RewardBasisSatoshis, credit.RewardBasisSatoshis);
    }

    [Fact]
    public void PpsEvidence_RejectsMissingOrConflictingLiveCalculation()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        var pool = CreatePool("ltc", PayoutScheme.PPS);

        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.CreatePpsCredit(pool, share));

        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        share.PpsCalculatedAmount += 0.000000000000000000000001m;
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.CreatePpsCredit(pool, share));
    }

    [Fact]
    public void PpsEvidence_DisabledConfigurationStillAuthorizesOnlyPpsAndRecomputes()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        pool.PaymentProcessing.Enabled = false;

        Assert.NotNull(ShareAccounting.CreatePpsCredit(pool, share));

        share.PpsCalculatedAmount += 0.000000000000000000000001m;
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.CreatePpsCredit(pool, share));

        pool.PaymentProcessing.PayoutScheme = PayoutScheme.PROP;
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.CreatePpsCredit(pool, share));
    }

    [Fact]
    public void PpsEvidence_SanitizedRecoveryCannotExceedImmutableZeroFeeMaximum()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        var acceptingPool = CreatePool("ltc", PayoutScheme.PPS);
        ShareAccounting.AttachPpsCreditEvidence(acceptingPool, share);
        var sanitizedPool = new PoolConfig
        {
            Id = "ltc",
            Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
        };

        share.PpsCalculatedAmount = share.RewardBasisSatoshis / 100_000_000m *
            (decimal) share.Difficulty / (decimal) share.NetworkDifficulty +
            0.000000000000000000000001m;

        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.CreatePpsCredit(sanitizedPool, share,
                allowMissingPpsConfiguration: true));
    }

    [Fact]
    public void PpsCredit_RejectsArithmeticOverflowButAllowsHighAssignedDifficulty()
    {
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        share.Difficulty = double.MaxValue;
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.AttachPpsCreditEvidence(pool, share));

        share.Difficulty = 101;
        share.NetworkDifficulty = 100;
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        Assert.Equal(6.3125m, share.PpsCalculatedAmount);
    }

    [Fact]
    public void ReplayHorizon_RejectsExpiredAndFutureAccountingEvidence()
    {
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        share.Created = now.AddDays(-30);

        ShareAccounting.ValidateReplayHorizon(share, now, 30);

        share.Created = now.AddDays(-30).AddTicks(-1);
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.ValidateReplayHorizon(share, now, 30));

        share.Created = now.AddMinutes(5).AddTicks(1);
        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.ValidateReplayHorizon(share, now, 30));
    }

    [Fact]
    public void PayloadHash_BindsEveryPpsLiabilityInput()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var credit = ShareAccounting.CreatePpsCredit(pool, share);
        var id = ShareAccounting.ParseCanonicalId(share.AccountingId);
        var persisted = new[] { ShareAccounting.ToPersistenceShare(share) };
        var expected = ShareAccounting.ComputePayloadHash(id, persisted,
            new[] { credit });

        foreach(var changed in new[]
                {
                    credit with { Difficulty = credit.Difficulty + 1 },
                    credit with
                    {
                        NetworkDifficulty = credit.NetworkDifficulty + 1,
                    },
                    credit with
                    {
                        RewardBasisSatoshis = credit.RewardBasisSatoshis + 1,
                    },
                    credit with { Created = credit.Created.AddTicks(1) },
                })
            Assert.NotEqual(expected, ShareAccounting.ComputePayloadHash(id,
                persisted, new[] { changed }));
    }

    [Theory]
    [InlineData("different-id")]
    [InlineData("nested")]
    [InlineData("same-pool")]
    [InlineData("different-session")]
    [InlineData("different-work")]
    public void PairedProof_RejectsPartialOrConflictingEvidence(string mutation)
    {
        var envelope = CreatePair();
        switch(mutation)
        {
            case "different-id":
                envelope.PairedShare.AccountingId = ShareAccounting.CreateId();
                break;
            case "nested":
                envelope.PairedShare.PairedShare = new Share();
                break;
            case "same-pool":
                envelope.PairedShare.PoolId = envelope.PoolId;
                break;
            case "different-session":
                envelope.PairedShare.SessionId = "other";
                break;
            case "different-work":
                envelope.PairedShare.ShareDifficulty++;
                break;
        }

        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.ValidateAndFlatten(envelope,
                CreatePools(PayoutScheme.PROP, PayoutScheme.PPLNS)));
    }

    [Fact]
    public void PpsCredit_UsesAssignedDifficultyRewardBasisAndRecipientDeduction()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        share.Difficulty = 4;
        share.NetworkDifficulty = 100;
        share.RewardBasisSatoshis = 5_000_000_000;
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        pool.RewardRecipients = new[]
        {
            new RewardRecipient { Address = "fee", Percentage = 2m },
        };

        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var credit = ShareAccounting.CreatePpsCredit(pool, share);

        Assert.Equal(1.96m, credit.CalculatedAmount);
        Assert.Equal(share.Difficulty, credit.Difficulty);
        Assert.Equal(share.NetworkDifficulty, credit.NetworkDifficulty);
    }

    [Fact]
    public void PpsCredit_DoesNotUseActualDifficultyWindfall()
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        share.Difficulty = 1;
        share.ActualDifficulty = 1_000_000;
        share.NetworkDifficulty = 100;
        share.RewardBasisSatoshis = 5_000_000_000;

        var pool = CreatePool("ltc", PayoutScheme.PPS);
        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var credit = ShareAccounting.CreatePpsCredit(pool, share);

        Assert.Equal(0.5m, credit.CalculatedAmount);
    }

    public static TheoryData<double, double, long, decimal, decimal>
        PpsReferenceVectors => new()
        {
            // (1 - f) * p * B, Rosenfeld, "Analysis of Bitcoin Pooled Mining
            // Reward Systems", section 3.2. Values are independently calculated.
            { 1, 100, 5_000_000_000, 0m, 0.5m },
            { 64, 1_000_000, 625_000_000, 1m, 0.000396m },
            { 1, 3, 1, 0m, 0.000000003333333333333333m },
        };

    [Theory]
    [MemberData(nameof(PpsReferenceVectors))]
    public void PpsCredit_MatchesExternalFixedVarDiffFeeAndRoundingVectors(
        double assignedDifficulty, double networkDifficulty,
        long rewardBasisSatoshis, decimal feePercent, decimal expected)
    {
        var share = CreatePair();
        share.PairedShare = null;
        share.AccountingRole = ShareAccountingRole.Single;
        share.Difficulty = assignedDifficulty;
        share.ActualDifficulty = assignedDifficulty * 10;
        share.NetworkDifficulty = networkDifficulty;
        share.RewardBasisSatoshis = rewardBasisSatoshis;
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        pool.RewardRecipients = feePercent > 0
            ? new[]
            {
                new RewardRecipient
                {
                    Address = "operator-fee",
                    Percentage = feePercent,
                },
            }
            : Array.Empty<RewardRecipient>();

        ShareAccounting.AttachPpsCreditEvidence(pool, share);
        var credit = ShareAccounting.CreatePpsCredit(pool, share);

        Assert.Equal(expected, credit.CalculatedAmount);
    }

    [Fact]
    public void LegacyShare_CannotSmugglePartialAccountingState()
    {
        var share = CreatePair();
        share.AccountingId = null;
        share.AccountingRole = ShareAccountingRole.None;
        share.PairedShare = null;

        Assert.Throws<InvalidDataException>(() =>
            ShareAccounting.ValidateAndFlatten(share,
                CreatePools(PayoutScheme.PROP, PayoutScheme.PPLNS)));
    }

    private static Share CreatePair()
    {
        var id = ShareAccounting.CreateId();
        var created = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var parent = new Share
        {
            PoolId = "ltc",
            Miner = "ltc-miner",
            Worker = "rig-1",
            UserAgent = "miner/1",
            IpAddress = "127.0.0.1",
            Source = "test",
            Difficulty = 4,
            ShareDifficulty = 8,
            ActualDifficulty = 8,
            SessionId = "session",
            AccountingId = id,
            AccountingRole = ShareAccountingRole.Parent,
            RewardBasisSatoshis = 625_000_000,
            BlockHeight = 100,
            NetworkDifficulty = 200,
            Created = created,
        };
        parent.PairedShare = new Share
        {
            PoolId = "doge",
            Miner = "doge-miner",
            Worker = parent.Worker,
            UserAgent = parent.UserAgent,
            IpAddress = parent.IpAddress,
            Source = parent.Source,
            Difficulty = 4,
            ShareDifficulty = parent.ShareDifficulty,
            ActualDifficulty = 8,
            SessionId = parent.SessionId,
            AccountingId = id,
            AccountingRole = ShareAccountingRole.Auxiliary,
            RewardBasisSatoshis = 1_000_000_000,
            BlockHeight = 200,
            NetworkDifficulty = 400,
            Created = created,
        };
        return parent;
    }

    private static Dictionary<string, PoolConfig> CreatePools(
        PayoutScheme parent, PayoutScheme auxiliary) => new()
    {
        ["ltc"] = CreatePool("ltc", parent),
        ["doge"] = CreatePool("doge", auxiliary),
    };

    private static PoolConfig CreatePool(string id, PayoutScheme scheme) => new()
    {
        Id = id,
        Enabled = true,
        Template = new BitcoinTemplate { Family = CoinFamily.Bitcoin },
        PaymentProcessing = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            PayoutScheme = scheme,
        },
    };
}
