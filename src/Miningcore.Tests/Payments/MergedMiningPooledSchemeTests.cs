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

        await shareRepository.Received(1).DeleteSharesBeforeAsync(connection,
            transaction, "ltc", block.Created, CancellationToken.None);
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
