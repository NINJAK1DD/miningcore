using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class SharePartitionIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task PartitionAppendix_RebuildsParentTransactionally()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_share_appendix_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE shares(poolid text NOT NULL);
            ");

            var scriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/createdb_postgresql_11_appendix.sql"));
            var script = await System.IO.File.ReadAllTextAsync(scriptPath);

            // Dapper executes SQL rather than psql meta-commands. Removing this one directive
            // lets CI exercise the exact transaction, role switch, table and index definitions.
            script = script.Replace("\\set ON_ERROR_STOP on", string.Empty,
                StringComparison.Ordinal);
            await connection.ExecuteAsync(script);

            Assert.Equal("p", await connection.ExecuteScalarAsync<string>(
                "SELECT relkind::text FROM pg_class WHERE oid = to_regclass('shares')"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_inherits
                WHERE inhparent = to_regclass('shares')"));
            Assert.Equal(9, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*)
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND tablename = 'shares'"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*) FROM pg_constraint
                WHERE conrelid = to_regclass('shares')
                  AND conname = 'fk_shares_accounting_group'"));

            await connection.ExecuteAsync(@"
                CREATE TABLE balance_changes(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    address text NOT NULL, amount decimal(28,12) NOT NULL,
                    usage text NULL, tags text[] NULL,
                    created timestamptz NOT NULL);
            ");
            var migrationPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory,
                    "../../../../Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql"));
            var migration = (await System.IO.File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            await connection.ExecuteAsync(migration);
            await connection.ExecuteAsync(script);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(@"
                SELECT count(*) FROM pg_constraint
                WHERE conrelid = to_regclass('shares')
                  AND conname = 'fk_shares_accounting_group'
                  AND contype = 'f' AND convalidated"));
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            Assert.True(await repository.HasShareAccountingSchemaAsync(connection,
                CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync(
                "ROLLBACK; RESET ROLE; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task PartitionedPrune_ConfinesCtidCandidatesToRequestedPool()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_share_prune_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE shares(
                    poolid text NOT NULL, created timestamptz NOT NULL)
                    PARTITION BY LIST(poolid);
                CREATE TABLE shares_ltc PARTITION OF shares
                    FOR VALUES IN ('ltc');
                CREATE TABLE shares_doge PARTITION OF shares
                    FOR VALUES IN ('doge');
                INSERT INTO shares(poolid, created)
                    SELECT 'ltc', now() - interval '30 days'
                    FROM generate_series(1, 4);
                INSERT INTO shares(poolid, created)
                    SELECT 'doge', now()
                    FROM generate_series(1, 4);
            ");
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());

            await using(var transaction = await connection.BeginTransactionAsync())
            {
                var result = await repository.PruneSharesBeforeInclusiveAsync(
                    connection, transaction, "ltc", DateTime.UtcNow.AddDays(-7),
                    3, CancellationToken.None);
                Assert.Equal(3, result.PrunedRows);
                Assert.True(result.HasMore);
                await transaction.CommitAsync();
            }

            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE poolid='ltc'"));
            Assert.Equal(4, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares WHERE poolid='doge'"));
        }
        finally
        {
            await connection.ExecuteAsync("ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task Preflight_DetectsMissingListPartitionsAndAcceptsDefaultPartition()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_share_partition_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE shares(poolid text NOT NULL);
            ");

            var mapper = AutoMapperFactory.CreateMapper();
            var repository = new ShareRepository(mapper);
            var configuredPools = new[]
            {
                "btc1-solo",
                "ltc1-solo",
                "ltc1",
                "doge1-solo",
                "quote'pool",
            };

            // The normal, unpartitioned schema needs no per-pool child tables.
            Assert.Empty(await repository.GetMissingSharePartitionsAsync(connection,
                configuredPools, CancellationToken.None));

            await connection.ExecuteAsync(@"
                DROP TABLE shares;
                CREATE TABLE shares(poolid text NOT NULL) PARTITION BY LIST(poolid);
                CREATE TABLE shares_btc_ltc PARTITION OF shares
                    FOR VALUES IN ('btc1-solo', 'ltc1-solo', 'quote''pool');
            ");

            // Bounds are matched as quoted SQL literals, not unsafe parameters or substrings.
            Assert.Equal(new[] { "doge1-solo", "ltc1" },
                await repository.GetMissingSharePartitionsAsync(connection,
                    configuredPools, CancellationToken.None));

            // Matching the exact partition bound must also reflect actual PostgreSQL routing.
            await connection.ExecuteAsync(
                "INSERT INTO shares(poolid) VALUES" +
                "('btc1-solo'), ('ltc1-solo'), ('quote''pool')");
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares"));

            await connection.ExecuteAsync(@"
                CREATE TABLE shares_other PARTITION OF shares DEFAULT;
                INSERT INTO shares(poolid) VALUES('doge1-solo'), ('ltc1');
            ");

            Assert.Empty(await repository.GetMissingSharePartitionsAsync(connection,
                configuredPools, CancellationToken.None));
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM shares"));
        }
        finally
        {
            await connection.ExecuteAsync("SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
