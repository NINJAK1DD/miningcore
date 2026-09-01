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
    private const string PublicBlockTypesFilter =
        "(type IS NULL OR type NOT IN ('auxpow-claim', 'parent-uncertain', 'merged-parent-uncertain'))";

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
                directsettlementlastchecked)
            VALUES(@poolid, @blockheight, @networkdifficulty, @status, @type, @transactionconfirmationdata,
                @miner, @reward, @effort, @minereffort, @confirmationprogress, @source, @hash, @created,
                @settlementmode, @grossrewardsatoshis, @directminerrewardsatoshis,
                @directminerscriptpubkey, CAST(@directrecipientoutputs AS jsonb),
                @directsettlementlastchecked)";

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

        const string query = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash WHERE id = @id";

        const string directQuery = @"UPDATE blocks SET blockheight = @blockheight,
            status = @status, type = @type, reward = @reward, effort = @effort,
            minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash,
            directsettlementlastchecked = @directsettlementlastchecked WHERE id = @id";

        const string auxPowPromotionQuery = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash
            WHERE id = @id AND NOT EXISTS (
                SELECT 1 FROM blocks
                WHERE poolid = @poolid AND hash = @hash AND type = 'auxpow' AND id <> @id
            )";

        var command = mapped.Type == "auxpow" ? auxPowPromotionQuery :
            string.Equals(mapped.SettlementMode,
                BitcoinDirectCoinbaseSettlement.Mode, StringComparison.Ordinal) ?
                directQuery : query;

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
        var query = $@"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status)
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            poolId,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> PageBlocksAsync(IDbConnection con, BlockStatus[] status, int page, int pageSize, CancellationToken ct)
    {
        var query = $@"SELECT * FROM blocks WHERE status = ANY(@status)
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> PageMinerBlocksAsync(IDbConnection con, string poolId, string address, BlockStatus[] status,
        int page, int pageSize, CancellationToken ct)
    {
        var query = $@"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND miner = @address
            AND {PublicBlockTypesFilter}
            ORDER BY created DESC OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query, new
        {
            poolId,
	    address,
            status = status.Select(x => x.ToString().ToLower()).ToArray(),
            offset = page * pageSize,
            pageSize
        }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> GetPendingBlocksForPoolAsync(IDbConnection con, string poolId)
    {
        const string query = @"SELECT * FROM blocks WHERE poolid = @poolid AND status = @status";

        return (await con.QueryAsync<Entities.Block>(query, new { status = BlockStatus.Pending.ToString().ToLower(), poolid = poolId }))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block[]> GetConfirmedBitcoinDirectBlocksForReconciliationAsync(
        IDbConnection con, string poolId, DateTime checkedBefore, int pageSize,
        CancellationToken ct)
    {
        if(pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        const string query = @"SELECT * FROM blocks
            WHERE poolid = @poolId
              AND status = 'confirmed'
              AND type = 'bitcoin-direct'
              AND settlementmode = 'coinbase-direct'
              AND (directsettlementlastchecked IS NULL OR
                   directsettlementlastchecked < @checkedBefore)
            ORDER BY directsettlementlastchecked ASC NULLS FIRST,
                created ASC, id ASC
            FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Block>(new CommandDefinition(query,
            new { poolId, checkedBefore, pageSize }, cancellationToken: ct)))
            .Select(mapper.Map<Block>)
            .ToArray();
    }

    public async Task<Block> GetBlockBeforeAsync(IDbConnection con, string poolId, BlockStatus[] status, DateTime before)
    {
        var query = $@"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before
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
                    ('directsettlementlastchecked', 'timestamp with time zone')
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
                          'checksettlementmodeisnullandgrossrewardsatoshisisnullanddirectminerrewardsatoshisisnullanddirectminerscriptpubkeyisnullanddirectrecipientoutputsisnullanddirectsettlementlastcheckedisnullorsettlementmode=''coinbase-direct''andtype=''bitcoin-direct''andgrossrewardsatoshis>0anddirectminerrewardsatoshis>0anddirectminerrewardsatoshis<=grossrewardsatoshisanddirectminerscriptpubkey~''^[0-9a-f]+$''andlengthdirectminerscriptpubkey%2=0andjsonb_typeofdirectrecipientoutputs=''array'''
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
                      AND i.indnkeyatts = 4 AND i.indnatts = 4
                      AND ARRAY(
                          SELECT lower(pg_get_indexdef(
                              index_class.oid, position, true))
                          FROM generate_series(1, i.indnkeyatts) position
                          ORDER BY position
                      ) = ARRAY['poolid', 'directsettlementlastchecked',
                          'created', 'id']
                      AND i.indoption = '0 2 0 0'::int2vector
                      AND regexp_replace(
                          replace(lower(pg_get_expr(i.indpred, i.indrelid,
                              true)), '::text', ''),
                          '[[:space:]()]', '', 'g') =
                          'status=''confirmed''andtype=''bitcoin-direct''andsettlementmode=''coinbase-direct'''
                ) AS ready
            )
            SELECT (SELECT COUNT(*) = 6
                    FROM required_columns r
                    JOIN actual_columns a USING(name)
                    WHERE a.data_type = r.data_type)
               AND (SELECT ready FROM required_constraint)
               AND (SELECT ready FROM required_reconciliation_index)";

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
