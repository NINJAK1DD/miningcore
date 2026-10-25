using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain.Ergo;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Miningcore.Tests.Mining;
using Miningcore.Tests.Util;
using Miningcore.Tests.Util.Postgres;
using Target = NBitcoin.Target;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class ErgoExactDifficultySettlementTests
{
    [PostgresIntegrationFact]
    public async Task ErgoExactNetworkDifficultyPersistsReplaysAndScoresPartialPplnsWindows()
    {
        var target = ErgoExactDifficultyTests.Target;
        var network = ErgoExactDifficultyTests.Exact(target);
        var rounded = new Target(target).Difficulty;
        Assert.True(rounded > network);
        // With the old rounded network metadata, this half-window ordinary
        // share receives less than 25. Even capping candidates alone cannot fix it.
        Assert.True(25m - (decimal) (network / 2 / rounded) * 50m > .000001m);
        var candidate = ErgoExactDifficultyTests.Admit(target, target - 1, network * 200, "candidate");
        var ordinary = ErgoExactDifficultyTests.Admit(target, target * 2, network / 2, "ordinary");
        Assert.True(candidate.IsBlockCandidate);
        Assert.False(ordinary.IsBlockCandidate);
        var normalization = (ErgoPayoutHandler) RuntimeHelpers.GetUninitializedObject(typeof(ErgoPayoutHandler));
        Assert.Equal(network, normalization.AdjustShareDifficulty(candidate.Difficulty));
        Assert.Equal(network / 2, normalization.AdjustShareDifficulty(ordinary.Difficulty));

        var schema = $"ergo_precision_{Guid.NewGuid():N}";
        var cs = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")) { SearchPath = schema, Pooling = false };
        await using var connection = new NpgsqlConnection(cs.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await connection.ExecuteAsync($"CREATE SCHEMA {schema}");
            await connection.ExecuteAsync(@"CREATE TABLE shares(
                poolid text NOT NULL, blockheight bigint NOT NULL, difficulty double precision NOT NULL,
                networkdifficulty double precision NOT NULL, sharedifficulty double precision NULL,
                actualdifficulty double precision NULL, miner text NOT NULL, worker text NULL,
                useragent text NULL, ipaddress text NOT NULL, source text NULL, sessionid text NULL, created timestamptz NOT NULL);
                CREATE TABLE balances(poolid text NOT NULL, address text NOT NULL, amount decimal(28,12) NOT NULL DEFAULT 0,
                    created timestamptz NOT NULL, updated timestamptz NOT NULL, PRIMARY KEY(poolid,address));
                CREATE TABLE balance_changes(id bigserial PRIMARY KEY, poolid text NOT NULL, address text NOT NULL,
                    amount decimal(28,12) NOT NULL, usage text NULL, tags text[] NULL, created timestamptz NOT NULL);");
            await connection.ExecuteAsync((await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_share_accounting.sql")))
                .Replace("\\set ON_ERROR_STOP on", string.Empty, StringComparison.Ordinal));
            var mapper = AutoMapperFactory.CreateMapper();
            var shares = new ShareRepository(mapper);
            var factory = new PgConnectionFactory(cs.ConnectionString);
            var balances = new BalanceRepository(mapper);
            foreach(var payout in new[] { PayoutScheme.PPLNS, PayoutScheme.PPLNSBF })
            {
                var id = "ergo_exact_" + payout;
                var created = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
                var batches = new ShareAccountingBatch[2];
                for(var i = 0; i < 2; i++)
                {
                    var verified = i == 0 ? candidate : ordinary;
                    var accountingId = Guid.NewGuid();
                    // The newer ordinary share scores 1/2; the older candidate
                    // contributes only the remaining 1/2 of a one-network window.
                    var share = new Share { PoolId = id, BlockHeight = 100, Miner = i == 0 ? "candidate" : "ordinary",
                        Difficulty = verified.Difficulty, NetworkDifficulty = verified.NetworkDifficulty,
                        IpAddress = "127.0.0.1", AccountingId = accountingId, AccountingRole = 1,
                        RewardBasisSatoshis = 5_000_000_000, Created = created.AddSeconds(i - 2) };
                    batches[i] = new ShareAccountingBatch { AccountingId = accountingId, Created = share.Created,
                        Shares = new[] { share }, PpsCredits = Array.Empty<PpsShareCredit>(),
                        PayloadHash = ShareAccounting.ComputePayloadHash(accountingId, new[] { share }, Array.Empty<PpsShareCredit>()) };
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        Assert.Equal(ShareAccountingInsertResult.Inserted, await shares.InsertAccountingBatchAsync(connection, tx, batches[i], CancellationToken.None));
                        await tx.RollbackAsync();
                    }
                    Assert.Equal(i, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE poolid=@id", new { id }));
                    await using(var tx = await connection.BeginTransactionAsync())
                    {
                        Assert.Equal(ShareAccountingInsertResult.Inserted, await shares.InsertAccountingBatchAsync(connection, tx, batches[i], CancellationToken.None));
                        await tx.CommitAsync();
                    }
                }
                await using(var tx = await connection.BeginTransactionAsync())
                {
                    foreach(var batch in batches)
                        Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await shares.InsertAccountingBatchAsync(connection, tx, batch, CancellationToken.None));
                    await tx.CommitAsync();
                }
                var persisted = await connection.QueryAsync<double>("SELECT networkdifficulty FROM shares WHERE poolid=@id", new { id });
                Assert.All(persisted, value => Assert.Equal(network, value));
                Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares WHERE poolid=@id", new { id }));
                var pool = Substitute.For<IMiningPool>();
                pool.Config.Returns(new PoolConfig { Id = id, PaymentProcessing = new PoolPaymentProcessingConfig
                    { PayoutScheme = payout, PayoutSchemeConfig = JObject.FromObject(new { Factor = 1, BlockFinderPercentage = 5 }) } });
                IPayoutScheme scheme = payout == PayoutScheme.PPLNS
                    ? new PPLNSPaymentScheme(factory, shares, Substitute.For<IBlockRepository>(), balances)
                    : new PPLNSBFPaymentScheme(factory, shares, Substitute.For<IBlockRepository>(), balances);
                var block = new Block { PoolId = id, BlockHeight = 100, Miner = "candidate", Created = created };
                await using(var tx = await connection.BeginTransactionAsync())
                {
                    await scheme.UpdateBalancesAsync(connection, tx, pool, normalization, block, 50m, CancellationToken.None);
                    await tx.RollbackAsync();
                }
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM balances WHERE poolid=@id", new { id }));
                await using(var tx = await connection.BeginTransactionAsync())
                {
                    await scheme.UpdateBalancesAsync(connection, tx, pool, normalization, block, 50m, CancellationToken.None);
                    await tx.CommitAsync();
                }
                Assert.Equal(payout == PayoutScheme.PPLNS ? 25m : 26.25m, await balances.GetBalanceAsync(connection, id, "candidate"));
                Assert.Equal(payout == PayoutScheme.PPLNS ? 25m : 23.75m, await balances.GetBalanceAsync(connection, id, "ordinary"));
                Assert.Equal(50m, await connection.ExecuteScalarAsync<decimal>("SELECT sum(amount) FROM balances WHERE poolid=@id", new { id }));
            }
        }
        finally { await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE"); }
    }
}
