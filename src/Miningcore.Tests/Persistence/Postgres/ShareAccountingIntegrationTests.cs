using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class ShareAccountingIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AccountingBatch_IsAtomicIdempotentAndCarriesSubUnitRemainders()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_share_accounting_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE shares(
                    poolid text NOT NULL, blockheight bigint NOT NULL,
                    difficulty double precision NOT NULL,
                    networkdifficulty double precision NOT NULL,
                    sharedifficulty double precision NULL,
                    actualdifficulty double precision NULL,
                    miner text NOT NULL, worker text NULL, useragent text NULL,
                    ipaddress text NOT NULL, source text NULL, sessionid text NULL,
                    created timestamptz NOT NULL);
                CREATE TABLE balances(
                    poolid text NOT NULL, address text NOT NULL,
                    amount decimal(28,12) NOT NULL DEFAULT 0,
                    created timestamptz NOT NULL, updated timestamptz NOT NULL,
                    PRIMARY KEY(poolid, address));
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    address text NOT NULL, amount decimal(28,12) NOT NULL,
                    usage text NULL, tags text[] NULL, created timestamptz NOT NULL);
            ");

            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql"));
            var migration = (await File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            await connection.ExecuteAsync(migration);

            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            Assert.True(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename IN ('share_accounting_groups',
                      'pps_share_credits', 'pps_credit_remainders')
                  AND tableowner = (SELECT pg_get_userbyid(datdba)
                      FROM pg_database WHERE datname = current_database())"));

            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(@"INSERT INTO shares(poolid, blockheight,
                    difficulty, networkdifficulty, miner, ipaddress, accountingid,
                    created) VALUES('ltc', 1, 1, 1, 'miner', '127.0.0.1',
                    @id, now())", new { id = Guid.NewGuid() }));

            var first = CreateBatch(Guid.NewGuid(), 0.0000000000006m, 'A');
            Assert.Equal(ShareAccountingInsertResult.Inserted,
                await InsertAsync(repository, connection, first));
            Assert.Equal(0m, await GetBalanceAsync(connection));

            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
                await InsertAsync(repository, connection, first));
            Assert.Equal(0m, await GetBalanceAsync(connection));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares"));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits"));

            var second = CreateBatch(Guid.NewGuid(), 0.0000000000006m, 'B');
            Assert.Equal(ShareAccountingInsertResult.Inserted,
                await InsertAsync(repository, connection, second));
            Assert.Equal(0.000000000001m, await GetBalanceAsync(connection));
            Assert.Equal(0.0000000000002m,
                await connection.ExecuteScalarAsync<decimal>(
                    "SELECT amount FROM pps_credit_remainders WHERE poolid='ltc' AND address='miner'"));

            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid=@id",
                new { id = second.AccountingId });
            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
                await InsertAsync(repository, connection, second));
            Assert.Equal(0.000000000001m, await GetBalanceAsync(connection));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits WHERE accountingid=@id",
                new { id = second.AccountingId }));

            var conflict = first with { PayloadHash = new string('F', 64) };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, conflict));
            Assert.Equal(0.000000000001m, await GetBalanceAsync(connection));

            var partial = CreateBatch(Guid.NewGuid(), 0.25m, 'C');
            partial = partial with
            {
                Shares = new[]
                {
                    partial.Shares[0],
                    partial.Shares[0] with
                    {
                        PoolId = "doge",
                        Miner = null,
                        AccountingRole = 2,
                    },
                },
            };
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertAsync(repository, connection, partial));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_groups WHERE accountingid=@id",
                new { id = partial.AccountingId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE accountingid=@id",
                new { id = partial.AccountingId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits WHERE accountingid=@id",
                new { id = partial.AccountingId }));

            await connection.ExecuteAsync(
                "UPDATE shares SET miner='tampered' WHERE accountingid=@id",
                new { id = first.AccountingId });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, first));

            var prunedPair = CreatePairBatch(Guid.NewGuid(), 0.25m, 'D');
            Assert.Equal(ShareAccountingInsertResult.Inserted,
                await InsertAsync(repository, connection, prunedPair));
            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid=@id AND poolid='doge'",
                new { id = prunedPair.AccountingId });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, prunedPair));
            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid=@id",
                new { id = prunedPair.AccountingId });
            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
                await InsertAsync(repository, connection, prunedPair));

            await connection.ExecuteAsync(@"
                ALTER TABLE pps_credit_remainders
                DROP CONSTRAINT ck_pps_remainder_range");
            Assert.False(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static async Task<ShareAccountingInsertResult> InsertAsync(
        ShareRepository repository, NpgsqlConnection connection,
        ShareAccountingBatch batch)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted);
        var result = await repository.InsertAccountingBatchAsync(connection,
            transaction, batch, CancellationToken.None);
        await transaction.CommitAsync();
        return result;
    }

    private static Task<decimal> GetBalanceAsync(NpgsqlConnection connection) =>
        connection.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE((SELECT amount FROM balances WHERE poolid='ltc' AND address='miner'), 0)");

    private static ShareAccountingBatch CreateBatch(Guid id, decimal amount,
        char hashCharacter)
    {
        var created = new DateTime(2026, 8, 29, 12, 0, 0,
            DateTimeKind.Utc).AddTicks(id.GetHashCode() & 0xffff);
        var share = new Share
        {
            PoolId = "ltc",
            BlockHeight = 100,
            Miner = "miner",
            Worker = "rig",
            UserAgent = "miner/1",
            Difficulty = 1,
            ShareDifficulty = 2,
            ActualDifficulty = 2,
            NetworkDifficulty = 100,
            IpAddress = "127.0.0.1",
            Source = "test",
            SessionId = "session",
            AccountingId = id,
            AccountingRole = 1,
            RewardBasisSatoshis = 5_000_000_000,
            Created = created,
        };
        return new ShareAccountingBatch
        {
            AccountingId = id,
            PayloadHash = new string(hashCharacter, 64),
            Shares = new[] { share },
            PpsCredits = new[]
            {
                new PpsShareCredit
                {
                    PoolId = share.PoolId,
                    AccountingId = id,
                    Address = share.Miner,
                    CalculatedAmount = amount,
                    Difficulty = share.Difficulty,
                    NetworkDifficulty = share.NetworkDifficulty,
                    RewardBasisSatoshis = share.RewardBasisSatoshis.Value,
                    Created = created,
                },
            },
            Created = created,
        };
    }

    private static ShareAccountingBatch CreatePairBatch(Guid id, decimal amount,
        char hashCharacter)
    {
        var batch = CreateBatch(id, amount, hashCharacter);
        return batch with
        {
            Shares = new[]
            {
                batch.Shares[0],
                batch.Shares[0] with
                {
                    PoolId = "doge",
                    Miner = "doge-miner",
                    AccountingRole = 2,
                },
            },
        };
    }
}
