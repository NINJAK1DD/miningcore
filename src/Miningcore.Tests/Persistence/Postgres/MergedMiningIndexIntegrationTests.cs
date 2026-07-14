using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class MergedMiningIndexIntegrationTests
{
    [Fact]
    public async Task PayoutOwnershipMigrationAndPreflight_RejectDeferrablePaymentBatchKey()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(connectionString))
            return;

        var schema = $"miningcore_payout_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE payments(
                    poolid text NOT NULL,
                    transactionconfirmationdata text NOT NULL,
                    created timestamptz NOT NULL);
                CREATE TABLE payment_batches(
                    poolid text NOT NULL,
                    transactionconfirmationdata text NOT NULL,
                    created timestamptz NOT NULL,
                    PRIMARY KEY(poolid, transactionconfirmationdata)
                        DEFERRABLE INITIALLY IMMEDIATE);
            ");

            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql"));
            var migration = await File.ReadAllTextAsync(migrationPath);
            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(migration));
            await connection.ExecuteAsync("ROLLBACK");

            // The migration is transactional: rejecting the malformed arbiter must not leave
            // a partially installed ownership table behind.
            Assert.Null(await connection.ExecuteScalarAsync<uint?>(
                $"SELECT to_regclass('{schema}.payout_manager_ownership')::oid"));

            await connection.ExecuteAsync(@"
                CREATE TABLE payout_manager_ownership(
                    id smallint NOT NULL PRIMARY KEY CHECK(id = 1),
                    generation bigint NOT NULL DEFAULT 0,
                    owner_id uuid NULL,
                    owner_host text NULL,
                    owner_process_id int NULL,
                    acquired timestamptz NULL,
                    released timestamptz NULL);
                INSERT INTO payout_manager_ownership(id) VALUES(1);
            ");

            var leaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = $"{schema},public",
            }.ConnectionString;
            await using var lease = new PostgresPayoutManagerLease(
                new PgConnectionFactory(leaseConnectionString));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                lease.TryAcquireAsync(CancellationToken.None));
            Assert.Contains("payment-batch idempotency schema", error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Null(await connection.ExecuteScalarAsync<Guid?>(
                "SELECT owner_id FROM payout_manager_ownership WHERE id = 1"));
        }
        finally
        {
            await connection.ExecuteAsync("SET search_path TO public; ROLLBACK");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    [Fact]
    public async Task Migration_DropsOnlyIndexesOwnedByResolvedBlocksSchema()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        if(string.IsNullOrWhiteSpace(connectionString))
            return;

        var suffix = Guid.NewGuid().ToString("N");
        var shadowSchema = $"miningcore_shadow_{suffix}";
        var targetSchema = $"miningcore_target_{suffix}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {shadowSchema};
                CREATE SCHEMA {targetSchema};
                CREATE TABLE {shadowSchema}.unrelated(
                    poolid text, hash text, transactionconfirmationdata text);
                CREATE INDEX idx_blocks_auxpow_pool_hash
                    ON {shadowSchema}.unrelated(poolid, hash);
                CREATE INDEX idx_blocks_auxpow_claim
                    ON {shadowSchema}.unrelated(poolid, hash, transactionconfirmationdata);
                CREATE INDEX idx_blocks_merged_parent_pool_hash
                    ON {shadowSchema}.unrelated(hash, poolid);

                CREATE TABLE {targetSchema}.blocks(
                    poolid text NOT NULL,
                    hash text NOT NULL,
                    type text,
                    transactionconfirmationdata text NOT NULL DEFAULT '');
                CREATE INDEX idx_blocks_auxpow_pool_hash
                    ON {targetSchema}.blocks(hash, poolid);
                CREATE INDEX idx_blocks_auxpow_claim
                    ON {targetSchema}.blocks(poolid, transactionconfirmationdata);
                CREATE INDEX idx_blocks_merged_parent_pool_hash
                    ON {targetSchema}.blocks(hash);
                SET search_path TO {shadowSchema}, {targetSchema}, public;
            ");

            var migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql"));
            await connection.ExecuteAsync(await File.ReadAllTextAsync(migrationPath));

            var mapper = new MapperConfiguration(cfg =>
                    cfg.AddProfile(new AutoMapperProfile()))
                .CreateMapper();
            var repository = new BlockRepository(mapper);
            Assert.True(await repository.HasMergedMiningBlockIndexesAsync(connection,
                CancellationToken.None));

            foreach(var indexName in new[]
                    {
                        "idx_blocks_auxpow_pool_hash",
                        "idx_blocks_auxpow_claim",
                        "idx_blocks_merged_parent_pool_hash",
                    })
            {
                Assert.NotNull(await connection.ExecuteScalarAsync<uint?>($@"
                    SELECT to_regclass('{shadowSchema}.{indexName}')::oid"));
                Assert.NotNull(await connection.ExecuteScalarAsync<uint?>($@"
                    SELECT to_regclass('{targetSchema}.{indexName}')::oid"));
            }
        }

        finally
        {
            await connection.ExecuteAsync("SET search_path TO public");
            await connection.ExecuteAsync(
                $"DROP SCHEMA IF EXISTS {shadowSchema} CASCADE; " +
                $"DROP SCHEMA IF EXISTS {targetSchema} CASCADE;");
        }
    }

    [Fact]
    public async Task Preflight_UsesBlocksResolvedByCurrentSearchPath()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");

        // The GitHub workflow supplies PostgreSQL. Local runs remain self-contained when an
        // integration database has not been explicitly provided.
        if(string.IsNullOrWhiteSpace(connectionString))
            return;

        var suffix = Guid.NewGuid().ToString("N");
        var validSchema = $"miningcore_valid_{suffix}";
        var staleSchema = $"miningcore_stale_{suffix}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {validSchema};
                CREATE SCHEMA {staleSchema};

                CREATE TABLE {validSchema}.blocks(
                    poolid text NOT NULL,
                    hash text NOT NULL,
                    type text,
                    status text,
                    transactionconfirmationdata text NOT NULL DEFAULT ''
                );
                CREATE TABLE {validSchema}.shares(
                    poolid text NOT NULL,
                    difficulty double precision NOT NULL,
                    networkdifficulty double precision NOT NULL,
                    miner text NOT NULL,
                    created timestamptz NOT NULL
                );
                CREATE UNIQUE INDEX idx_blocks_auxpow_pool_hash
                    ON {validSchema}.blocks(poolid, hash) WHERE type = 'auxpow';
                CREATE UNIQUE INDEX idx_blocks_auxpow_claim
                    ON {validSchema}.blocks(poolid, hash,
                        (regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')))
                    WHERE type = 'auxpow-claim';
                CREATE UNIQUE INDEX idx_blocks_merged_parent_pool_hash
                    ON {validSchema}.blocks(poolid, hash)
                    WHERE type IN ('merged-parent', 'merged-parent-uncertain');

                CREATE TABLE {staleSchema}.blocks(
                    poolid text NOT NULL,
                    hash text NOT NULL,
                    type text,
                    status text,
                    transactionconfirmationdata text NOT NULL DEFAULT ''
                );
                CREATE INDEX idx_blocks_auxpow_pool_hash
                    ON {staleSchema}.blocks(poolid, hash);
                CREATE INDEX idx_blocks_auxpow_claim
                    ON {staleSchema}.blocks(poolid, hash, transactionconfirmationdata);
                CREATE INDEX idx_blocks_merged_parent_pool_hash
                    ON {staleSchema}.blocks(poolid, hash);
            ");

            var mapper = new MapperConfiguration(cfg =>
                    cfg.AddProfile(new AutoMapperProfile()))
                .CreateMapper();
            var repository = new BlockRepository(mapper);

            await connection.ExecuteAsync(
                $"SET search_path TO {validSchema}, {staleSchema}, public");
            var initialPreflight = await repository.HasMergedMiningBlockIndexesAsync(
                connection, CancellationToken.None);
            var indexDetails = await connection.QueryAsync<string>(@"
                SELECT index_class.relname || ': keys=' ||
                    ARRAY(
                        SELECT lower(regexp_replace(
                            pg_get_indexdef(index_class.oid, key_position, true),
                            '\s+', ' ', 'g'))
                        FROM generate_series(1, i.indnkeyatts) key_position
                        ORDER BY key_position
                    )::text || '; predicate=' ||
                    lower(regexp_replace(pg_get_expr(i.indpred, i.indrelid, true),
                        '\s+', ' ', 'g'))
                FROM pg_index i
                JOIN pg_class index_class ON index_class.oid = i.indexrelid
                WHERE i.indrelid = to_regclass('blocks')
                ORDER BY index_class.relname");
            Assert.True(initialPreflight, string.Join(Environment.NewLine, indexDetails));

            async Task AssertMalformedAuxPowIndexRejectedAsync(string keyExpression,
                string predicate)
            {
                await connection.ExecuteAsync($@"
                    DROP INDEX {validSchema}.idx_blocks_auxpow_pool_hash;
                    CREATE UNIQUE INDEX idx_blocks_auxpow_pool_hash
                        ON {validSchema}.blocks({keyExpression})
                        WHERE {predicate};
                ");
                Assert.False(await repository.HasMergedMiningBlockIndexesAsync(connection,
                    CancellationToken.None));
                await connection.ExecuteAsync($@"
                    DROP INDEX {validSchema}.idx_blocks_auxpow_pool_hash;
                    CREATE UNIQUE INDEX idx_blocks_auxpow_pool_hash
                        ON {validSchema}.blocks(poolid, hash)
                        WHERE type = 'auxpow';
                ");
                Assert.True(await repository.HasMergedMiningBlockIndexesAsync(connection,
                    CancellationToken.None));
            }

            await AssertMalformedAuxPowIndexRejectedAsync("lower(poolid), hash",
                "type = 'auxpow'");
            await AssertMalformedAuxPowIndexRejectedAsync("hash, poolid",
                "type = 'auxpow'");
            await AssertMalformedAuxPowIndexRejectedAsync("poolid, lower(hash)",
                "type = 'auxpow'");
            await AssertMalformedAuxPowIndexRejectedAsync("poolid, hash",
                "type = 'auxpow' AND status = 'pending'");

            var blockTime = DateTime.UtcNow;
            var directParent = new Block
            {
                PoolId = "ltc-solo",
                Type = "merged-parent",
                Created = blockTime,
            };
            Assert.True(PayoutManager.ShouldDeferMergedParentShareSettlement(
                directParent, blockTime.AddSeconds(30)));
            await connection.ExecuteAsync($@"
                INSERT INTO {validSchema}.shares(
                    poolid, difficulty, networkdifficulty, miner, created)
                VALUES('ltc-solo', 1, 1, 'miner', @created)",
                new { created = blockTime.AddSeconds(-1) });
            var shareRepository = new ShareRepository(mapper);
            Assert.Equal(1d, await shareRepository
                .GetEffectiveAccumulatedShareDifficultyBetweenAsync(connection,
                    "ltc-solo", blockTime.AddMinutes(-1), blockTime,
                    CancellationToken.None));

            // Model the paired winning share arriving after the synchronous block row. Its
            // originating timestamp equals the block boundary and must be included once the
            // direct/relay share-settlement delay has elapsed.
            await connection.ExecuteAsync($@"
                INSERT INTO {validSchema}.shares(
                    poolid, difficulty, networkdifficulty, miner, created)
                VALUES('ltc-solo', 1, 1, 'miner', @created)",
                new { created = blockTime });
            Assert.Equal(2d, await shareRepository
                .GetEffectiveAccumulatedShareDifficultyBetweenAsync(connection,
                    "ltc-solo", blockTime.AddMinutes(-1), blockTime,
                    CancellationToken.None));
            Assert.False(PayoutManager.ShouldDeferMergedParentShareSettlement(
                directParent,
                blockTime.Add(PayoutManager.MergedParentShareSettlementDelay)));

            // Identically named valid indexes still exist, but unqualified runtime SQL now
            // resolves blocks to the stale relation and the preflight must reject it.
            await connection.ExecuteAsync(
                $"SET search_path TO {staleSchema}, {validSchema}, public");
            Assert.False(await repository.HasMergedMiningBlockIndexesAsync(connection,
                CancellationToken.None));
        }

        finally
        {
            await connection.ExecuteAsync("SET search_path TO public");
            await connection.ExecuteAsync(
                $"DROP SCHEMA IF EXISTS {validSchema} CASCADE; " +
                $"DROP SCHEMA IF EXISTS {staleSchema} CASCADE;");
        }
    }
}
