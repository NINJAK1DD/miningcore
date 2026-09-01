using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class BitcoinDirectSoloIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task Migration_RepairsContractAndPersistsImmutableEvidence()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_bitcoin_direct_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE blocks(
                    id bigserial PRIMARY KEY, poolid text NOT NULL,
                    blockheight bigint NOT NULL,
                    networkdifficulty double precision NOT NULL,
                    status text NOT NULL, type text NULL,
                    transactionconfirmationdata text NULL,
                    miner text NULL, reward decimal(28,12) NULL,
                    effort double precision NULL,
                    minereffort double precision NULL,
                    confirmationprogress double precision NULL,
                    source text NULL, hash text NULL,
                    created timestamptz NOT NULL);
                CREATE UNIQUE INDEX idx_blocks_bitcoin_direct_candidate
                    ON blocks(poolid, hash)
                    WHERE type = 'bitcoin-direct';");
            var migrationPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "../../../../Miningcore/Persistence/Postgres/Scripts/add_bitcoin_direct_solo.sql"));
            var migration = (await File.ReadAllTextAsync(migrationPath))
                .Replace("\\set ON_ERROR_STOP on", string.Empty,
                    StringComparison.Ordinal);
            var repository = new BlockRepository(
                AutoMapperFactory.CreateMapper());

            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            await connection.ExecuteAsync(@"
                ALTER TABLE blocks DROP CONSTRAINT
                    chk_blocks_bitcoin_direct_settlement;
                ALTER TABLE blocks ADD CONSTRAINT
                    chk_blocks_bitcoin_direct_settlement CHECK(
                        ((settlementmode IS NULL AND
                            grossrewardsatoshis IS NULL AND
                            directminerrewardsatoshis IS NULL AND
                            directminerscriptpubkey IS NULL AND
                            directrecipientoutputs IS NULL AND
                            directsettlementlastchecked IS NULL)
                         OR
                         (settlementmode = 'coinbase-direct' AND
                            type = 'bitcoin-direct' AND
                            grossrewardsatoshis > 0 AND
                            directminerrewardsatoshis > 0 AND
                            directminerrewardsatoshis <= grossrewardsatoshis AND
                            directminerscriptpubkey ~ '^[0-9a-f]+$' AND
                            length(directminerscriptpubkey) % 2 = 0 AND
                            jsonb_typeof(directrecipientoutputs) = 'array'))
                        OR true);");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            await connection.ExecuteAsync(@"
                DROP INDEX idx_blocks_bitcoin_direct_reconcile;
                CREATE INDEX idx_blocks_bitcoin_direct_reconcile ON blocks(
                    poolid, directsettlementlastchecked DESC, created, id)
                    WHERE status = 'confirmed' AND type = 'bitcoin-direct' AND
                        settlementmode = 'coinbase-direct';");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            var direct = new Block
            {
                PoolId = "btc-direct",
                BlockHeight = 101,
                NetworkDifficulty = 1,
                Status = BlockStatus.Pending,
                Type = "bitcoin-direct",
                TransactionConfirmationData = new string('a', 64),
                Miner = "miner",
                Hash = new string('b', 64),
                Created = DateTime.UtcNow,
                SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
                GrossRewardSatoshis = 5_000_000_000,
                DirectMinerRewardSatoshis = 4_900_000_000,
                DirectMinerScriptPubKey = "0014" + new string('1', 40),
                DirectRecipientOutputs =
                    "[{\"Address\":\"fee\",\"ScriptPubKey\":\"0014" +
                    new string('2', 40) +
                    "\",\"AmountSatoshis\":100000000}]",
            };
            await repository.InsertAsync(connection, null, direct,
                CancellationToken.None);

            direct.Id = await connection.ExecuteScalarAsync<long>(@"
                SELECT id FROM blocks WHERE poolid = 'btc-direct'");

            var stored = await connection.QuerySingleAsync<dynamic>(@"
                SELECT settlementmode, grossrewardsatoshis,
                    directminerrewardsatoshis, directminerscriptpubkey,
                    directrecipientoutputs::text AS directrecipientoutputs
                FROM blocks WHERE poolid='btc-direct'");
            Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
                (string) stored.settlementmode);
            Assert.Equal(5_000_000_000L,
                (long) stored.grossrewardsatoshis);
            Assert.Equal(4_900_000_000L,
                (long) stored.directminerrewardsatoshis);
            Assert.Contains("100000000",
                (string) stored.directrecipientoutputs);

            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(@"
                    UPDATE blocks SET status = 'confirmed'
                    WHERE poolid = 'btc-direct'"));
            direct.Status = BlockStatus.Confirmed;
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                direct));
            var now = DateTime.UtcNow;
            var due = await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", now, 64,
                    CancellationToken.None);
            var dueBlock = Assert.Single(due);
            Assert.Null(dueBlock.DirectSettlementLastChecked);

            dueBlock.DirectSettlementLastChecked = now;
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                dueBlock));
            Assert.Empty(await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", now.AddMinutes(-30), 64,
                    CancellationToken.None));

            Assert.True(await repository.TouchBitcoinDirectReconciliationAsync(
                connection, null, direct.Id, now.AddHours(-2),
                CancellationToken.None));
            Assert.Single(await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", now.AddHours(-1), 64,
                    CancellationToken.None));

            await repository.InsertAsync(connection, null, new Block
            {
                PoolId = "legacy",
                BlockHeight = 100,
                NetworkDifficulty = 1,
                Status = BlockStatus.Pending,
                Created = DateTime.UtcNow,
            }, CancellationToken.None);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM blocks WHERE poolid='legacy' AND settlementmode IS NULL"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                repository.InsertAsync(connection, null, new Block
                {
                    PoolId = "partial",
                    BlockHeight = 102,
                    NetworkDifficulty = 1,
                    Status = BlockStatus.Pending,
                    Created = DateTime.UtcNow,
                    GrossRewardSatoshis = 5_000_000_000,
                }, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                repository.InsertAsync(connection, null, new Block
                {
                    PoolId = "unknown",
                    BlockHeight = 103,
                    NetworkDifficulty = 1,
                    Status = BlockStatus.Pending,
                    Created = DateTime.UtcNow,
                    SettlementMode = "future-mode",
                }, CancellationToken.None));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM blocks WHERE poolid IN ('partial', 'unknown')"));

            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(@"
                    INSERT INTO blocks(poolid, blockheight,
                        networkdifficulty, status, type,
                        transactionconfirmationdata, created,
                        settlementmode, grossrewardsatoshis)
                    VALUES('partial-sql', 104, 1, 'pending',
                        'bitcoin-direct', 'tx', now(),
                        'coinbase-direct', 5000000000)"));

            await connection.ExecuteAsync(@"
                DROP TRIGGER trg_guard_bitcoin_direct_block_update ON blocks;");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(@"
                CREATE TRIGGER trg_guard_bitcoin_direct_block_update
                    AFTER UPDATE ON blocks
                    FOR EACH ROW
                    EXECUTE FUNCTION guard_bitcoin_direct_block_update();");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
        }
        finally
        {
            await connection.ExecuteAsync(
                "ROLLBACK; SET search_path TO public");
            await connection.ExecuteAsync(
                $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
