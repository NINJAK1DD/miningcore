using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Npgsql;

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

    public async Task<bool> InsertAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        var mapped = mapper.Map<Entities.Block>(block);

        const string query =
            @"INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type, transactionconfirmationdata,
                miner, reward, effort, minereffort, confirmationprogress, source, hash, created)
            VALUES(@poolid, @blockheight, @networkdifficulty, @status, @type, @transactionconfirmationdata,
                @miner, @reward, @effort, @minereffort, @confirmationprogress, @source, @hash, @created)";

        const string auxPowConflictClause =
            " ON CONFLICT (poolid, hash) WHERE type = 'auxpow' DO NOTHING";
        const string auxPowClaimConflictClause =
            " ON CONFLICT (poolid, hash, (regexp_replace(transactionconfirmationdata, ':[0-9]+$', ''))) WHERE type = 'auxpow-claim' DO NOTHING";
        const string mergedParentConflictClause =
            " ON CONFLICT (poolid, hash) WHERE type IN ('merged-parent', 'merged-parent-uncertain') DO NOTHING";

        var command = mapped.Type switch
        {
            "auxpow" => query + auxPowConflictClause,
            "auxpow-claim" => query + auxPowClaimConflictClause,
            "merged-parent" => query + mergedParentConflictClause,
            "merged-parent-uncertain" => query + mergedParentConflictClause,
            _ => query,
        };

        return await con.ExecuteAsync(command, mapped, tx) > 0;
    }

    public async Task DeleteBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        const string query = "DELETE FROM blocks WHERE id = @id";
        await con.ExecuteAsync(query, block, tx);
    }

    public async Task UpdateBlockAsync(IDbConnection con, IDbTransaction tx, Block block)
    {
        var mapped = mapper.Map<Entities.Block>(block);

        const string query = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash WHERE id = @id";

        const string auxPowPromotionQuery = @"UPDATE blocks SET blockheight = @blockheight, status = @status, type = @type,
            reward = @reward, effort = @effort, minereffort = @minereffort, confirmationprogress = @confirmationprogress,
            transactionconfirmationdata = @transactionconfirmationdata, hash = @hash
            WHERE id = @id AND NOT EXISTS (
                SELECT 1 FROM blocks
                WHERE poolid = @poolid AND hash = @hash AND type = 'auxpow' AND id <> @id
            )";

        try
        {
            await con.ExecuteAsync(mapped.Type == "auxpow" ? auxPowPromotionQuery : query,
                mapped, tx);
        }

        catch(PostgresException ex) when(mapped.Type == "auxpow" &&
            ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Another recorder/payout node finalized the same DOGE child block between
            // reconciliation and update. Leave this claim untouched; a later payout cycle
            // will see the finalized row and orphan the superseded claim cleanly.
        }
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
        var query = $@"SELECT * FROM blocks WHERE poolid = @poolid AND status = ANY(@status) AND created < @before
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
        const string query = @"SELECT
            to_regclass('idx_blocks_auxpow_pool_hash') IS NOT NULL AND
            to_regclass('idx_blocks_auxpow_claim') IS NOT NULL AND
            to_regclass('idx_blocks_merged_parent_pool_hash') IS NOT NULL";

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
