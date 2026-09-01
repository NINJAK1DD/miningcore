using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;

namespace Miningcore.Persistence.Postgres.Repositories;

public class BlockRepository : IBlockRepository
{
    public BlockRepository(IMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly IMapper mapper;
    internal const int MaximumPublicPageSize = 100;
    private const string PublicBlockTypesFilter =
        "(type IS NULL OR type NOT IN ('auxpow-claim', 'parent-uncertain', 'merged-parent-uncertain'))";
    private const string BlockMetadataColumns = @"id, poolid, blockheight,
        networkdifficulty, status, type, confirmationprogress, effort,
        minereffort, transactionconfirmationdata, miner, reward, source,
        hash, created, settlementmode, grossrewardsatoshis,
        directminerrewardsatoshis, directminerscriptpubkey,
        directrecipientoutputs, directsettlementlastchecked,
        directsubmissionstate, directsubmissionattempts,
        directsubmissiondefinitivemisses, directsubmissionlastattempt";
    private const string BlockReplayColumns = BlockMetadataColumns +
        ", directsubmissionblock";

    public async Task<bool> InsertAsync(IDbConnection con, IDbTransaction tx,
        Block block, CancellationToken ct = default)
    {
        var mapped = mapper.Map<Entities.Block>(block);

        const string legacyQuery =
            @"INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type, transactionconfirmationdata,
                miner, reward, effort, minereffort, confirmationprogress, source, hash, created)
            VALUES(@poolid, @blockheight, @networkdifficulty, @status, @type, @transactionconfirmationdata,
                @miner, @reward, @effort, @minereffort, @confirmationprogress, @source, @hash, @created)";
        const string directQuery =
            @"INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type, transactionconfirmationdata,
                miner, reward, effort, minereffort, confirmationprogress, source, hash, created,
                settlementmode, grossrewardsatoshis, directminerrewardsatoshis,
                directminerscriptpubkey, directrecipientoutputs,
                directsettlementlastchecked, directsubmissionstate,
                directsubmissionblock, directsubmissionattempts,
                directsubmissiondefinitivemisses, directsubmissionlastattempt)
            VALUES(@poolid, @blockheight, @networkdifficulty, @status, @type, @transactionconfirmationdata,
                @miner, @reward, @effort, @minereffort, @confirmationprogress, @source, @hash, @created,
                @settlementmode, @grossrewardsatoshis, @directminerrewardsatoshis,
                @directminerscriptpubkey, CAST(@directrecipientoutputs AS jsonb),
                @directsettlementlastchecked, @directsubmissionstate,
                @directsubmissionblock, @directsubmissionattempts,
                @directsubmissiondefinitivemisses, @directsubmissionlastattempt)";

        var hasSettlementEvidence = mapped.SettlementMode != null ||
            mapped.GrossRewardSatoshis.HasValue ||
            mapped.DirectMinerRewardSatoshis.HasValue ||
            mapped.DirectMinerScriptPubKey != null ||
            mapped.DirectRecipientOutputs != null;
        var isBitcoinDirect = string.Equals(mapped.SettlementMode,
            BitcoinDirectCoinbaseSettlement.Mode, StringComparison.Ordinal);

        if(hasSettlementEvidence && !isBitcoinDirect)
            throw new InvalidDataException(
                "Block settlement evidence requires the exact " +
                $"'{BitcoinDirectCoinbaseSettlement.Mode}' settlement mode");
        if(isBitcoinDirect && !string.Equals(mapped.Type,
               BitcoinDirectCoinbaseSettlement.BlockType,
               StringComparison.Ordinal))
            throw new InvalidDataException(
                "Direct coinbase settlement evidence requires the exact " +
                $"'{BitcoinDirectCoinbaseSettlement.BlockType}' block type");
        if(string.Equals(mapped.Type,
               BitcoinDirectCoinbaseSettlement.BlockType,
               StringComparison.Ordinal) && !isBitcoinDirect)
            throw new InvalidDataException(
                $"Block type '{BitcoinDirectCoinbaseSettlement.BlockType}' " +
                "requires complete direct coinbase settlement evidence");
        if(isBitcoinDirect)
            BitcoinDirectSubmission.ValidatePersistedBlock(block);

        var query = isBitcoinDirect ? directQuery : legacyQuery;

        var command = BlockOnlyCandidatePersistenceRules.TryGet(mapped.Type,
            out var persistenceRule)
            ? query + persistenceRule.ConflictClause
            : query;

        return await con.ExecuteAsync(new CommandDefinition(command, mapped, tx,
            cancellationToken: ct)) > 0;
    }

    public async Task DeleteBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        const string query = "DELETE FROM blocks WHERE id = @id";
        await con.ExecuteAsync(query, block, tx);
    }

    public async Task<bool> UpdateBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        var mapped = mapper.Map<Entities.Block>(block);

        if(string.Equals(block.SettlementMode,
               BitcoinDirectCoinbaseSettlement.Mode,
               StringComparison.Ordinal))
            BitcoinDirectSubmission.ValidatePersistedProjection(block);

        const string legacyQuery = @"UPDATE blocks SET blockheight = @blockheight,
            status = @status, type = @type, reward = @reward, effort = @effort,
            minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata,
            hash = @hash WHERE id = @id";

        const string directQuery = @"WITH direct_update_guard AS MATERIALIZED (
                SELECT set_config('miningcore.direct_settlement_update', 'on', true)
                FROM blocks
                WHERE id = @id
                  AND settlementmode = 'coinbase-direct'
            )
            UPDATE blocks SET blockheight = @blockheight,
            status = @status, type = @type, reward = @reward, effort = @effort,
            minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash,
            directsettlementlastchecked = @directsettlementlastchecked,
            directsubmissionstate = @directsubmissionstate,
            directsubmissionattempts = @directsubmissionattempts,
            directsubmissiondefinitivemisses = @directsubmissiondefinitivemisses,
            directsubmissionlastattempt = @directsubmissionlastattempt
            FROM direct_update_guard WHERE id = @id";

        const string auxPowPromotionQuery = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash
            WHERE id = @id AND NOT EXISTS (
                SELECT 1 FROM blocks
                WHERE poolid = @poolid AND hash = @hash AND type = 'auxpow' AND id <> @id
            )";

        // In-memory state selects the column set so deployments which have not enabled
        // direct SOLO remain compatible with the legacy schema. It does not grant update
        // authority: the direct CTE must match the persisted row, while the row trigger
        // rejects a legacy statement against a persisted direct row. The AFTER STATEMENT
        // trigger then revokes authority before another statement in the transaction.
        var command = mapped.Type == "auxpow" ? auxPowPromotionQuery :
            string.Equals(mapped.SettlementMode,
                BitcoinDirectCoinbaseSettlement.Mode, StringComparison.Ordinal)
                ? directQuery
                : legacyQuery;

        return await con.ExecuteAsync(command, mapped, tx) > 0;
    }

    public async Task<Block> GetBlockByIdForUpdateAsync(IDbConnection con, IDbTransaction tx,
        long id)
    {
        const string query = "SELECT * FROM blocks WHERE id = @id FOR UPDATE";

        return (await con.QueryAsync<Entities.Block>(query, new { id }, tx))
            .Select(mapper.Map<Block>)
            .FirstOrDefault();
    }

    public async Task<Block[]> PageBlocksAsync(IDbConnection con, string poolId, BlockStatus[] status,
        int page, int pageSize, CancellationToken ct)
    {
        ValidatePublicPage(page, pageSize);
        var query = $@"SELECT {BlockMetadataColumns} FROM blocks WHERE poolid = @poolid AND status = ANY(@status)
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = (long) page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> PageBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        ValidatePublicPage(page, pageSize);
        var query = $@"SELECT {BlockMetadataColumns} FROM blocks WHERE status = ANY(@status)
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = (long) page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> PageMinerBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status,
        int page, int pageSize, CancellationToken ct)
    {
        ValidatePublicPage(page, pageSize);
        var query = $@"SELECT {BlockMetadataColumns} FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND miner = @address
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            poolId,
	    address,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = (long) page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> GetPendingBlocksForPoolAsync(IDbConnection con, string poolId)
    {
        var query = $@"SELECT {BlockMetadataColumns},
            CASE
                WHEN type = 'bitcoin-coinbase-direct'
                  AND settlementmode = 'coinbase-direct'
                  AND directsubmissionstate IN
                      ('prepared', 'submitted-uncertain')
                    THEN directsubmissionblock
                ELSE NULL
            END AS directsubmissionblock
            FROM blocks
            WHERE poolid = @poolid AND status = @status";

        return (await con.QueryAsync<Entities.Block>(query, new { status = BlockStatus.Pending.ToString().ToLower(), poolid = poolId }))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> GetBitcoinDirectBlocksForReconciliationAsync(
        IDbConnection con, string poolId, long minimumBlockHeight,
        DateTime checkedBefore, int pageSize, CancellationToken ct)
    {
        if(minimumBlockHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumBlockHeight));
        if(pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var query = $@"SELECT {BlockMetadataColumns} FROM blocks
            WHERE poolid = @poolId
              AND status IN ('confirmed', 'orphaned')
              AND type = 'bitcoin-coinbase-direct'
              AND settlementmode = 'coinbase-direct'
              AND blockheight >= @minimumBlockHeight
              AND (directsettlementlastchecked IS NULL OR
                   directsettlementlastchecked < @checkedBefore)
            ORDER BY directsettlementlastchecked ASC NULLS FIRST,
                created ASC, id ASC
            FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query,
            new { poolId, minimumBlockHeight, checkedBefore, pageSize },
            cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> GetBitcoinDirectSubmissionsForReplayAsync(
        IDbConnection con, string poolId, long afterId, int pageSize,
        CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(poolId))
            throw new ArgumentException("Pool id is required", nameof(poolId));
        if(pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if(afterId < 0)
            throw new ArgumentOutOfRangeException(nameof(afterId));

        var query = $@"SELECT {BlockReplayColumns} FROM blocks
            WHERE poolid = @poolId
              AND status = 'pending'
              AND type = 'bitcoin-coinbase-direct'
              AND settlementmode = 'coinbase-direct'
              AND directsubmissionstate IN
                  ('prepared', 'submitted-uncertain')
              AND id > @afterId
            ORDER BY id ASC
            FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(
            query, new { poolId, afterId, pageSize }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<bool> TouchBitcoinDirectReconciliationAsync(
        IDbConnection con, IDbTransaction tx, long id, DateTime checkedAt,
        CancellationToken ct = default)
    {
        const string query = @"WITH direct_update_guard AS MATERIALIZED (
                SELECT set_config('miningcore.direct_settlement_update', 'on', true)
                FROM blocks
                WHERE id = @id
                  AND type = 'bitcoin-coinbase-direct'
                  AND settlementmode = 'coinbase-direct'
            )
            UPDATE blocks SET directsettlementlastchecked = @checkedAt
            FROM direct_update_guard
            WHERE id = @id
              AND status IN ('confirmed', 'orphaned')
              AND type = 'bitcoin-coinbase-direct'
              AND settlementmode = 'coinbase-direct'";

        return await con.ExecuteAsync(new CommandDefinition(query,
            new { id, checkedAt }, tx, cancellationToken: ct)) > 0;
    }

    public async Task<Block>
        RecordBitcoinDirectSubmissionAttemptAsync(IDbConnection con,
            IDbTransaction tx, string poolId, string blockHash,
            BitcoinDirectSubmissionOutcome outcome, DateTime attemptedAt,
            int minimumDefinitiveMisses, DateTime rejectBefore,
            CancellationToken ct = default)
    {
        if(string.IsNullOrWhiteSpace(poolId))
            throw new ArgumentException("Pool id is required", nameof(poolId));
        if(string.IsNullOrWhiteSpace(blockHash))
            throw new ArgumentException("Block hash is required", nameof(blockHash));
        if(minimumDefinitiveMisses <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumDefinitiveMisses));

        var nextState = outcome == BitcoinDirectSubmissionOutcome.ObservedActive
            ? BitcoinDirectSubmission.ObservedActive
            : BitcoinDirectSubmission.SubmittedUncertain;
        var definitiveMiss = outcome ==
            BitcoinDirectSubmissionOutcome.DefinitiveMiss;

        const string query = @"WITH candidate AS MATERIALIZED (
                SELECT id, directsubmissionstate,
                    directsubmissiondefinitivemisses,
                    directsubmissionattempts, created
                FROM blocks
                WHERE poolid = @poolId AND hash = @blockHash
                  AND type = 'bitcoin-coinbase-direct'
                  AND settlementmode = 'coinbase-direct'
                  AND status = 'pending'
                  AND directsubmissionstate IN
                      ('prepared', 'submitted-uncertain')
                FOR UPDATE
            ), direct_update_guard AS MATERIALIZED (
                SELECT set_config(
                    'miningcore.direct_settlement_update', 'on', true)
                FROM candidate
            ), next_values AS MATERIALIZED (
                SELECT candidate.id,
                    candidate.directsubmissionattempts + 1 AS attempts,
                    candidate.directsubmissiondefinitivemisses +
                        CASE WHEN @definitiveMiss THEN 1 ELSE 0 END AS misses,
                    CASE
                        WHEN @definitiveMiss
                          AND candidate.directsubmissiondefinitivemisses + 1 >=
                              @minimumDefinitiveMisses
                          AND candidate.created <= @rejectBefore
                            THEN 'rejected'
                        ELSE @nextState
                    END AS state
                FROM candidate, direct_update_guard
            )
            UPDATE blocks SET
                directsubmissionstate = next_values.state,
                directsubmissionattempts = next_values.attempts,
                directsubmissiondefinitivemisses = next_values.misses,
                directsubmissionlastattempt = @attemptedAt,
                status = CASE WHEN next_values.state = 'rejected'
                    THEN 'orphaned' ELSE blocks.status END,
                confirmationprogress = CASE
                    WHEN next_values.state = 'rejected' THEN 0
                    ELSE blocks.confirmationprogress END,
                reward = CASE WHEN next_values.state = 'rejected' THEN 0
                    ELSE blocks.reward END
            FROM next_values
            WHERE blocks.id = next_values.id
            RETURNING blocks.*";

        var entity = await con.QuerySingleOrDefaultAsync<Entities.Block>(
            new CommandDefinition(query, new
            {
                poolId,
                blockHash,
                nextState,
                definitiveMiss,
                attemptedAt,
                minimumDefinitiveMisses,
                rejectBefore,
            }, tx, cancellationToken: ct));
        return entity == null ? null : mapper.Map<Block>(entity);
    }

    public async Task<Block> GetBlockBeforeAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before)
    {
        var query = $@"SELECT created FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC FETCH NEXT 1 ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }))
            .Select(mapper.Map<Block>)
            .FirstOrDefault();
    }
    
    public async Task<uint> GetBlockBeforeCountAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before)
    {
        var query = $@"SELECT COUNT(*) FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before
            AND {PublicBlockTypesFilter}";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }

    public Task<uint> GetPoolBlockCountAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        var query = $@"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId
            AND {PublicBlockTypesFilter}";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetTotalConfirmedBlocksAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        var query = $@"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND status = 'confirmed'
            AND {PublicBlockTypesFilter}";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetTotalPendingBlocksAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        var query = $@"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND status = 'pending'
            AND {PublicBlockTypesFilter}";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<decimal> GetLastConfirmedBlockRewardAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        var query = $@"SELECT reward FROM blocks WHERE poolid = @poolId AND status = 'confirmed'
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC LIMIT 1";

        return con.ExecuteScalarAsync<decimal>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<uint> GetMinerBlockCountAsync(IDbConnection con, string poolId, string address, CancellationToken ct)
    {
        var query = $@"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND miner = @address
            AND {PublicBlockTypesFilter}";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId, address }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastPoolBlockTimeAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        var query = $@"SELECT created FROM blocks WHERE poolid = @poolId
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC LIMIT 1";

        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public Task<DateTime?> GetLastMinerBlockTimeAsync(IDbConnection con, string poolId, string address, CancellationToken ct)
    {
        var query = $@"SELECT created FROM blocks WHERE poolid = @poolId AND miner = @address
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC LIMIT 1";
        return con.ExecuteScalarAsync<DateTime?>(new CommandDefinition(query, new { poolId, address }, cancellationToken: ct));
    }

    public async Task<Block> GetBlockByPoolHeightAndTypeAsync(IDbConnection con, string poolId, long height, string type)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolId AND blockheight = @height AND type = @type";

        return (await con.QueryAsync<Entities.Block>(query, new
        { 
            poolId,
            height,
            type
        }))
            .Select(mapper.Map<Block>)
            .FirstOrDefault();
    }

    public async Task<Block> GetBlockByPoolHashAndTypeAsync(IDbConnection con, string poolId,
        string hash, string type)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolId AND hash = @hash AND type = @type
            ORDER BY created ASC FETCH NEXT 1 ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(query, new
        {
            poolId,
            hash,
            type,
        }))
            .Select(mapper.Map<Block>)
            .FirstOrDefault();
    }

    public Task<bool> HasMergedMiningBlockIndexesAsync(IDbConnection con, CancellationToken ct)
    {
        const string query = @"WITH expected(name, key_expressions, predicate) AS (
                VALUES
                    ('idx_blocks_auxpow_pool_hash', ARRAY[
                        'poolid',
                        'hash'
                    ], 'type = ''auxpow''::text'),
                    ('idx_blocks_auxpow_claim', ARRAY[
                        'poolid',
                        'hash',
                        'regexp_replace(transactionconfirmationdata, '':[0-9]+$''::text, ''''::text)'
                    ], 'type = ''auxpow-claim''::text'),
                    ('idx_blocks_merged_parent_pool_hash', ARRAY[
                        'poolid',
                        'hash'
                    ], 'type = any (array[''merged-parent''::text, ''merged-parent-uncertain''::text])'),
                    ('idx_blocks_bitcoin_direct_pool_hash', ARRAY[
                        'poolid',
                        'hash'
                    ], 'type = ''bitcoin-direct''::text')
            ),
            actual AS (
                SELECT lower(index_class.relname) AS name,
                    i.indisunique,
                    i.indisvalid,
                    i.indisready,
                    ARRAY(
                        SELECT lower(regexp_replace(
                            pg_get_indexdef(index_class.oid, key_position, true),
                            '\s+', ' ', 'g'))
                        FROM generate_series(1, i.indnkeyatts) key_position
                        ORDER BY key_position
                    ) AS key_expressions,
                    lower(regexp_replace(
                        pg_get_expr(i.indpred, i.indrelid, true),
                        '\s+', ' ', 'g')) AS predicate
                FROM pg_index i
                JOIN pg_class index_class ON index_class.oid = i.indexrelid
                JOIN pg_class table_class ON table_class.oid = i.indrelid
                JOIN pg_namespace table_namespace ON table_namespace.oid = table_class.relnamespace
                WHERE index_class.relkind = 'i'
                  AND table_class.relkind IN ('r', 'p')
                  -- Resolve the same unqualified relation used by runtime repository SQL.
                  -- This excludes stale same-named indexes in every other schema.
                  AND i.indrelid = to_regclass('blocks')
            )
            SELECT COUNT(*) = 4
            FROM expected e
            JOIN actual a ON a.name = e.name
            WHERE a.indisunique
              AND a.indisvalid
              AND a.indisready
              AND a.key_expressions = e.key_expressions
              AND a.predicate = e.predicate";

        return con.ExecuteScalarAsync<bool>(new CommandDefinition(query,
            cancellationToken: ct));
    }

    private static void ValidatePublicPage(int page, int pageSize)
    {
        if(page < 0)
            throw new ArgumentOutOfRangeException(nameof(page));
        if(pageSize is <= 0 or > MaximumPublicPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize),
                $"Public block page size must be between 1 and {MaximumPublicPageSize}");
    }

    public Task<bool> HasBitcoinDirectSoloSchemaAsync(IDbConnection con,
        CancellationToken ct)
    {
        const string query = @"WITH required_columns(name, data_type) AS (
                VALUES
                    ('settlementmode', 'text'),
                    ('grossrewardsatoshis', 'bigint'),
                    ('directminerrewardsatoshis', 'bigint'),
                    ('directminerscriptpubkey', 'text'),
                    ('directrecipientoutputs', 'jsonb'),
                    ('directsettlementlastchecked', 'timestamp with time zone'),
                    ('directsubmissionstate', 'text'),
                    ('directsubmissionblock', 'text'),
                    ('directsubmissionattempts', 'integer'),
                    ('directsubmissiondefinitivemisses', 'integer'),
                    ('directsubmissionlastattempt', 'timestamp with time zone')
            ), actual_columns AS (
                SELECT lower(a.attname) AS name,
                    format_type(a.atttypid, a.atttypmod) AS data_type
                FROM pg_attribute a
                WHERE a.attrelid = to_regclass('blocks')
                  AND a.attnum > 0 AND NOT a.attisdropped
            ), required_constraint AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_constraint c
                    WHERE c.conrelid = to_regclass('blocks')
                      AND lower(c.conname) =
                          'chk_blocks_bitcoin_direct_settlement'
                      AND c.contype = 'c'
                      AND c.convalidated
                      AND regexp_replace(
                          replace(lower(pg_get_constraintdef(c.oid)),
                              '::text', ''),
                          '[[:space:]()]', '', 'g') =
                          'checknum_nonnullssettlementmode,grossrewardsatoshis,directminerrewardsatoshis,directminerscriptpubkey,directrecipientoutputs,directsubmissionstate,directsubmissionblock,directsubmissionattempts,directsubmissiondefinitivemisses,directsubmissionlastattempt=0anddirectsettlementlastcheckedisnullandtypeisdistinctfrom''bitcoin-coinbase-direct''ornum_nonnullssettlementmode,grossrewardsatoshis,directminerrewardsatoshis,directminerscriptpubkey,directrecipientoutputs=5andsettlementmode=''coinbase-direct''andtype=''bitcoin-coinbase-direct''andgrossrewardsatoshis>0anddirectminerrewardsatoshis>0anddirectminerrewardsatoshis<=grossrewardsatoshisanddirectminerscriptpubkey~''^[0-9a-f]+$''andlengthdirectminerscriptpubkey%2=0andjsonb_typeofdirectrecipientoutputs=''array''anddirectsubmissionstate=''legacy-observed''anddirectsubmissionblockisnullanddirectsubmissionattempts=0anddirectsubmissiondefinitivemisses=0anddirectsubmissionlastattemptisnullordirectsubmissionstate=anyarray[''prepared'',''submitted-uncertain'',''observed-active'',''rejected'']anddirectsubmissionblock~''^[0-9a-f]+$''andlengthdirectsubmissionblock>=162andlengthdirectsubmissionblock<=8000000andlengthdirectsubmissionblock%2=0anddirectsubmissionattempts>=0anddirectsubmissiondefinitivemisses>=0anddirectsubmissiondefinitivemisses<=directsubmissionattemptsanddirectsubmissionstate=''prepared''anddirectsubmissionattempts=0anddirectsubmissiondefinitivemisses=0anddirectsubmissionlastattemptisnullandstatus=''pending''ordirectsubmissionstate<>''prepared''anddirectsubmissionattempts>0anddirectsubmissionlastattemptisnotnullanddirectsubmissionstate<>''submitted-uncertain''orstatus=''pending''anddirectsubmissionstate<>''rejected''orstatus=''orphaned''anddirectsubmissiondefinitivemisses>=3'
                ) AS ready
            ), required_candidate_index AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_class index_class ON index_class.oid = i.indexrelid
                    JOIN pg_am am ON am.oid = index_class.relam
                    WHERE i.indrelid = to_regclass('blocks')
                      AND lower(index_class.relname) =
                          'idx_blocks_bitcoin_coinbase_direct_pool_hash'
                      AND am.amname = 'btree'
                      AND i.indisunique
                      AND i.indisvalid AND i.indisready
                      AND i.indnkeyatts = 2 AND i.indnatts = 2
                      AND ARRAY(
                          SELECT lower(pg_get_indexdef(
                              index_class.oid, position, true))
                          FROM generate_series(1, i.indnkeyatts) position
                          ORDER BY position
                      ) = ARRAY['poolid', 'hash']
                      AND i.indoption = '0 0'::int2vector
                      AND regexp_replace(
                          replace(lower(pg_get_expr(i.indpred, i.indrelid,
                              true)), '::text', ''),
                          '[[:space:]()]', '', 'g') =
                          'type=''bitcoin-coinbase-direct'''
                ) AS ready
            ), required_reconciliation_index AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_class index_class ON index_class.oid = i.indexrelid
                    JOIN pg_am am ON am.oid = index_class.relam
                    WHERE i.indrelid = to_regclass('blocks')
                      AND lower(index_class.relname) =
                          'idx_blocks_bitcoin_direct_reconcile'
                      AND am.amname = 'btree'
                      AND NOT i.indisunique
                      AND i.indisvalid AND i.indisready
                      AND i.indnkeyatts = 5 AND i.indnatts = 5
                      AND ARRAY(
                          SELECT lower(pg_get_indexdef(
                              index_class.oid, position, true))
                          FROM generate_series(1, i.indnkeyatts) position
                          ORDER BY position
                      ) = ARRAY['poolid', 'directsettlementlastchecked',
                          'created', 'id', 'blockheight']
                      AND i.indoption = '0 2 0 0 0'::int2vector
                      AND regexp_replace(
                          replace(lower(pg_get_expr(i.indpred, i.indrelid,
                              true)), '::text', ''),
                          '[[:space:]()]', '', 'g') =
                          'status=anyarray[''confirmed'',''orphaned'']andtype=''bitcoin-coinbase-direct''andsettlementmode=''coinbase-direct'''
                ) AS ready
            ), required_submission_index AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_index i
                    JOIN pg_class index_class ON index_class.oid = i.indexrelid
                    JOIN pg_am am ON am.oid = index_class.relam
                    WHERE i.indrelid = to_regclass('blocks')
                      AND lower(index_class.relname) =
                          'idx_blocks_bitcoin_direct_submission'
                      AND am.amname = 'btree'
                      AND NOT i.indisunique
                      AND i.indisvalid AND i.indisready
                      AND i.indnkeyatts = 2 AND i.indnatts = 2
                      AND ARRAY(
                          SELECT lower(pg_get_indexdef(
                              index_class.oid, position, true))
                          FROM generate_series(1, i.indnkeyatts) position
                          ORDER BY position
                      ) = ARRAY['poolid', 'id']
                      AND i.indoption = '0 0'::int2vector
                      AND regexp_replace(
                          replace(lower(pg_get_expr(i.indpred, i.indrelid,
                              true)), '::text', ''),
                          '[[:space:]()]', '', 'g') =
                          'status=''pending''andtype=''bitcoin-coinbase-direct''andsettlementmode=''coinbase-direct''anddirectsubmissionstate=anyarray[''prepared'',''submitted-uncertain'']'
                ) AS ready
            ), required_update_guard AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_trigger t
                    JOIN pg_proc p ON p.oid = t.tgfoid
                    JOIN pg_language l ON l.oid = p.prolang
                    WHERE t.tgrelid = to_regclass('blocks')
                      AND lower(t.tgname) =
                          'trg_guard_bitcoin_direct_block_update'
                      AND NOT t.tgisinternal
                      AND t.tgenabled = 'O'
                      AND t.tgtype = 19
                      AND lower(p.proname) =
                          'guard_bitcoin_direct_block_update'
                      AND p.pronargs = 0
                      AND p.prorettype = 'trigger'::regtype
                      AND NOT p.prosecdef
                      AND l.lanname = 'plpgsql'
                      AND p.proconfig @> ARRAY['search_path=pg_catalog']
                      AND regexp_replace(lower(p.prosrc),
                          '[[:space:]]', '', 'g') =
                          'beginifold.settlementmode=''coinbase-direct''andcurrent_setting(''miningcore.direct_settlement_update'',true)isdistinctfrom''on''thenraiseexceptionusingerrcode=''55000'',message=''direct-settlementblockupdatesrequireacompatibleminingcorebinary'';endif;returnnew;end;'
                ) AS ready
            ), required_clear_update_guard AS (
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_trigger t
                    JOIN pg_proc p ON p.oid = t.tgfoid
                    JOIN pg_language l ON l.oid = p.prolang
                    WHERE t.tgrelid = to_regclass('blocks')
                      AND lower(t.tgname) =
                          'trg_clear_bitcoin_direct_block_update_guard'
                      AND NOT t.tgisinternal
                      AND t.tgenabled = 'O'
                      AND t.tgtype = 16
                      AND lower(p.proname) =
                          'clear_bitcoin_direct_block_update_guard'
                      AND p.pronargs = 0
                      AND p.prorettype = 'trigger'::regtype
                      AND NOT p.prosecdef
                      AND l.lanname = 'plpgsql'
                      AND p.proconfig @> ARRAY['search_path=pg_catalog']
                      AND regexp_replace(lower(p.prosrc),
                          '[[:space:]]', '', 'g') =
                          'beginperformset_config(''miningcore.direct_settlement_update'',''off'',true);returnnull;end;'
                ) AS ready
            )
            SELECT (SELECT COUNT(*) = 11
                    FROM required_columns r
                    JOIN actual_columns a USING(name)
                    WHERE a.data_type = r.data_type)
               AND (SELECT ready FROM required_constraint)
               AND (SELECT ready FROM required_candidate_index)
               AND (SELECT ready FROM required_reconciliation_index)
               AND (SELECT ready FROM required_submission_index)
               AND (SELECT ready FROM required_update_guard)
               AND (SELECT ready FROM required_clear_update_guard)";

        return con.ExecuteScalarAsync<bool>(new CommandDefinition(query,
            cancellationToken: ct));
    }
    
    public async Task<uint> GetPoolDuplicateBlockCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status)";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray()
        }));
    }
    
    public async Task<uint> GetPoolDuplicateBlockBeforeCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status) AND created < @before";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }
    
    public async Task<uint> GetPoolDuplicateBlockAfterCountByPoolHeightNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, BlockStatus[] status, DateTime after)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND status = ANY(@status) AND created > @after";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            after
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockBeforeCountByPoolHeightAndHashNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, string hash, BlockStatus[] status, DateTime before)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND hash = @hash AND status = ANY(@status) AND created < @before";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            hash,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            before
        }));
    }

    public async Task<uint> GetPoolDuplicateBlockAfterCountByPoolHeightAndHashNoTypeAndStatusAsync(IDbConnection con, string poolId, long height, string hash, BlockStatus[] status, DateTime after)
    {
        const string query = @"SELECT COUNT(id) FROM blocks WHERE poolid = @poolId AND blockheight = @height AND hash = @hash AND status = ANY(@status) AND created > @after";
        
        return await con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new
        {
            poolId,
            height,
            hash,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            after
        }));
    }
}
