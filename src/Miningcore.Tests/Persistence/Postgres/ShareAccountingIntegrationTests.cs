using System;
using System.Data;
using System.IO;
using System.Linq;
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
    public async Task OrdinaryBatch_RemainsCompatibleWithLegacySharesTable()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_legacy_share_{Guid.NewGuid():N}";
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
                    created timestamptz NOT NULL);");

            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            await using var transaction = await connection.BeginTransactionAsync();
            await repository.BatchInsertAsync(connection, transaction, new[]
            {
                new Share
                {
                    PoolId = "solo",
                    BlockHeight = 42,
                    Difficulty = 1,
                    NetworkDifficulty = 100,
                    Miner = "miner",
                    IpAddress = "127.0.0.1",
                    Created = DateTime.UtcNow,
                },
            }, CancellationToken.None);
            await transaction.CommitAsync();

            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE poolid='solo'"));
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task AccountingMigration_DoesNotRequireBlockSchema()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_accounting_migration_{Guid.NewGuid():N}";
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
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    address text NOT NULL, amount decimal(28,12) NOT NULL,
                    usage text NULL, tags text[] NULL,
                    created timestamptz NOT NULL);
            ");

            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql"));
            var migration = (await File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            Assert.False(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
            await connection.ExecuteAsync(migration);
            await connection.ExecuteAsync(migration);

            Assert.True(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
            Assert.Equal(1, await connection.ExecuteAsync(
                "DELETE FROM share_accounting_prune_state WHERE singletonid=1"));
            Assert.False(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_prune_state WHERE singletonid=1"));

            Assert.Equal(4, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename IN ('share_accounting_groups',
                      'share_accounting_prune_state', 'pps_share_credits',
                      'pps_credit_remainders')"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename = 'blocks'"));
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task PpsCalculatedAmount_UsesExactNumeric3824Boundary()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_pps_numeric_{Guid.NewGuid():N}";
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
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    address text NOT NULL, amount decimal(28,12) NOT NULL,
                    usage text NULL, tags text[] NULL,
                    created timestamptz NOT NULL);
            ");
            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql"));
            var migration = (await File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            await connection.ExecuteAsync(migration);

            const string insert = @"WITH accounting AS (
                    INSERT INTO share_accounting_groups(accountingid,
                        projectioncount, payloadhash, created)
                    VALUES(@id, 1, @hash, now()))
                INSERT INTO pps_share_credits(poolid, accountingid, address,
                    calculatedamount, creditedamount, difficulty,
                    networkdifficulty, rewardbasissatoshis, created)
                VALUES('ltc', @id, 'miner', @amount, 0, 1, 1, 100000000,
                    now())";

            await connection.ExecuteAsync(insert, new
            {
                id = Guid.NewGuid(),
                hash = new string('A', 64),
                amount = 99_999_999_999_999m,
            });
            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(insert, new
                {
                    id = Guid.NewGuid(),
                    hash = new string('B', 64),
                    amount = 100_000_000_000_000m,
                }));
            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(insert, new
                {
                    id = Guid.NewGuid(),
                    hash = new string('C', 64),
                    amount = 100_000_000_000_001m,
                }));
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

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
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*) FROM pg_tables
                WHERE schemaname = current_schema() AND tablename = 'blocks'"));
            Assert.Equal(4, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_tables
                WHERE schemaname = current_schema()
                  AND tablename IN ('share_accounting_groups',
                      'share_accounting_prune_state', 'pps_share_credits',
                      'pps_credit_remainders')
                  AND tableowner = (SELECT pg_get_userbyid(datdba)
                      FROM pg_database WHERE datname = current_database())"));

            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(@"INSERT INTO shares(poolid, blockheight,
                    difficulty, networkdifficulty, miner, ipaddress, accountingid,
                    created) VALUES('ltc', 1, 1, 1, 'miner', '127.0.0.1',
                    @id, now())", new { id = Guid.NewGuid() }));

            var referencedFirst = Guid.NewGuid();
            var referencedSecond = Guid.NewGuid();
            var removable = Guid.NewGuid();
            await connection.ExecuteAsync(@"
                INSERT INTO share_accounting_groups(accountingid,
                    projectioncount, payloadhash, created) VALUES
                    (@referencedFirst, 1, @firstHash, @created),
                    (@referencedSecond, 1, @secondHash, @created + interval '1 second'),
                    (@removable, 1, @thirdHash, @created + interval '2 seconds');
                INSERT INTO shares(poolid, blockheight, difficulty,
                    networkdifficulty, miner, ipaddress, accountingid,
                    accountingrole, rewardbasissatoshis, created)
                VALUES
                    ('ltc', 1, 1, 1, 'retained-first', '127.0.0.1',
                        @referencedFirst, 1, 100000000, @created),
                    ('ltc', 1, 1, 1, 'retained-second', '127.0.0.1',
                        @referencedSecond, 1, 100000000,
                        @created + interval '1 second');", new
            {
                referencedFirst,
                referencedSecond,
                removable,
                firstHash = new string('1', 64),
                secondHash = new string('2', 64),
                thirdHash = new string('3', 64),
                created = new DateTime(2020, 1, 1, 0, 0, 0,
                    DateTimeKind.Utc),
            });
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                await connection.ExecuteAsync("SET LOCAL enable_seqscan=off",
                    transaction: transaction);
                var plan = (await connection.QueryAsync<string>(
                    new CommandDefinition(@"EXPLAIN (COSTS OFF)
                        SELECT accountingid, created
                        FROM share_accounting_groups
                        WHERE created <= @before
                          AND (created, accountingid) >
                              (@cursorCreated, @cursorAccountingId)
                        ORDER BY created, accountingid
                        LIMIT @candidateScanSize", new
                    {
                        before = new DateTime(2020, 1, 2, 0, 0, 0,
                            DateTimeKind.Utc),
                        cursorCreated = new DateTime(2020, 1, 1, 0, 0, 0,
                            DateTimeKind.Utc),
                        cursorAccountingId = referencedFirst,
                        candidateScanSize = 3,
                    }, transaction))).ToArray();
                Assert.Contains(plan, line => line.Contains(
                    "idx_share_accounting_groups_prune",
                    StringComparison.OrdinalIgnoreCase));
                Assert.Contains(plan, line => line.Contains("Index Cond:",
                    StringComparison.Ordinal));
                Assert.DoesNotContain(plan, line => line.Contains("Join Filter:",
                    StringComparison.Ordinal));
                await transaction.RollbackAsync();
            }
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune = await repository.PruneShareAccountingEvidenceBeforeAsync(
                    connection, transaction,
                    new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), 2,
                    CancellationToken.None);
                Assert.Equal(0, prune.PrunedRows);
                Assert.True(prune.HasMore);
                await transaction.CommitAsync();
            }
            Assert.Equal(referencedSecond,
                await connection.ExecuteScalarAsync<Guid?>(
                    "SELECT cursoraccountingid FROM share_accounting_prune_state " +
                    "WHERE singletonid=1"));
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune = await repository.PruneShareAccountingEvidenceBeforeAsync(
                    connection, transaction,
                    new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), 2,
                    CancellationToken.None);
                Assert.Equal(1, prune.PrunedRows);
                Assert.False(prune.HasMore);
                await transaction.CommitAsync();
            }
            Assert.Null(await connection.ExecuteScalarAsync<Guid?>(
                "SELECT cursoraccountingid FROM share_accounting_prune_state " +
                "WHERE singletonid=1"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_groups " +
                "WHERE accountingid=@removable", new { removable }));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_groups " +
                "WHERE accountingid IN (@referencedFirst, @referencedSecond)",
                new { referencedFirst, referencedSecond }));

            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid IN (@referencedFirst, @referencedSecond)",
                new { referencedFirst, referencedSecond });
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune = await repository.PruneShareAccountingEvidenceBeforeAsync(
                    connection, transaction,
                    new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc), 2,
                    CancellationToken.None);
                Assert.Equal(2, prune.PrunedRows);
                Assert.False(prune.HasMore);
                await transaction.CommitAsync();
            }

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

            var oversized = CreateBatch(Guid.NewGuid(), 0.25m, 'E');
            oversized = oversized with
            {
                Shares = new[]
                {
                    oversized.Shares[0] with { BlockHeight = ulong.MaxValue },
                },
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, oversized));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_groups WHERE accountingid=@id",
                new { id = oversized.AccountingId }));

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
            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
                await InsertAsync(repository, connection, prunedPair));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE accountingid=@id",
                new { id = prunedPair.AccountingId }));
            await connection.ExecuteAsync(
                "UPDATE shares SET miner='tampered' WHERE accountingid=@id",
                new { id = prunedPair.AccountingId });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, prunedPair));
            await connection.ExecuteAsync(
                "UPDATE shares SET miner='miner' WHERE accountingid=@id",
                new { id = prunedPair.AccountingId });
            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid=@id",
                new { id = prunedPair.AccountingId });
            Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted,
                await InsertAsync(repository, connection, prunedPair));

            var bulk = Enumerable.Range(0, 250).Select(index =>
            {
                var candidate = CreateBatch(Guid.NewGuid(), 0.001m, 'E');
                var share = candidate.Shares[0] with { Miner = "load-miner" };
                var credit = candidate.PpsCredits[0] with
                {
                    Address = "load-miner",
                };
                return candidate with
                {
                    Shares = new[] { share },
                    PpsCredits = new[] { credit },
                };
            }).ToArray();
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var outcomes = await repository.InsertAccountingBatchesAsync(
                    connection, transaction, bulk, CancellationToken.None);
                Assert.All(outcomes, outcome => Assert.Equal(
                    ShareAccountingInsertResult.Inserted, outcome));
                await transaction.CommitAsync();
            }
            Assert.Equal(250, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits WHERE address='load-miner'"));
            Assert.Equal(0.25m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT amount FROM balances WHERE poolid='ltc' AND address='load-miner'"));

            // Two recorder transactions contending for the same recipient must serialize on
            // the remainder row without deadlocking or losing either liability.
            await using(var secondConnection = new NpgsqlConnection(connectionString))
            {
                await secondConnection.OpenAsync();
                await secondConnection.ExecuteAsync(
                    $"SET search_path TO {schema}, public");
                var concurrentFirst = CreateBatch(Guid.NewGuid(), 0.01m, 'A');
                var concurrentSecond = CreateBatch(Guid.NewGuid(), 0.02m, 'B');
                await using var firstTransaction =
                    await connection.BeginTransactionAsync();
                await using var secondTransaction =
                    await secondConnection.BeginTransactionAsync();
                Assert.Equal(ShareAccountingInsertResult.Inserted,
                    await repository.InsertAccountingBatchAsync(connection,
                        firstTransaction, concurrentFirst,
                        CancellationToken.None));
                var contending = repository.InsertAccountingBatchAsync(
                    secondConnection, secondTransaction, concurrentSecond,
                    CancellationToken.None);
                await Task.Delay(100);
                Assert.False(contending.IsCompleted);
                await firstTransaction.CommitAsync();
                Assert.Equal(ShareAccountingInsertResult.Inserted,
                    await contending.WaitAsync(TimeSpan.FromSeconds(5)));
                await secondTransaction.CommitAsync();
            }
            Assert.Equal(0.280000000001m, await GetBalanceAsync(connection));

            var retained = CreateBatch(Guid.NewGuid(), 0.25m, 'F');
            var retainedCreated = new DateTime(2026, 8, 28, 12, 0, 0,
                DateTimeKind.Utc);
            retained = retained with
            {
                Created = retainedCreated,
                Shares = new[]
                {
                    retained.Shares[0] with
                    {
                        Miner = "retention-miner",
                        Created = retainedCreated,
                    },
                },
                PpsCredits = new[]
                {
                    retained.PpsCredits[0] with
                    {
                        Address = "retention-miner",
                        Created = retainedCreated,
                    },
                },
            };
            Assert.Equal(ShareAccountingInsertResult.Inserted,
                await InsertAsync(repository, connection, retained));
            await connection.ExecuteAsync(
                "DELETE FROM shares WHERE accountingid=@id",
                new { id = retained.AccountingId });
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune =
                    await repository.PruneShareAccountingEvidenceBeforeAsync(
                        connection, transaction, retained.Created.AddSeconds(1),
                        100, CancellationToken.None);
                Assert.Equal(3, prune.PrunedRows);
                Assert.False(prune.HasMore);
                await transaction.CommitAsync();
            }
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM share_accounting_groups WHERE accountingid=@id",
                new { id = retained.AccountingId }));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_share_credits WHERE accountingid=@id",
                new { id = retained.AccountingId }));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pps_credit_remainders " +
                "WHERE poolid='ltc' AND address='retention-miner'"));

            var expiredReplay = retained with
            {
                NewReceiptNotBefore = retained.Created.AddSeconds(1),
            };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InsertAsync(repository, connection, expiredReplay));
            Assert.Equal(0.25m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT amount FROM balances WHERE poolid='ltc' AND address='retention-miner'"));

            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var outcomes = await repository.InsertAccountingBatchesAsync(
                    connection, transaction, bulk, CancellationToken.None);
                Assert.All(outcomes, outcome => Assert.Equal(
                    ShareAccountingInsertResult.AlreadyCommitted, outcome));
                await transaction.CommitAsync();
            }
            Assert.Equal(0.25m, await connection.ExecuteScalarAsync<decimal>(
                "SELECT amount FROM balances WHERE poolid='ltc' AND address='load-miner'"));

            await connection.ExecuteAsync(@"
                INSERT INTO shares(poolid, blockheight, difficulty,
                    networkdifficulty, miner, ipaddress, created)
                SELECT 'pps-retention', value, 1, 1, 'miner', '127.0.0.1',
                    @created
                FROM generate_series(1, 3) value",
                new { created = DateTime.UtcNow.AddDays(-10) });
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune = await repository.PruneSharesBeforeInclusiveAsync(
                    connection, transaction, "pps-retention", DateTime.UtcNow,
                    2, CancellationToken.None);
                Assert.Equal(2, prune.PrunedRows);
                Assert.True(prune.HasMore);
                await transaction.CommitAsync();
            }
            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var prune = await repository.PruneSharesBeforeInclusiveAsync(
                    connection, transaction, "pps-retention", DateTime.UtcNow,
                    2, CancellationToken.None);
                Assert.Equal(1, prune.PrunedRows);
                Assert.False(prune.HasMore);
                await transaction.CommitAsync();
            }

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
