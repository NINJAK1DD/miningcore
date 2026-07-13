using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class MergedMiningIndexIntegrationTests
{
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
            Assert.True(await repository.HasMergedMiningBlockIndexesAsync(connection,
                CancellationToken.None));

            var blockTime = DateTime.UtcNow;
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
            // relay-settlement delay has elapsed.
            await connection.ExecuteAsync($@"
                INSERT INTO {validSchema}.shares(
                    poolid, difficulty, networkdifficulty, miner, created)
                VALUES('ltc-solo', 1, 1, 'miner', @created)",
                new { created = blockTime });
            Assert.Equal(2d, await shareRepository
                .GetEffectiveAccumulatedShareDifficultyBetweenAsync(connection,
                    "ltc-solo", blockTime.AddMinutes(-1), blockTime,
                    CancellationToken.None));

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
