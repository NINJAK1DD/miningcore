using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.BitcoinBlake2b;

internal sealed class BitcoinBlake2bLedgerIntegrationFactAttribute : FactAttribute
{
    public BitcoinBlake2bLedgerIntegrationFactAttribute()
    {
        if(!File.Exists(Environment.GetEnvironmentVariable(BitcoinBlake2bIntegrationFactAttribute.BinaryEnvironmentVariable)) ||
           string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")))
            Skip = "Set MININGCORE_TEST_BLAKE2B_BITCOIND and MININGCORE_TEST_POSTGRES for the daemon-to-ledger test";
    }
}

// Each run owns a unique schema. Neither setup nor cleanup touches the lab's
// existing tables; CI supplies its disposable PostgreSQL service instead.
internal sealed class BitcoinBlake2bLedgerProbe : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly string schema = $"miningcore_blake2b_{Guid.NewGuid():N}";

    private BitcoinBlake2bLedgerProbe() => connection = new NpgsqlConnection(
        Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));

    internal static async Task<BitcoinBlake2bLedgerProbe> CreateAsync()
    {
        var probe = new BitcoinBlake2bLedgerProbe();
        await probe.connection.OpenAsync();
        try
        {
            await probe.connection.ExecuteAsync($@"
                CREATE SCHEMA {probe.schema}; SET search_path TO {probe.schema}, public;
                CREATE TABLE shares(
                    poolid text NOT NULL, blockheight bigint NOT NULL,
                    difficulty double precision NOT NULL, networkdifficulty double precision NOT NULL,
                    sharedifficulty double precision NULL, actualdifficulty double precision NULL,
                    miner text NOT NULL, worker text NULL, useragent text NULL,
                    ipaddress text NOT NULL, source text NULL, sessionid text NULL,
                    created timestamptz NOT NULL);
                CREATE TABLE balances(
                    poolid text NOT NULL, address text NOT NULL, amount decimal(28,12) NOT NULL DEFAULT 0,
                    created timestamptz NOT NULL, updated timestamptz NOT NULL, PRIMARY KEY(poolid,address));
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL, address text NOT NULL,
                    amount decimal(28,12) NOT NULL, usage text NULL, tags text[] NULL, created timestamptz NOT NULL);");
            var migration = await File.ReadAllTextAsync(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql")));
            await probe.connection.ExecuteAsync(migration.Replace("\\set ON_ERROR_STOP on", string.Empty, StringComparison.Ordinal));
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    internal async Task AssertPpsPersistenceAsync(Miningcore.Blockchain.Share share, PoolConfig pool)
    {
        Assert.Equal(CoinFamily.BitcoinBlake2b, pool.Template.Family);
        var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
        Assert.True(await repository.HasShareAccountingSchemaAsync(connection, CancellationToken.None));
        var mapped = new[] { ShareAccounting.ToPersistenceShare(share) };
        var credits = new[] { ShareAccounting.CreatePpsCredit(pool, share) };
        var id = ShareAccounting.ParseCanonicalId(share.AccountingId);
        var batch = new ShareAccountingBatch
        {
            AccountingId = id, Shares = mapped, PpsCredits = credits, Created = share.Created,
            PayloadHash = ShareAccounting.ComputePayloadHash(id, mapped, credits),
            NewReceiptNotBefore = DateTime.UtcNow.AddDays(-1),
        };
        Assert.Equal(ShareAccountingInsertResult.Inserted, await InsertAsync(repository, batch));
        Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await InsertAsync(repository, batch));
        var amount = credits[0].CalculatedAmount;
        var credited = decimal.Truncate(amount * 1_000_000_000_000m) / 1_000_000_000_000m;
        Assert.Equal(amount, await connection.ExecuteScalarAsync<decimal>("SELECT calculatedamount FROM pps_share_credits"));
        Assert.Equal(credited, await connection.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(amount - credited, await connection.ExecuteScalarAsync<decimal>("SELECT amount FROM pps_credit_remainders"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM shares"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM balance_changes"));

        // A changed financial payload cannot reinterpret an already committed
        // proof, even if its replacement digest is internally consistent.
        var changedCredits = new[] { credits[0] with { CalculatedAmount = amount / 2 } };
        var changed = batch with
        {
            PpsCredits = changedCredits,
            PayloadHash = ShareAccounting.ComputePayloadHash(id, mapped, changedCredits),
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => InsertAsync(repository, changed));
        Assert.Equal(credited, await connection.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
    }

    internal async Task AssertSchemeSettlementAsync(Miningcore.Blockchain.Share share, PoolConfig config,
        Miningcore.Persistence.Model.Block block, IPayoutHandler handler)
    {
        var mapper = AutoMapperFactory.CreateMapper();
        var shares = new ShareRepository(mapper);
        var balances = new BalanceRepository(mapper);
        var blocks = new BlockRepository(mapper);
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"))
            { SearchPath = schema + ",public" };
        var cf = new PgConnectionFactory(builder.ConnectionString);
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        var payoutScheme = config.PaymentProcessing.PayoutScheme;
        if(payoutScheme != PayoutScheme.PPS)
        {
            await using var insertion = await connection.BeginTransactionAsync();
            await shares.BatchInsertAsync(connection, insertion,
                new[] { ShareAccounting.ToPersistenceShare(share) }, CancellationToken.None);
            await insertion.CommitAsync();
        }
        IPayoutScheme scheme = payoutScheme switch
        {
            PayoutScheme.SOLO => new SOLOPaymentScheme(shares, balances),
            PayoutScheme.PROP => new PROPPaymentScheme(cf, shares, blocks, balances),
            PayoutScheme.PPLNS => new PPLNSPaymentScheme(cf, shares, blocks, balances),
            PayoutScheme.PPS => new PPSPaymentScheme(shares),
            _ => throw new InvalidOperationException(),
        };
        var before = await balances.GetBalanceAsync(connection, config.Id, share.Miner);
        await using(var transaction = await connection.BeginTransactionAsync())
        {
            await scheme.UpdateBalancesAsync(connection, transaction, pool, handler, block, 49m, CancellationToken.None);
            await transaction.CommitAsync();
        }
        var after = await balances.GetBalanceAsync(connection, config.Id, share.Miner);
        Assert.Equal(payoutScheme == PayoutScheme.PPS ? before : 49m, after);
        if(payoutScheme == PayoutScheme.PPS)
        {
            Assert.True(before > 0);
            block.Status = Miningcore.Persistence.Model.BlockStatus.Orphaned;
            await using var transaction = await connection.BeginTransactionAsync();
            await scheme.UpdateBalancesAsync(connection, transaction, pool, handler, block, 0m, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Equal(before, await balances.GetBalanceAsync(connection, config.Id, share.Miner));
        }
    }

    private async Task<ShareAccountingInsertResult> InsertAsync(ShareRepository repository, ShareAccountingBatch batch)
    {
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var result = await repository.InsertAccountingBatchAsync(connection, transaction, batch, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
        finally { await connection.DisposeAsync(); }
    }
}
