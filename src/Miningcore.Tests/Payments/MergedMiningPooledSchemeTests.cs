using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class MergedMiningPooledSchemeTests
{
    [Theory]
    [InlineData(PayoutScheme.PPLNS)]
    [InlineData(PayoutScheme.PROP)]
    public async Task Pearl_PooledRewardsUseAssignedCreditRatherThanLuckyAchievedDifficulty(PayoutScheme kind)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("pearl", kind, 1m);
        pool.Config.Template = new PearlCoinTemplate {Symbol = "PRL", Family = CoinFamily.Pearl};
        var block = CreateBlock("pearl", 100);
        var first = CreateShare("pearl", "miner-a", 25, 100, block.Created) with
            {ShareDifficulty = 1_000_000, ActualDifficulty = 1_000_000};
        var second = CreateShare("pearl", "miner-b", 75, 100, block.Created.AddSeconds(-1)) with
            {ShareDifficulty = 100, ActualDifficulty = 100};
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, "pearl", block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[] {first, second});
        IPayoutScheme scheme = kind == PayoutScheme.PPLNS
            ? new PPLNSPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository)
            : new PROPPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository);

        // A 10 PRL reward after the independently tested 2.5% operator allocation.
        await scheme.UpdateBalancesAsync(fixture.WriteConnection, fixture.Transaction, pool,
            fixture.PayoutHandler, block, 9.75m, CancellationToken.None);
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, "pearl", "miner-a", 2.4375m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, "pearl", "miner-b", 7.3125m, Arg.Any<string>());
    }

    [Fact]
    public async Task Prop_UsesEachProjectedPoolsIndependentRound()
    {
        var fixture = CreateFixture();
        var parent = CreatePool("ltc", PayoutScheme.PROP);
        var auxiliary = CreatePool("doge", PayoutScheme.PROP);
        var parentBlock = CreateBlock("ltc", 200);
        var auxiliaryBlock = CreateBlock("doge", 300);

        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection,
                "ltc", parentBlock.Created, true, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateShare("ltc", "ltc-a", 25, 100, parentBlock.Created),
                CreateShare("ltc", "ltc-b", 75, 100,
                    parentBlock.Created.AddSeconds(-1)),
            });
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection,
                "doge", auxiliaryBlock.Created, true, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateShare("doge", "doge-a", 50, 200,
                    auxiliaryBlock.Created),
                CreateShare("doge", "doge-b", 50, 200,
                    auxiliaryBlock.Created.AddSeconds(-1)),
            });

        var scheme = new PROPPaymentScheme(fixture.ConnectionFactory,
            fixture.ShareRepository, fixture.BlockRepository,
            fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection,
            fixture.Transaction, parent, fixture.PayoutHandler, parentBlock,
            40m, CancellationToken.None);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection,
            fixture.Transaction, auxiliary, fixture.PayoutHandler,
            auxiliaryBlock, 10_000m, CancellationToken.None);

        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "ltc", "ltc-a",
            10m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "ltc", "ltc-b",
            30m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "doge", "doge-a",
            5_000m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "doge", "doge-b",
            5_000m, Arg.Any<string>());
        await fixture.ShareRepository.Received(1).DeleteSharesBeforeInclusiveAsync(
            fixture.WriteConnection, fixture.Transaction, "ltc", parentBlock.Created,
            Arg.Any<CancellationToken>());
        await fixture.ShareRepository.Received(1).DeleteSharesBeforeInclusiveAsync(
            fixture.WriteConnection, fixture.Transaction, "doge", auxiliaryBlock.Created,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pplns_UsesIndependentNetworkWindowsAndVarDiffShares()
    {
        var fixture = CreateFixture();
        var parent = CreatePool("ltc", PayoutScheme.PPLNS, 1m);
        var auxiliary = CreatePool("doge", PayoutScheme.PPLNS, 0.5m);
        var parentBlock = CreateBlock("ltc", 200);
        var auxiliaryBlock = CreateBlock("doge", 300);

        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection,
                "ltc", parentBlock.Created, true, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateShare("ltc", "ltc-a", 60, 100, parentBlock.Created),
                CreateShare("ltc", "ltc-b", 40, 100,
                    parentBlock.Created.AddSeconds(-1)),
            });
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection,
                "doge", auxiliaryBlock.Created, true, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                CreateShare("doge", "doge-a", 80, 400,
                    auxiliaryBlock.Created),
                CreateShare("doge", "doge-b", 120, 400,
                    auxiliaryBlock.Created.AddSeconds(-1)),
            });

        var scheme = new PPLNSPaymentScheme(fixture.ConnectionFactory,
            fixture.ShareRepository, fixture.BlockRepository,
            fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection,
            fixture.Transaction, parent, fixture.PayoutHandler, parentBlock,
            50m, CancellationToken.None);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection,
            fixture.Transaction, auxiliary, fixture.PayoutHandler,
            auxiliaryBlock, 10_000m, CancellationToken.None);

        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "ltc", "ltc-a",
            30m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "ltc", "ltc-b",
            20m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "doge", "doge-a",
            4_000m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(
            fixture.WriteConnection, fixture.Transaction, "doge", "doge-b",
            6_000m, Arg.Any<string>());
    }

    [Fact]
    public async Task Pps_BlockOutcomeNeverCreatesOrReversesShareLiability()
    {
        var shareRepository = Substitute.For<IShareRepository>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var pool = CreatePool("ltc", PayoutScheme.PPS);
        var scheme = new PPSPaymentScheme(shareRepository);
        var block = CreateBlock("ltc", 200);

        await scheme.UpdateBalancesAsync(connection, transaction, pool,
            Substitute.For<IPayoutHandler>(), block, 50m,
            CancellationToken.None);

        await shareRepository.DidNotReceiveWithAnyArgs()
            .DeleteSharesBeforeInclusiveAsync(default, default, default,
                default, default);
    }

    [Theory]
    [InlineData(1e100, 1)]
    [InlineData(double.MaxValue, 1e-200)]
    public async Task PplnsBf_LargeLegacyRatioCannotOverflowWindowOrFinderReward(double difficulty, double network)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("large-bf", PayoutScheme.PPLNSBF, 2m);
        pool.Config.PaymentProcessing.PayoutSchemeConfig = JObject.FromObject(new { Factor = 2m, BlockFinderPercentage = 5m });
        var id = pool.Config.Id;
        var block = CreateBlock(id, 100);
        block.Miner = "finder";
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
            { CreateShare(id, "miner", difficulty, network, block.Created) });
        var scheme = new PPLNSBFPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository,
            fixture.BlockRepository, fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection, fixture.Transaction, pool,
            fixture.PayoutHandler, block, 50m, CancellationToken.None);
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, id, "miner", 47.5m, Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, id, "finder", 2.5m, Arg.Any<string>());
    }

    [Theory]
    [InlineData(1e100, 1, false)]
    [InlineData(1e100, 1e-250, false)]
    [InlineData(1e100, 1e-250, true)]
    [InlineData(1e-100, 1e250, true)]
    public async Task Prop_ExtremeRatiosPreserveRoundProportionsInEitherOrder(double difficulty, double network, bool reverse)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("large-prop", PayoutScheme.PROP);
        var id = pool.Config.Id;
        var block = CreateBlock(id, 100);
        var small = CreateShare(id, "small", difficulty, network, block.Created);
        var large = CreateShare(id, "large", difficulty * 2, network, block.Created.AddSeconds(-1));
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(reverse ? new[] { large, small } : new[] { small, large });
        var scheme = new PROPPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository,
            fixture.BlockRepository, fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection, fixture.Transaction, pool,
            fixture.PayoutHandler, block, 60m, CancellationToken.None);
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, id, "small", Arg.Is<decimal>(x => Math.Abs(x - 20m) < 0.00000001m), Arg.Any<string>());
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, id, "large", Arg.Is<decimal>(x => Math.Abs(x - 40m) < 0.00000001m), Arg.Any<string>());
    }

    [Fact]
    public async Task Prop_OverflowingDiagnosticTotalsCannotChangeEqualRewardWeights()
    {
        var fixture = CreateFixture();
        var pool = CreatePool("prop-diagnostics", PayoutScheme.PROP);
        var id = pool.Config.Id;
        var block = CreateBlock(id, 100);
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
            {
                CreateShare(id, "a", double.MaxValue, 1, block.Created),
                CreateShare(id, "a", double.MaxValue, 1, block.Created.AddSeconds(-1)),
                CreateShare(id, "b", double.MaxValue, 1, block.Created.AddSeconds(-2)),
                CreateShare(id, "b", double.MaxValue, 1, block.Created.AddSeconds(-3)),
            });
        var scheme = new PROPPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository,
            fixture.BlockRepository, fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection, fixture.Transaction, pool,
            fixture.PayoutHandler, block, 60m, CancellationToken.None);
        foreach(var miner in new[] { "a", "b" })
            await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
                fixture.Transaction, id, miner, 30m, Arg.Any<string>());
    }

    [Theory]
    [InlineData(PayoutScheme.PROP, double.NaN, 1)]
    [InlineData(PayoutScheme.PPLNS, double.PositiveInfinity, 1)]
    [InlineData(PayoutScheme.PPLNSBF, 1, 0)]
    [InlineData(PayoutScheme.PROP, 0, 1)]
    [InlineData(PayoutScheme.PPLNS, -1, 1)]
    [InlineData(PayoutScheme.PPLNSBF, double.NaN, 1)]
    public async Task InvalidSettlementInputs_CannotCommitRewardsOrConsumeRound(PayoutScheme kind, double difficulty, double network)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("invalid-score", kind, 2m);
        var id = pool.Config.Id;
        var block = CreateBlock(id, 100);
        block.Miner = "finder";
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
                { CreateShare(id, "miner\"\n", difficulty, network, block.Created) with
                    { BlockHeight = 77, IpAddress = "private-ip", UserAgent = "private-agent" } });
        IPayoutScheme scheme = kind switch
        {
            PayoutScheme.PROP => new PROPPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
            PayoutScheme.PPLNS => new PPLNSPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
            _ => new PPLNSBFPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
        };
        var error = await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => scheme.UpdateBalancesAsync(fixture.WriteConnection,
            fixture.Transaction, pool, fixture.PayoutHandler, block, 50m, CancellationToken.None));
        var identity = JObject.Parse(error.Message.Split("share=")[1]);
        Assert.Equal(id, identity["poolId"].Value<string>());
        Assert.Equal("miner\"\n", identity["miner"].Value<string>());
        Assert.Equal(block.Created, identity["created"].Value<DateTime>());
        Assert.Equal(77, identity["shareBlockHeight"].Value<int>());
        Assert.Equal(100, identity["settlementBlockHeight"].Value<int>());
        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain("private-ip", error.Message);
        Assert.DoesNotContain("private-agent", error.Message);
        await fixture.BalanceRepository.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
        await fixture.ShareRepository.DidNotReceiveWithAnyArgs().DeleteSharesBeforeInclusiveAsync(default, default, default, default, default);
    }

    [Theory]
    [InlineData(PayoutScheme.PROP)]
    [InlineData(PayoutScheme.PPLNS)]
    [InlineData(PayoutScheme.PPLNSBF)]
    public async Task AdjustedZeroDifficulty_IdentifiesHandlerFailureBeforeAnyRewardWrites(PayoutScheme kind)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("adjusted-zero", kind, 2m);
        var id = pool.Config.Id;
        var block = CreateBlock(id, 100);
        block.Miner = "finder";
        fixture.PayoutHandler.AdjustShareDifficulty(2).Returns(0d);
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
                { CreateShare(id, "miner", 2, 1, block.Created) });
        IPayoutScheme scheme = kind switch
        {
            PayoutScheme.PROP => new PROPPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
            PayoutScheme.PPLNS => new PPLNSPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
            _ => new PPLNSBFPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository, fixture.BlockRepository, fixture.BalanceRepository),
        };
        var error = await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => scheme.UpdateBalancesAsync(
            fixture.WriteConnection, fixture.Transaction, pool, fixture.PayoutHandler, block, 50m, CancellationToken.None));
        var identity = JObject.Parse(error.Message.Split("share=")[1]);
        Assert.Equal(2, identity["storedDifficulty"].Value<double>());
        Assert.Equal(0, identity["adjustedDifficulty"].Value<double>());
        await fixture.BalanceRepository.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
        await fixture.ShareRepository.DidNotReceiveWithAnyArgs().DeleteSharesBeforeInclusiveAsync(default, default, default, default, default);
    }

    private static Fixture CreateFixture()
    {
        var connectionFactory = Substitute.For<IConnectionFactory>();
        var readConnection = Substitute.For<IDbConnection>();
        var writeConnection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var shareRepository = Substitute.For<IShareRepository>();
        var blockRepository = Substitute.For<IBlockRepository>();
        var balanceRepository = Substitute.For<IBalanceRepository>();
        var payoutHandler = Substitute.For<IPayoutHandler>();
        connectionFactory.OpenConnectionAsync().Returns(Task.FromResult(
            readConnection));
        payoutHandler.AdjustShareDifficulty(Arg.Any<double>())
            .Returns(x => x.Arg<double>());
        payoutHandler.FormatAmount(Arg.Any<decimal>())
            .Returns(x => x.Arg<decimal>().ToString());
        shareRepository.CountSharesBeforeAsync(Arg.Any<IDbConnection>(),
                Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(0L);
        shareRepository.CountSharesBeforeInclusiveAsync(Arg.Any<IDbConnection>(),
                Arg.Any<IDbTransaction>(), Arg.Any<string>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(1L);
        return new Fixture(connectionFactory, readConnection, writeConnection,
            transaction, shareRepository, blockRepository, balanceRepository,
            payoutHandler);
    }

    [Theory]
    [InlineData(1e100, 1)]
    [InlineData(double.MaxValue, 1e-200)]
    public async Task Pplns_LargeFiniteLegacyShareCannotOverflowWindowOrDiagnostics(double difficulty, double network)
    {
        var fixture = CreateFixture();
        var pool = CreatePool("large-pplns", PayoutScheme.PPLNS, 2m);
        var block = CreateBlock("large-pplns", 100);
        var id = pool.Config.Id;
        var page = new[] { CreateShare(id, "miner", difficulty, network, block.Created) };
        fixture.ShareRepository.ReadSharesBeforeAsync(fixture.ReadConnection, id, block.Created,
            true, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new[]
                { page[0] });
        var scheme = new PPLNSPaymentScheme(fixture.ConnectionFactory, fixture.ShareRepository,
            fixture.BlockRepository, fixture.BalanceRepository);
        await scheme.UpdateBalancesAsync(fixture.WriteConnection, fixture.Transaction, pool,
            fixture.PayoutHandler, block, 50m, CancellationToken.None);
        await fixture.BalanceRepository.Received(1).AddAmountAsync(fixture.WriteConnection,
            fixture.Transaction, pool.Config.Id, "miner", 50m, Arg.Any<string>());
    }

    private static IMiningPool CreatePool(string id, PayoutScheme scheme,
        decimal? factor = null)
    {
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(new PoolConfig
        {
            Id = id,
            PaymentProcessing = new PoolPaymentProcessingConfig
            {
                Enabled = true,
                PayoutScheme = scheme,
                PayoutSchemeConfig = factor.HasValue
                    ? JObject.FromObject(new { Factor = factor.Value })
                    : null,
            },
        });
        return pool;
    }

    private static Block CreateBlock(string poolId, ulong height) => new()
    {
        PoolId = poolId,
        BlockHeight = height,
        Created = new DateTime(2026, 8, 29, 12, 0, 0,
            DateTimeKind.Utc).AddMinutes(height),
    };

    private static Share CreateShare(string poolId, string miner,
        double difficulty, double networkDifficulty, DateTime created) => new()
    {
        PoolId = poolId,
        Miner = miner,
        Difficulty = difficulty,
        NetworkDifficulty = networkDifficulty,
        Created = created,
    };

    private sealed record Fixture(
        IConnectionFactory ConnectionFactory,
        IDbConnection ReadConnection,
        IDbConnection WriteConnection,
        IDbTransaction Transaction,
        IShareRepository ShareRepository,
        IBlockRepository BlockRepository,
        IBalanceRepository BalanceRepository,
        IPayoutHandler PayoutHandler);
}
