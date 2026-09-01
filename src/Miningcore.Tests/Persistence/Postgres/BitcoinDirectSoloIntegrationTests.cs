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
using Miningcore.Tests.Blockchain.Bitcoin;

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
                DROP INDEX idx_blocks_bitcoin_direct_reconcile;
                CREATE INDEX idx_blocks_bitcoin_direct_reconcile ON blocks(
                    poolid, blockheight,
                    directsettlementlastchecked NULLS FIRST, created, id)
                    WHERE status IN ('confirmed', 'orphaned') AND
                        type = 'bitcoin-coinbase-direct' AND
                        settlementmode = 'coinbase-direct';");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            await connection.ExecuteAsync(@"
                DROP INDEX idx_blocks_bitcoin_direct_submission;
                CREATE INDEX idx_blocks_bitcoin_direct_submission ON blocks(
                    poolid, id DESC)
                    WHERE status = 'pending' AND
                        type = 'bitcoin-coinbase-direct' AND
                        settlementmode = 'coinbase-direct' AND
                        directsubmissionstate IN
                            ('prepared', 'submitted-uncertain');");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
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
                            type = 'bitcoin-coinbase-direct' AND
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
                    WHERE status = 'confirmed' AND
                        type = 'bitcoin-coinbase-direct' AND
                        settlementmode = 'coinbase-direct';");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            var submission = BitcoinDirectSubmissionTestData.Create();
            var direct = new Block
            {
                PoolId = "btc-direct",
                BlockHeight = 101,
                NetworkDifficulty = 1,
                Status = BlockStatus.Pending,
                Type = BitcoinDirectCoinbaseSettlement.BlockType,
                TransactionConfirmationData = submission.CoinbaseTxId,
                Miner = "miner",
                Hash = submission.BlockHash,
                Created = DateTime.UtcNow,
                SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
                GrossRewardSatoshis = 5_000_000_000,
                DirectMinerRewardSatoshis = 4_900_000_000,
                DirectMinerScriptPubKey = "0014" + new string('1', 40),
                DirectRecipientOutputs =
                    "[{\"Address\":\"fee\",\"ScriptPubKey\":\"0014" +
                    new string('2', 40) +
                    "\",\"AmountSatoshis\":100000000}]",
                DirectSubmissionState = BitcoinDirectSubmission.Prepared,
                DirectSubmissionBlock = submission.BlockHex,
                DirectSubmissionAttempts = 0,
                DirectSubmissionDefinitiveMisses = 0,
            };
            await repository.InsertAsync(connection, null, direct,
                CancellationToken.None);

            direct.Id = await connection.ExecuteScalarAsync<long>(@"
                SELECT id FROM blocks WHERE poolid = 'btc-direct'");

            var stored = await connection.QuerySingleAsync<dynamic>(@"
                SELECT settlementmode, grossrewardsatoshis,
                    directminerrewardsatoshis, directminerscriptpubkey,
                    directrecipientoutputs::text AS directrecipientoutputs,
                    directsubmissionstate, directsubmissionblock,
                    directsubmissionattempts,
                    directsubmissiondefinitivemisses
                FROM blocks WHERE poolid='btc-direct'");
            Assert.Equal(BitcoinDirectCoinbaseSettlement.Mode,
                (string) stored.settlementmode);
            Assert.Equal(5_000_000_000L,
                (long) stored.grossrewardsatoshis);
            Assert.Equal(4_900_000_000L,
                (long) stored.directminerrewardsatoshis);
            Assert.Contains("100000000",
                (string) stored.directrecipientoutputs);
            Assert.Equal(BitcoinDirectSubmission.Prepared,
                (string) stored.directsubmissionstate);
            Assert.Equal(submission.BlockHex,
                (string) stored.directsubmissionblock);
            Assert.Equal(0, (int) stored.directsubmissionattempts);
            Assert.Equal(0, (int) stored.directsubmissiondefinitivemisses);
            var replayable = Assert.Single(await repository
                .GetBitcoinDirectSubmissionsForReplayAsync(connection,
                    direct.PoolId, 0, 16, CancellationToken.None));
            Assert.Equal(submission.BlockHex,
                replayable.DirectSubmissionBlock);
            var preparedPending = Assert.Single(await repository
                .GetPendingBlocksForPoolAsync(connection, direct.PoolId));
            Assert.Equal(BitcoinDirectSubmission.Prepared,
                preparedPending.DirectSubmissionState);
            Assert.Equal(submission.BlockHex,
                preparedPending.DirectSubmissionBlock);

            var quarantined = new Block
            {
                PoolId = "btc-direct-quarantine",
                BlockHeight = direct.BlockHeight,
                NetworkDifficulty = direct.NetworkDifficulty,
                Status = BlockStatus.Pending,
                Type = direct.Type,
                TransactionConfirmationData = direct.TransactionConfirmationData,
                Miner = direct.Miner,
                Hash = direct.Hash,
                Created = DateTime.UtcNow,
                SettlementMode = direct.SettlementMode,
                GrossRewardSatoshis = direct.GrossRewardSatoshis,
                DirectMinerRewardSatoshis = direct.DirectMinerRewardSatoshis,
                DirectMinerScriptPubKey = direct.DirectMinerScriptPubKey,
                DirectRecipientOutputs = direct.DirectRecipientOutputs,
                DirectSubmissionState = BitcoinDirectSubmission.Prepared,
                DirectSubmissionBlock = direct.DirectSubmissionBlock,
                DirectSubmissionAttempts = 0,
                DirectSubmissionDefinitiveMisses = 0,
            };
            await repository.InsertAsync(connection, null, quarantined,
                CancellationToken.None);
            quarantined.Id = await connection.ExecuteScalarAsync<long>(@"
                SELECT id FROM blocks
                WHERE poolid = 'btc-direct-quarantine'");
            quarantined.Status = BlockStatus.Quarantined;
            quarantined.DirectSubmissionState =
                BitcoinDirectSubmission.Quarantined;
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                quarantined));
            var storedQuarantine = await connection.QuerySingleAsync<dynamic>(@"
                SELECT status, directsubmissionstate, directsubmissionblock
                FROM blocks WHERE id = @id", new { quarantined.Id });
            Assert.Equal("quarantined", (string) storedQuarantine.status);
            Assert.Equal(BitcoinDirectSubmission.Quarantined,
                (string) storedQuarantine.directsubmissionstate);
            Assert.Equal(submission.BlockHex,
                (string) storedQuarantine.directsubmissionblock);
            Assert.Empty(await repository
                .GetBitcoinDirectSubmissionsForReplayAsync(connection,
                    quarantined.PoolId, 0, 16, CancellationToken.None));

            await Assert.ThrowsAsync<PostgresException>(() =>
                connection.ExecuteAsync(@"
                    UPDATE blocks SET status = 'confirmed'
                    WHERE poolid = 'btc-direct'"));
            var observed = await repository
                .RecordBitcoinDirectSubmissionAttemptAsync(connection, null,
                    direct.PoolId, direct.Hash,
                    BitcoinDirectSubmissionOutcome.ObservedActive,
                    DateTime.UtcNow, 3, DateTime.UtcNow.AddMinutes(-30));
            Assert.NotNull(observed);
            Assert.Empty(await repository
                .GetBitcoinDirectSubmissionsForReplayAsync(connection,
                    direct.PoolId, 0, 16, CancellationToken.None));
            var observedPending = Assert.Single(await repository
                .GetPendingBlocksForPoolAsync(connection, direct.PoolId));
            Assert.Equal(BitcoinDirectSubmission.ObservedActive,
                observedPending.DirectSubmissionState);
            Assert.Null(observedPending.DirectSubmissionBlock);
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                observedPending));
            Assert.Equal(submission.BlockHex,
                await connection.ExecuteScalarAsync<string>(@"
                    SELECT directsubmissionblock FROM blocks
                    WHERE id = @id", new { id = direct.Id }));
            direct.DirectSubmissionState = observed.DirectSubmissionState;
            direct.DirectSubmissionAttempts =
                observed.DirectSubmissionAttempts;
            direct.DirectSubmissionDefinitiveMisses =
                observed.DirectSubmissionDefinitiveMisses;
            direct.DirectSubmissionLastAttempt =
                observed.DirectSubmissionLastAttempt;
            direct.Status = BlockStatus.Confirmed;
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                direct));

            await using(var tx = await connection.BeginTransactionAsync())
            {
                direct.Status = BlockStatus.Pending;
                Assert.True(await repository.UpdateBlockAsync(connection, tx,
                    direct));
                await Assert.ThrowsAsync<PostgresException>(() =>
                    connection.ExecuteAsync(@"
                        UPDATE blocks SET status = 'confirmed'
                        WHERE poolid = 'btc-direct'", transaction: tx));
                await tx.RollbackAsync();
                direct.Status = BlockStatus.Confirmed;
            }
            var now = DateTime.UtcNow;
            var due = await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", 0, now, 64,
                    CancellationToken.None);
            var dueBlock = Assert.Single(due);
            Assert.Null(dueBlock.DirectSettlementLastChecked);
            Assert.Null(dueBlock.DirectSubmissionBlock);

            dueBlock.DirectSettlementLastChecked = now;
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                dueBlock));
            Assert.Equal(submission.BlockHex,
                await connection.ExecuteScalarAsync<string>(@"
                    SELECT directsubmissionblock FROM blocks
                    WHERE id = @id", new { id = direct.Id }));
            Assert.Empty(await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", 0,
                    now.AddMinutes(-30), 64,
                    CancellationToken.None));

            Assert.True(await repository.TouchBitcoinDirectReconciliationAsync(
                connection, null, direct.Id, now.AddHours(-2),
                CancellationToken.None));
            Assert.Single(await repository
                .GetBitcoinDirectBlocksForReconciliationAsync(
                    connection, "btc-direct", 0,
                    now.AddHours(-1), 64,
                    CancellationToken.None));

            await connection.ExecuteAsync(@"
                ALTER TABLE blocks DROP CONSTRAINT
                    chk_blocks_bitcoin_direct_settlement;
                INSERT INTO blocks(poolid, blockheight, networkdifficulty,
                    status, type, transactionconfirmationdata, miner, hash,
                    created, settlementmode, grossrewardsatoshis,
                    directminerrewardsatoshis, directminerscriptpubkey,
                    directrecipientoutputs)
                VALUES('mistyped-direct', 99, 1, 'pending',
                    'bitcoin-direct', '" + new string('c', 64) + @"',
                    'miner', '" + new string('d', 64) + @"', now(),
                    'coinbase-direct', 5000000000, 4900000000,
                    '0014" + new string('3', 40) + @"', '[]');");
            var mistypedId = await connection.ExecuteScalarAsync<long>(@"
                SELECT id FROM blocks WHERE poolid = 'mistyped-direct'");
            var mistyped = new Block
            {
                Id = mistypedId,
                PoolId = "mistyped-direct",
                BlockHeight = 99,
                NetworkDifficulty = 1,
                Status = BlockStatus.Quarantined,
                Type = "bitcoin-direct",
                TransactionConfirmationData = new string('c', 64),
                Miner = "miner",
                Hash = new string('d', 64),
                Created = DateTime.UtcNow,
                SettlementMode = BitcoinDirectCoinbaseSettlement.Mode,
                GrossRewardSatoshis = 5_000_000_000,
                DirectMinerRewardSatoshis = 4_900_000_000,
                DirectMinerScriptPubKey = "0014" + new string('3', 40),
                DirectRecipientOutputs = "[]",
                DirectSubmissionState =
                    BitcoinDirectSubmission.LegacyObserved,
                DirectSubmissionAttempts = 0,
                DirectSubmissionDefinitiveMisses = 0,
            };
            Assert.True(await repository.UpdateBlockAsync(connection, null,
                mistyped));
            Assert.Equal("quarantined",
                await connection.ExecuteScalarAsync<string>(@"
                    SELECT status FROM blocks
                    WHERE id = @mistypedId", new { mistypedId }));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            Assert.Equal(BitcoinDirectCoinbaseSettlement.BlockType,
                await connection.ExecuteScalarAsync<string>(@"
                    SELECT type FROM blocks
                    WHERE id = @mistypedId", new { mistypedId }));

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
                        'bitcoin-coinbase-direct', 'tx', now(),
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

            await connection.ExecuteAsync(@"
                CREATE OR REPLACE FUNCTION guard_bitcoin_direct_block_update()
                RETURNS trigger LANGUAGE plpgsql
                SET search_path = pg_catalog AS $$
                BEGIN
                    IF false AND OLD.settlementmode = 'coinbase-direct' AND
                       current_setting('miningcore.direct_settlement_update', true)
                           IS DISTINCT FROM 'on' THEN
                        RAISE EXCEPTION USING ERRCODE = '55000',
                            MESSAGE = 'direct-settlement block updates require a compatible Miningcore binary';
                    END IF;
                    RETURN NEW;
                END;
                $$;");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            await connection.ExecuteAsync(@"
                CREATE OR REPLACE FUNCTION clear_bitcoin_direct_block_update_guard()
                RETURNS trigger LANGUAGE plpgsql
                SET search_path = pg_catalog AS $$
                BEGIN
                    RETURN NULL;
                END;
                $$;");
            Assert.False(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));
            await connection.ExecuteAsync(migration);
            Assert.True(await repository.HasBitcoinDirectSoloSchemaAsync(
                connection, CancellationToken.None));

            await connection.ExecuteAsync(@"
                DROP INDEX idx_blocks_bitcoin_coinbase_direct_pool_hash;
                CREATE INDEX idx_blocks_bitcoin_coinbase_direct_pool_hash
                    ON blocks(poolid, hash)
                    WHERE type = 'bitcoin-coinbase-direct';");
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
