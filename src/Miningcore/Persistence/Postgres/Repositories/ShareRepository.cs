using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Miningcore.Persistence.Postgres.Repositories;

public class ShareRepository : IShareRepository
{
    public ShareRepository(IMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly IMapper mapper;

    private sealed class AccountingGroupRow
    {
        public short ProjectionCount { get; set; }
        public string PayloadHash { get; set; }
    }

    private sealed class PpsCreditRow
    {
        public string PoolId { get; set; }
        public Guid AccountingId { get; set; }
        public string Address { get; set; }
        public decimal CalculatedAmount { get; set; }
        public decimal CreditedAmount { get; set; }
        public double Difficulty { get; set; }
        public double NetworkDifficulty { get; set; }
        public long RewardBasisSatoshis { get; set; }
        public DateTime Created { get; set; }
    }

    private sealed class AccountingGroupPruneRow
    {
        public int PrunedRows { get; set; }
        public bool HasMore { get; set; }
    }

    public Task<bool> HasShareAccountingSchemaAsync(IDbConnection con,
        CancellationToken ct)
    {
        const string query = @"WITH required_columns(
                relation_name, column_name, type_name, nullable,
                numeric_precision, numeric_scale) AS (VALUES
                ('shares', 'accountingid', 'uuid', true, NULL::int, NULL::int),
                ('shares', 'accountingrole', 'int2', true, NULL, NULL),
                ('shares', 'rewardbasissatoshis', 'int8', true, NULL, NULL),
                ('share_accounting_groups', 'accountingid', 'uuid', false, NULL, NULL),
                ('share_accounting_groups', 'projectioncount', 'int2', false, NULL, NULL),
                ('share_accounting_groups', 'payloadhash', 'bpchar', false, NULL, NULL),
                ('share_accounting_groups', 'created', 'timestamptz', false, NULL, NULL),
                ('pps_share_credits', 'poolid', 'text', false, NULL, NULL),
                ('pps_share_credits', 'accountingid', 'uuid', false, NULL, NULL),
                ('pps_share_credits', 'address', 'text', false, NULL, NULL),
                ('pps_share_credits', 'calculatedamount', 'numeric', false, 38, 24),
                ('pps_share_credits', 'creditedamount', 'numeric', false, 28, 12),
                ('pps_share_credits', 'difficulty', 'float8', false, NULL, NULL),
                ('pps_share_credits', 'networkdifficulty', 'float8', false, NULL, NULL),
                ('pps_share_credits', 'rewardbasissatoshis', 'int8', false, NULL, NULL),
                ('pps_share_credits', 'created', 'timestamptz', false, NULL, NULL),
                ('pps_credit_remainders', 'poolid', 'text', false, NULL, NULL),
                ('pps_credit_remainders', 'address', 'text', false, NULL, NULL),
                ('pps_credit_remainders', 'amount', 'numeric', false, 38, 24),
                ('pps_credit_remainders', 'updated', 'timestamptz', false, NULL, NULL)
            ), missing_columns AS (
                SELECT required.*
                FROM required_columns required
                LEFT JOIN information_schema.columns actual
                  ON actual.table_schema = current_schema()
                 AND actual.table_name = required.relation_name
                 AND actual.column_name = required.column_name
                 AND actual.udt_name = required.type_name
                 AND (actual.is_nullable = 'YES') = required.nullable
                 AND (required.numeric_precision IS NULL OR
                      (actual.numeric_precision = required.numeric_precision AND
                       actual.numeric_scale = required.numeric_scale))
                WHERE actual.column_name IS NULL
            )
            SELECT NOT EXISTS(SELECT 1 FROM missing_columns)
            AND EXISTS (
                SELECT 1 FROM pg_index index_record
                WHERE index_record.indrelid = to_regclass('shares')
                  AND index_record.indisunique
                  AND index_record.indisvalid
                  AND index_record.indisready
                  AND index_record.indnkeyatts = 2
                  AND ARRAY(
                      SELECT attribute.attname::text
                      FROM unnest(index_record.indkey)
                           WITH ORDINALITY key(attnum, position)
                      JOIN pg_attribute attribute
                        ON attribute.attrelid = index_record.indrelid
                       AND attribute.attnum = key.attnum
                      WHERE key.position <= index_record.indnkeyatts
                      ORDER BY key.position) = ARRAY['poolid', 'accountingid']
                  AND pg_get_expr(index_record.indpred,
                      index_record.indrelid) = '(accountingid IS NOT NULL)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('share_accounting_groups')
                  AND contype = 'p' AND convalidated
                  AND pg_get_constraintdef(oid) = 'PRIMARY KEY (accountingid)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND contype = 'p' AND convalidated
                  AND pg_get_constraintdef(oid) =
                      'PRIMARY KEY (poolid, accountingid)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND contype = 'f' AND convalidated
                  AND pg_get_constraintdef(oid) =
                      'FOREIGN KEY (accountingid) REFERENCES share_accounting_groups(accountingid)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_credit_remainders')
                  AND contype = 'p' AND convalidated
                  AND pg_get_constraintdef(oid) =
                      'PRIMARY KEY (poolid, address)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('share_accounting_groups')
                  AND conname = 'ck_share_accounting_projection_count'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('share_accounting_groups')
                  AND conname = 'ck_share_accounting_payload_hash'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('shares')
                  AND conname = 'ck_shares_accounting_tuple'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('shares')
                  AND conname = 'fk_shares_accounting_group'
                  AND contype = 'f' AND convalidated
                  AND pg_get_constraintdef(oid) =
                      'FOREIGN KEY (accountingid) REFERENCES share_accounting_groups(accountingid)')
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND conname = 'ck_pps_calculated_amount'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND conname = 'ck_pps_credited_amount'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND conname = 'ck_pps_difficulty'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND conname = 'ck_pps_network_difficulty'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_share_credits')
                  AND conname = 'ck_pps_reward_basis'
                  AND contype = 'c' AND convalidated)
            AND EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = to_regclass('pps_credit_remainders')
                  AND conname = 'ck_pps_remainder_range'
                  AND contype = 'c' AND convalidated)
            AND NOT EXISTS (
                SELECT required.name
                FROM (VALUES
                    ('idx_shares_accounting'),
                    ('idx_share_accounting_groups_created'),
                    ('idx_pps_share_credits_accounting'),
                    ('idx_pps_share_credits_created'),
                    ('idx_balance_changes_pps_created')) AS required(name)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM pg_class index_relation
                    JOIN pg_namespace namespace
                      ON namespace.oid = index_relation.relnamespace
                    JOIN pg_index index_record
                      ON index_record.indexrelid = index_relation.oid
                    WHERE namespace.nspname = current_schema()
                      AND index_relation.relname = required.name
                      AND index_record.indisvalid
                      AND index_record.indisready))";

        return con.QuerySingleAsync<bool>(new CommandDefinition(query,
            cancellationToken: ct));
    }

    public async Task<string[]> GetMissingSharePartitionsAsync(IDbConnection con,
        IEnumerable<string> poolIds, CancellationToken ct)
    {
        const string query = @"WITH configured_pool AS (
                SELECT DISTINCT unnest(@poolIds::text[]) AS poolid
            ),
            shares_relation AS (
                SELECT relation.oid, relation.relkind
                FROM pg_class relation
                WHERE relation.oid = to_regclass('shares')
            ),
            partition_bound AS (
                SELECT pg_get_expr(child.relpartbound, child.oid, true) AS expression
                FROM shares_relation parent
                JOIN pg_inherits inheritance
                  ON inheritance.inhparent = parent.oid
                JOIN pg_class child
                  ON child.oid = inheritance.inhrelid
            )
            SELECT configured_pool.poolid
            FROM configured_pool
            WHERE EXISTS (
                    SELECT 1 FROM shares_relation WHERE relkind = 'p')
              AND NOT EXISTS (
                    SELECT 1
                    FROM partition_bound
                    WHERE expression = 'DEFAULT'
                       OR strpos(expression,
                           quote_literal(configured_pool.poolid)) > 0)
            ORDER BY configured_pool.poolid";

        var ids = poolIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? Array.Empty<string>();

        if(ids.Length == 0)
            return Array.Empty<string>();

        return (await con.QueryAsync<string>(new CommandDefinition(query,
            new { poolIds = ids }, cancellationToken: ct))).ToArray();
    }

    public Task<bool> HasRecoveryImportSchemaAsync(IDbConnection con,
        CancellationToken ct)
    {
        const string query = @"SELECT EXISTS (
            SELECT 1
            FROM pg_constraint constraint_record
            JOIN pg_class relation
              ON relation.oid = constraint_record.conrelid
            JOIN pg_index index_record
              ON index_record.indexrelid = constraint_record.conindid
            WHERE relation.oid = to_regclass('share_recovery_imports')
              AND relation.relkind IN ('r', 'p')
              AND constraint_record.contype = 'p'
              AND NOT constraint_record.condeferrable
              AND index_record.indisunique
              AND index_record.indisvalid
              AND index_record.indisready
              AND index_record.indimmediate
              AND pg_get_constraintdef(constraint_record.oid) ILIKE
                  'PRIMARY KEY (filehash)%'
              AND EXISTS (
                  SELECT 1 FROM pg_attribute attribute
                  WHERE attribute.attrelid = relation.oid
                    AND attribute.attname = 'filehash'
                    AND attribute.atttypid = 'text'::regtype
                    AND attribute.attnotnull AND NOT attribute.attisdropped)
              AND EXISTS (
                  SELECT 1 FROM pg_attribute attribute
                  WHERE attribute.attrelid = relation.oid
                    AND attribute.attname = 'filename'
                    AND attribute.atttypid = 'text'::regtype
                    AND attribute.attnotnull AND NOT attribute.attisdropped)
              AND EXISTS (
                  SELECT 1 FROM pg_attribute attribute
                  WHERE attribute.attrelid = relation.oid
                    AND attribute.attname = 'recordcount'
                    AND attribute.atttypid = 'integer'::regtype
                    AND attribute.attnotnull AND NOT attribute.attisdropped)
              AND EXISTS (
                  SELECT 1 FROM pg_attribute attribute
                  WHERE attribute.attrelid = relation.oid
                    AND attribute.attname = 'created'
                    AND attribute.atttypid = 'timestamp with time zone'::regtype
                    AND attribute.attnotnull AND NOT attribute.attisdropped)
        )";

        return con.QuerySingleAsync<bool>(new CommandDefinition(query,
            cancellationToken: ct));
    }

    public async Task<bool> TryRegisterRecoveryImportAsync(IDbConnection con,
        IDbTransaction tx, string fileHash, string filename, int recordCount,
        CancellationToken ct)
    {
        const string query = @"INSERT INTO share_recovery_imports(
                filehash, filename, recordcount, created)
            VALUES(@fileHash, @filename, @recordCount, now())
            ON CONFLICT(filehash) DO NOTHING";

        return await con.ExecuteAsync(new CommandDefinition(query, new
        {
            fileHash,
            filename,
            recordCount,
        }, tx, cancellationToken: ct)) > 0;
    }

    public Task<bool> HasMatchingRecoveryImportAsync(IDbConnection con,
        string fileHash, string filename, int recordCount,
        CancellationToken ct)
    {
        const string query = @"SELECT EXISTS (
            SELECT 1
            FROM share_recovery_imports
            WHERE filehash = @fileHash
              AND filename = @filename
              AND recordcount = @recordCount)";

        return con.QuerySingleAsync<bool>(new CommandDefinition(query, new
        {
            fileHash,
            filename,
            recordCount,
        }, cancellationToken: ct));
    }

    public async Task BatchInsertAsync(IDbConnection con, IDbTransaction tx, IEnumerable<Share> shares, CancellationToken ct)
    {
        // NOTE: Even though the tx parameter is completely ignored here,
        // the COPY command still honors a current ambient transaction

        var pgCon = (NpgsqlConnection) con;

        const string query = @"COPY shares (poolid, blockheight, difficulty,
            networkdifficulty, sharedifficulty, actualdifficulty, miner, worker, useragent, ipaddress, source, sessionid,
            created)
            FROM STDIN (FORMAT BINARY)";

        await using(var writer = await pgCon.BeginBinaryImportAsync(query, ct))
        {
            foreach(var share in shares)
            {
                if(share.AccountingId.HasValue || share.AccountingRole.HasValue ||
                   share.RewardBasisSatoshis.HasValue)
                    throw new InvalidDataException(
                        "Ordinary share COPY must not receive accounting projections");

                await writer.StartRowAsync(ct);

                await writer.WriteAsync(share.PoolId, ct);
                await writer.WriteAsync((long) share.BlockHeight, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(share.Difficulty, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(share.NetworkDifficulty, NpgsqlDbType.Double, ct);

                if(share.ShareDifficulty.HasValue)
                    await writer.WriteAsync(share.ShareDifficulty.Value, NpgsqlDbType.Double, ct);
                else
                    await writer.WriteNullAsync(ct);

                if(share.ActualDifficulty.HasValue)
                    await writer.WriteAsync(share.ActualDifficulty.Value, NpgsqlDbType.Double, ct);
                else
                    await writer.WriteNullAsync(ct);

                await writer.WriteAsync(share.Miner, ct);

                if(string.IsNullOrEmpty(share.Worker))
                    await writer.WriteNullAsync(ct);
                else
                    await writer.WriteAsync(share.Worker, ct);

                if(string.IsNullOrEmpty(share.UserAgent))
                    await writer.WriteNullAsync(ct);
                else
                    await writer.WriteAsync(share.UserAgent, ct);

                await writer.WriteAsync(share.IpAddress, ct);

                if(string.IsNullOrEmpty(share.Source))
                    await writer.WriteNullAsync(ct);
                else
                    await writer.WriteAsync(share.Source, ct);

                if(string.IsNullOrEmpty(share.SessionId))
                    await writer.WriteNullAsync(ct);
                else
                    await writer.WriteAsync(share.SessionId, ct);

                await writer.WriteAsync(share.Created, NpgsqlDbType.TimestampTz, ct);
            }

            await writer.CompleteAsync(ct);
        }
    }

    public async Task<ShareAccountingInsertResult> InsertAccountingBatchAsync(
        IDbConnection con, IDbTransaction tx, ShareAccountingBatch batch,
        CancellationToken ct)
    {
        var results = await InsertAccountingBatchesAsync(con, tx,
            new[] { batch }, ct);
        return results[0];
    }

    public async Task<ShareAccountingInsertResult[]> InsertAccountingBatchesAsync(
        IDbConnection con, IDbTransaction tx,
        IReadOnlyList<ShareAccountingBatch> batches, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if(batches.Count == 0)
            return Array.Empty<ShareAccountingInsertResult>();

        foreach(var batch in batches)
            ValidateAccountingBatch(batch);

        if(batches.Select(x => x.AccountingId).Distinct().Count() != batches.Count)
            throw new InvalidDataException(
                "One persistence batch contains duplicate share-accounting ids");

        const string register = @"INSERT INTO share_accounting_groups(
                accountingid, projectioncount, payloadhash, created)
            SELECT accountingid, projectioncount, payloadhash, created
            FROM unnest(@AccountingIds::uuid[],
                @ProjectionCounts::smallint[], @PayloadHashes::text[],
                @CreatedValues::timestamptz[], @ReceiptCutoffs::timestamptz[])
                AS candidate(accountingid, projectioncount, payloadhash, created,
                    receiptcutoff)
            WHERE created >= receiptcutoff
            ON CONFLICT(accountingid) DO NOTHING
            RETURNING accountingid";
        var insertedIds = (await con.QueryAsync<Guid>(new CommandDefinition(
            register, new
            {
                AccountingIds = batches.Select(x => x.AccountingId).ToArray(),
                ProjectionCounts = batches.Select(x =>
                    checked((short) x.Shares.Length)).ToArray(),
                PayloadHashes = batches.Select(x => x.PayloadHash).ToArray(),
                CreatedValues = batches.Select(x => x.Created).ToArray(),
                ReceiptCutoffs = batches.Select(x =>
                    x.NewReceiptNotBefore).ToArray(),
            }, tx, cancellationToken: ct))).ToHashSet();

        foreach(var batch in batches.Where(x => !insertedIds.Contains(
                    x.AccountingId)))
            await VerifyCommittedAccountingBatchAsync(con, tx, batch, ct);

        var insertedBatches = batches.Where(x => insertedIds.Contains(
                x.AccountingId)).ToArray();
        if(insertedBatches.Length > 0)
        {
            await InsertAccountingSharesAsync(con, tx, insertedBatches, ct);
            await InsertPpsCreditsAsync(con, tx, insertedBatches, ct);
        }

        return batches.Select(x => insertedIds.Contains(x.AccountingId)
            ? ShareAccountingInsertResult.Inserted
            : ShareAccountingInsertResult.AlreadyCommitted).ToArray();
    }

    private static void ValidateAccountingBatch(ShareAccountingBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if(batch.AccountingId == Guid.Empty ||
           batch.Shares is not { Length: 1 or 2 } ||
           batch.PpsCredits == null || batch.PayloadHash?.Length != 64 ||
           !batch.PayloadHash.All(x => x is >= '0' and <= '9' or >= 'A' and <= 'F') ||
           batch.Shares.Any(x => x == null || x.BlockHeight > long.MaxValue) ||
           batch.Created.Kind != DateTimeKind.Utc ||
           (batch.NewReceiptNotBefore != DateTime.MinValue &&
               batch.NewReceiptNotBefore.Kind != DateTimeKind.Utc) ||
           batch.PpsCredits.Any(x => x == null ||
               x.AccountingId != batch.AccountingId ||
               string.IsNullOrWhiteSpace(x.PoolId) ||
               string.IsNullOrWhiteSpace(x.Address) || x.CalculatedAmount <= 0 ||
               !double.IsFinite(x.Difficulty) || x.Difficulty <= 0 ||
               !double.IsFinite(x.NetworkDifficulty) || x.NetworkDifficulty <= 0 ||
               x.RewardBasisSatoshis <= 0))
            throw new InvalidDataException("Malformed share-accounting batch");
    }

    private static async Task InsertAccountingSharesAsync(IDbConnection con,
        IDbTransaction tx, IReadOnlyList<ShareAccountingBatch> batches,
        CancellationToken ct)
    {
        var shares = batches.SelectMany(x => x.Shares).ToArray();
        const string query = @"INSERT INTO shares(poolid, blockheight,
                difficulty, networkdifficulty, sharedifficulty, actualdifficulty,
                miner, worker, useragent, ipaddress, source, sessionid,
                accountingid, accountingrole, rewardbasissatoshis, created)
            SELECT * FROM unnest(@PoolIds::text[], @BlockHeights::bigint[],
                @Difficulties::double precision[],
                @NetworkDifficulties::double precision[],
                @ShareDifficulties::double precision[],
                @ActualDifficulties::double precision[], @Miners::text[],
                @Workers::text[], @UserAgents::text[], @IpAddresses::text[],
                @Sources::text[], @SessionIds::text[], @AccountingIds::uuid[],
                @AccountingRoles::smallint[], @RewardBases::bigint[],
                @CreatedValues::timestamptz[])";
        await con.ExecuteAsync(new CommandDefinition(query, new
        {
            PoolIds = shares.Select(x => x.PoolId).ToArray(),
            BlockHeights = shares.Select(x => checked((long) x.BlockHeight)).ToArray(),
            Difficulties = shares.Select(x => x.Difficulty).ToArray(),
            NetworkDifficulties = shares.Select(x => x.NetworkDifficulty).ToArray(),
            ShareDifficulties = shares.Select(x => x.ShareDifficulty).ToArray(),
            ActualDifficulties = shares.Select(x => x.ActualDifficulty).ToArray(),
            Miners = shares.Select(x => x.Miner).ToArray(),
            Workers = shares.Select(x => x.Worker).ToArray(),
            UserAgents = shares.Select(x => x.UserAgent).ToArray(),
            IpAddresses = shares.Select(x => x.IpAddress).ToArray(),
            Sources = shares.Select(x => x.Source).ToArray(),
            SessionIds = shares.Select(x => x.SessionId).ToArray(),
            AccountingIds = shares.Select(x => x.AccountingId.Value).ToArray(),
            AccountingRoles = shares.Select(x => x.AccountingRole.Value).ToArray(),
            RewardBases = shares.Select(x => x.RewardBasisSatoshis.Value).ToArray(),
            CreatedValues = shares.Select(x => x.Created).ToArray(),
        }, tx, cancellationToken: ct));
    }

    private static async Task InsertPpsCreditsAsync(IDbConnection con,
        IDbTransaction tx, IReadOnlyList<ShareAccountingBatch> batches,
        CancellationToken ct)
    {
        var credits = batches.SelectMany(x => x.PpsCredits)
            .OrderBy(x => x.PoolId, StringComparer.Ordinal)
            .ThenBy(x => x.Address, StringComparer.Ordinal)
            .ThenBy(x => x.Created)
            .ThenBy(x => x.AccountingId)
            .ToArray();
        if(credits.Length == 0)
            return;

        const string seed = @"INSERT INTO pps_credit_remainders(
                poolid, address, amount, updated)
            SELECT poolid, address, 0, created
            FROM (SELECT poolid, address, min(created) AS created
                FROM unnest(@PoolIds::text[], @Addresses::text[],
                    @CreatedValues::timestamptz[])
                    AS input(poolid, address, created)
                GROUP BY poolid, address) recipients
            ORDER BY poolid, address
            ON CONFLICT(poolid, address) DO NOTHING";
        await con.ExecuteAsync(new CommandDefinition(seed, new
        {
            PoolIds = credits.Select(x => x.PoolId).ToArray(),
            Addresses = credits.Select(x => x.Address).ToArray(),
            CreatedValues = credits.Select(x => x.Created).ToArray(),
        }, tx, cancellationToken: ct));

        // Lock recipients in one stable order. This prevents deadlocks between recorder batches
        // while reducing the normal 250-share path to a bounded number of database round trips.
        const string lockRemainders = @"WITH recipients AS MATERIALIZED (
                SELECT DISTINCT poolid, address
                FROM unnest(@PoolIds::text[], @Addresses::text[])
                    AS input(poolid, address)
            )
            SELECT remainder.poolid
            FROM pps_credit_remainders remainder
            JOIN recipients USING(poolid, address)
            ORDER BY remainder.poolid, remainder.address
            FOR UPDATE OF remainder";
        var lockedRecipients = (await con.QueryAsync<string>(new CommandDefinition(lockRemainders,
            new
            {
                PoolIds = credits.Select(x => x.PoolId).ToArray(),
                Addresses = credits.Select(x => x.Address).ToArray(),
            }, tx, cancellationToken: ct))).AsList();
        var recipientCount = credits.Select(x => (x.PoolId, x.Address))
            .Distinct().Count();
        if(lockedRecipients.Count != recipientCount)
            throw new InvalidDataException(
                "PPS remainder locking did not cover every credit recipient");

        const string apply = @"WITH input AS (
                SELECT *
                FROM unnest(@PoolIds::text[], @AccountingIds::uuid[],
                    @Addresses::text[], @CalculatedAmounts::numeric[],
                    @Difficulties::double precision[],
                    @NetworkDifficulties::double precision[],
                    @RewardBases::bigint[], @CreatedValues::timestamptz[])
                AS value(poolid, accountingid, address, calculatedamount,
                    difficulty, networkdifficulty, rewardbasissatoshis, created)
            ), running AS (
                SELECT input.*, remainder.amount +
                        sum(calculatedamount) OVER recipient_window AS accumulated,
                    remainder.amount + COALESCE(sum(calculatedamount) OVER (
                        PARTITION BY input.poolid, input.address
                        ORDER BY input.created, input.accountingid
                        ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0)
                        AS previous
                FROM input
                JOIN pps_credit_remainders remainder USING(poolid, address)
                WINDOW recipient_window AS (PARTITION BY input.poolid, input.address
                    ORDER BY input.created, input.accountingid
                    ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
            ), calculated AS (
                SELECT running.*,
                    trunc(accumulated, 12) - trunc(previous, 12) AS creditedamount
                FROM running
            ), inserted_credits AS (
                INSERT INTO pps_share_credits(poolid, accountingid, address,
                    calculatedamount, creditedamount, difficulty,
                    networkdifficulty, rewardbasissatoshis, created)
                SELECT poolid, accountingid, address, calculatedamount,
                    creditedamount, difficulty, networkdifficulty,
                    rewardbasissatoshis, created
                FROM calculated
                RETURNING *
            ), inserted_changes AS (
                INSERT INTO balance_changes(poolid, address, amount, usage,
                    tags, created)
                SELECT poolid, address, creditedamount, 'PPS share credit',
                    ARRAY['pps', 'pps-share:' || replace(accountingid::text, '-', '')],
                    created
                FROM inserted_credits WHERE creditedamount > 0
                RETURNING poolid, address, amount, created
            ), balance_totals AS (
                SELECT poolid, address, sum(amount) AS amount,
                    min(created) AS created, max(created) AS updated
                FROM inserted_changes GROUP BY poolid, address
            ), updated_balances AS (
                INSERT INTO balances(poolid, address, amount, created, updated)
                SELECT poolid, address, amount, created, updated
                FROM balance_totals ORDER BY poolid, address
                ON CONFLICT(poolid, address) DO UPDATE
                SET amount = balances.amount + EXCLUDED.amount,
                    updated = EXCLUDED.updated
                RETURNING poolid
            ), final_remainders AS (
                SELECT poolid, address,
                    max(accumulated) - trunc(max(accumulated), 12) AS amount,
                    max(created) AS updated
                FROM running GROUP BY poolid, address
            ), updated_remainders AS (
                UPDATE pps_credit_remainders remainder
                SET amount = final.amount, updated = final.updated
                FROM final_remainders final
                WHERE remainder.poolid = final.poolid
                  AND remainder.address = final.address
                RETURNING remainder.poolid
            )
            SELECT (SELECT count(*) FROM inserted_credits) AS creditcount,
                (SELECT count(*) FROM updated_remainders) AS remaindercount,
                (SELECT count(*) FROM updated_balances) AS balancecount";
        var applied = await con.QuerySingleAsync<PpsApplyCounts>(
            new CommandDefinition(apply, new
        {
            PoolIds = credits.Select(x => x.PoolId).ToArray(),
            AccountingIds = credits.Select(x => x.AccountingId).ToArray(),
            Addresses = credits.Select(x => x.Address).ToArray(),
            CalculatedAmounts = credits.Select(x => x.CalculatedAmount).ToArray(),
            Difficulties = credits.Select(x => x.Difficulty).ToArray(),
            NetworkDifficulties = credits.Select(x => x.NetworkDifficulty).ToArray(),
            RewardBases = credits.Select(x => x.RewardBasisSatoshis).ToArray(),
            CreatedValues = credits.Select(x => x.Created).ToArray(),
        }, tx, cancellationToken: ct));
        if(applied.CreditCount != credits.Length ||
           applied.RemainderCount != recipientCount ||
           applied.BalanceCount > recipientCount)
            throw new InvalidDataException(
                "PPS credit application did not update the complete batch");
    }

    private static async Task VerifyCommittedAccountingBatchAsync(IDbConnection con,
        IDbTransaction tx, ShareAccountingBatch batch, CancellationToken ct)
    {
        const string groupQuery = @"SELECT projectioncount, payloadhash
            FROM share_accounting_groups WHERE accountingid = @AccountingId";
        var group = await con.QuerySingleOrDefaultAsync<AccountingGroupRow>(
            new CommandDefinition(groupQuery, new { batch.AccountingId }, tx,
                cancellationToken: ct));

        if(group == null || group.ProjectionCount != batch.Shares.Length ||
           !string.Equals(group.PayloadHash, batch.PayloadHash,
               StringComparison.Ordinal))
        {
            if(group == null && batch.Created < batch.NewReceiptNotBefore)
                throw new InvalidDataException(
                    $"Share accounting id {batch.AccountingId:N} is older than the transactional replay horizon and has no retained receipt; preserve it for manual financial reconciliation");

            throw new InvalidDataException(
                $"Share accounting id {batch.AccountingId:N} conflicts with committed evidence");
        }

        const string shareCountQuery = @"SELECT count(*) FROM shares
            WHERE accountingid = @AccountingId";
        var shareCount = await con.QuerySingleAsync<int>(new CommandDefinition(
            shareCountQuery, new { batch.AccountingId }, tx, cancellationToken: ct));
        const string creditCountQuery = @"SELECT count(*) FROM pps_share_credits
            WHERE accountingid = @AccountingId";
        var creditCount = await con.QuerySingleAsync<int>(new CommandDefinition(
            creditCountQuery, new { batch.AccountingId }, tx, cancellationToken: ct));

        // Each pool prunes settled share rows on its own payout boundary. A paired group may
        // therefore retain any subset of its original projections. The group and all original
        // rows were inserted in one transaction, so the durable payload hash is proof that a
        // smaller current set is retention, not a partial initial commit. PPS credits are durable
        // replay evidence and must remain complete.
        if(shareCount > batch.Shares.Length ||
           creditCount != batch.PpsCredits.Length)
            throw new InvalidDataException(
                $"Share accounting id {batch.AccountingId:N} is incomplete; preserve recovery evidence and stop");

        if(shareCount > 0)
        {
            const string shareQuery = @"SELECT * FROM shares
                WHERE accountingid = @AccountingId";
            var committedShares = (await con.QueryAsync<Entities.Share>(
                new CommandDefinition(shareQuery, new { batch.AccountingId }, tx,
                    cancellationToken: ct))).ToArray();

            foreach(var actual in committedShares)
            {
                var expected = batch.Shares.SingleOrDefault(x =>
                    string.Equals(x.PoolId, actual.PoolId, StringComparison.Ordinal));
                if(expected == null || actual.AccountingId != expected.AccountingId ||
                   actual.BlockHeight < 0 ||
                   (ulong) actual.BlockHeight != expected.BlockHeight ||
                   !string.Equals(actual.Miner, expected.Miner, StringComparison.Ordinal) ||
                   !string.Equals(actual.Worker, expected.Worker, StringComparison.Ordinal) ||
                   !string.Equals(actual.UserAgent, expected.UserAgent, StringComparison.Ordinal) ||
                   !string.Equals(actual.IpAddress, expected.IpAddress, StringComparison.Ordinal) ||
                   !string.Equals(actual.Source, expected.Source, StringComparison.Ordinal) ||
                   !string.Equals(actual.SessionId, expected.SessionId, StringComparison.Ordinal) ||
                   actual.Difficulty != expected.Difficulty ||
                   actual.ShareDifficulty != expected.ShareDifficulty ||
                   actual.ActualDifficulty != expected.ActualDifficulty ||
                   actual.NetworkDifficulty != expected.NetworkDifficulty ||
                   actual.AccountingRole != expected.AccountingRole ||
                   actual.RewardBasisSatoshis != expected.RewardBasisSatoshis ||
                   TruncateToPostgresTimestamp(actual.Created) !=
                   TruncateToPostgresTimestamp(expected.Created))
                    throw new InvalidDataException(
                        $"Share accounting id {batch.AccountingId:N} conflicts with committed projection evidence");
            }
        }

        const string creditQuery = @"SELECT * FROM pps_share_credits
            WHERE accountingid = @AccountingId";
        var committedCredits = (await con.QueryAsync<PpsCreditRow>(
            new CommandDefinition(creditQuery, new { batch.AccountingId }, tx,
                cancellationToken: ct))).ToArray();

        foreach(var expected in batch.PpsCredits)
        {
            var actual = committedCredits.SingleOrDefault(x =>
                string.Equals(x.PoolId, expected.PoolId, StringComparison.Ordinal));
            if(actual == null || actual.AccountingId != expected.AccountingId ||
               !string.Equals(actual.Address, expected.Address, StringComparison.Ordinal) ||
               actual.CalculatedAmount != expected.CalculatedAmount ||
               actual.Difficulty != expected.Difficulty ||
               actual.NetworkDifficulty != expected.NetworkDifficulty ||
               actual.RewardBasisSatoshis != expected.RewardBasisSatoshis ||
               TruncateToPostgresTimestamp(actual.Created) !=
               TruncateToPostgresTimestamp(expected.Created))
                throw new InvalidDataException(
                    $"PPS accounting id {batch.AccountingId:N} conflicts with committed credit evidence");
        }
    }

    private static DateTime TruncateToPostgresTimestamp(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, value.Kind);

    public async Task<Share[]> ReadSharesBeforeAsync(IDbConnection con, string poolId, DateTime before,
        bool inclusive, int pageSize, CancellationToken ct)
    {
        var query = @$"SELECT * FROM shares WHERE poolid = @poolId AND created {(inclusive ? " <= " : " < ")} @before
            ORDER BY created DESC FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.Share>(new CommandDefinition(query, new { poolId, before, pageSize }, cancellationToken: ct)))
            .Select(mapper.Map<Share>)
            .ToArray();
    }

    public Task<long> CountSharesBeforeAsync(IDbConnection con, IDbTransaction tx, string poolId, DateTime before, CancellationToken ct)
    {
        const string query = "SELECT count(*) FROM shares WHERE poolid = @poolId AND created < @before";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new { poolId, before }, tx, cancellationToken: ct));
    }

    public Task<long> CountSharesBeforeInclusiveAsync(IDbConnection con, IDbTransaction tx,
        string poolId, DateTime before, CancellationToken ct)
    {
        const string query =
            "SELECT count(*) FROM shares WHERE poolid = @poolId AND created <= @before";

        return con.QuerySingleAsync<long>(new CommandDefinition(query,
            new { poolId, before }, tx, cancellationToken: ct));
    }

    public Task<long> CountSharesByMinerAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = "SELECT count(*) FROM shares WHERE poolid = @poolId AND miner = @miner";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new { poolId, miner}, tx, cancellationToken: ct));
    }

    public Task<double?> GetEffortBetweenCreatedAsync(IDbConnection con, string poolId, double shareConst, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM((difficulty * @shareConst) / networkdifficulty) FROM shares WHERE poolid = @poolId AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, shareConst, start, end }, cancellationToken: ct));
    }

    public Task<double?> GetMinerEffortBetweenCreatedAsync(IDbConnection con, string poolId, string miner, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty / networkdifficulty) FROM shares WHERE poolid = @poolId AND miner = @miner AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, miner, start, end }, cancellationToken: ct));
    }

    public async Task DeleteSharesByMinerAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = "DELETE FROM shares WHERE poolid = @poolId AND miner = @miner";

        await con.ExecuteAsync(new CommandDefinition(query, new { poolId, miner}, tx, cancellationToken: ct));
    }

    public async Task DeleteSharesBeforeAsync(IDbConnection con, IDbTransaction tx, string poolId, DateTime before, CancellationToken ct)
    {
        const string query = "DELETE FROM shares WHERE poolid = @poolId AND created < @before";

        await con.ExecuteAsync(new CommandDefinition(query, new { poolId, before }, tx, cancellationToken: ct));
    }

    public async Task DeleteSharesBeforeInclusiveAsync(IDbConnection con, IDbTransaction tx,
        string poolId, DateTime before, CancellationToken ct)
    {
        const string query =
            "DELETE FROM shares WHERE poolid = @poolId AND created <= @before";

        await con.ExecuteAsync(new CommandDefinition(query, new { poolId, before }, tx,
            cancellationToken: ct));
    }

    public async Task<ShareAccountingPruneResult> PruneSharesBeforeInclusiveAsync(
        IDbConnection con, IDbTransaction tx, string poolId, DateTime before,
        int batchSize, CancellationToken ct)
    {
        if(batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        const string query = @"WITH candidates AS (
                SELECT ctid FROM shares
                WHERE poolid = @poolId AND created <= @before
                ORDER BY created, ctid LIMIT @batchSize), deleted AS (
                DELETE FROM shares value USING candidates
                WHERE value.poolid = @poolId
                  AND value.ctid = candidates.ctid RETURNING 1)
            SELECT count(*)::int FROM deleted;
            SELECT EXISTS(SELECT 1 FROM shares
                WHERE poolid = @poolId AND created <= @before)";
        using var grid = await con.QueryMultipleAsync(new CommandDefinition(query,
            new { poolId, before, batchSize }, tx, cancellationToken: ct));
        return new ShareAccountingPruneResult(
            await grid.ReadSingleAsync<int>(), await grid.ReadSingleAsync<bool>());
    }

    public Task<ShareAccountingPruneResult> PruneShareAccountingEvidenceBeforeAsync(
        IDbConnection con, IDbTransaction tx, DateTime before, int batchSize,
        CancellationToken ct)
    {
        // The replay-age check rejects old relay and recovery records before persistence. Once
        // that horizon passes, detailed liabilities and compact group receipts may be pruned
        // without allowing a duplicate credit. Operators that require longer audit retention
        // archive these rows first using the documented database procedure. Keep remainder rows:
        // they are one per recipient and preserve exact sub-unit carry across retention windows.
        if(batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        const string query = @"WITH candidates AS (
                SELECT id FROM balance_changes
                WHERE usage = 'PPS share credit' AND created <= @before
                ORDER BY created, id LIMIT @batchSize), deleted AS (
                DELETE FROM balance_changes changes USING candidates
                WHERE changes.id = candidates.id
                RETURNING 1)
            SELECT count(*)::int FROM deleted;
            WITH candidates AS (
                SELECT poolid, accountingid FROM pps_share_credits
                WHERE created <= @before
                ORDER BY created, poolid, accountingid LIMIT @batchSize),
            deleted AS (
                DELETE FROM pps_share_credits credits USING candidates
                WHERE credits.poolid = candidates.poolid
                  AND credits.accountingid = candidates.accountingid
                RETURNING 1)
            SELECT count(*)::int FROM deleted;
            WITH expiring AS MATERIALIZED (
                SELECT accounting.accountingid,
                    row_number() OVER (ORDER BY accounting.created,
                        accounting.accountingid) AS position
                FROM share_accounting_groups accounting
                WHERE accounting.created <= @before
                ORDER BY accounting.created, accounting.accountingid
                LIMIT @candidateScanSize), candidates AS (
                SELECT expiring.accountingid
                FROM expiring
                WHERE expiring.position <= @batchSize
                  AND NOT EXISTS (SELECT 1 FROM shares
                      WHERE shares.accountingid = expiring.accountingid)
                  AND NOT EXISTS (SELECT 1 FROM pps_share_credits credits
                      WHERE credits.accountingid = expiring.accountingid)),
            deleted AS (
                DELETE FROM share_accounting_groups accounting USING candidates
                WHERE accounting.accountingid = candidates.accountingid
                RETURNING accounting.accountingid)
            SELECT (SELECT count(*)::int FROM deleted) AS PrunedRows,
                EXISTS(SELECT 1 FROM expiring
                    LEFT JOIN deleted USING(accountingid)
                    WHERE deleted.accountingid IS NULL) AS HasMore;
            SELECT EXISTS(SELECT 1 FROM balance_changes
                    WHERE usage = 'PPS share credit' AND created <= @before)
                OR EXISTS(SELECT 1 FROM pps_share_credits
                    WHERE created <= @before)";
        return PruneAsync();

        async Task<ShareAccountingPruneResult> PruneAsync()
        {
            using var grid = await con.QueryMultipleAsync(new CommandDefinition(
                query, new
                {
                    before,
                    batchSize,
                    candidateScanSize = (long) batchSize + 1,
                }, tx, cancellationToken: ct));
            var pruned = await grid.ReadSingleAsync<int>() +
                await grid.ReadSingleAsync<int>();
            var groupResult = await grid.ReadSingleAsync<AccountingGroupPruneRow>();
            pruned += groupResult.PrunedRows;
            var tableHasMore = await grid.ReadSingleAsync<bool>();
            var hasMore = groupResult.HasMore || tableHasMore;
            return new ShareAccountingPruneResult(pruned, hasMore);
        }
    }

    private sealed class PpsApplyCounts
    {
        public int CreditCount { get; init; }
        public int RemainderCount { get; init; }
        public int BalanceCount { get; init; }
    }

    public Task<double?> GetAccumulatedShareDifficultyBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty) FROM shares WHERE poolid = @poolId AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct));
    }

    public Task<double?> GetMinerShareDifficultyBetweenAsync(IDbConnection con, string poolId, string miner, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty / networkdifficulty) FROM shares WHERE poolid = @poolId AND miner = @miner AND created > @start AND created <= @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, miner, start, end }, cancellationToken: ct));
    }

    public Task<double?> GetEffectiveAccumulatedShareDifficultyBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty / networkdifficulty) FROM shares WHERE poolid = @poolId AND created > @start AND created <= @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct));
    }

    public async Task<MinerWorkerHashes[]> GetHashAccumulationBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"SELECT SUM(difficulty), COUNT(difficulty), MIN(created) AS firstshare, MAX(created) AS lastshare, miner, worker FROM shares
            WHERE poolid = @poolId AND created >= @start AND created <= @end
            GROUP BY miner, worker";

        return (await con.QueryAsync<MinerWorkerHashes>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct)))
            .ToArray();
    }

    public async Task<KeyValuePair<string, double>[]> GetAccumulatedUserAgentShareDifficultyBetweenAsync(
        IDbConnection con, string poolId, DateTime start, DateTime end, bool byVersion, CancellationToken ct)
    {
        const string query = @"SELECT SUM(difficulty) AS value, REGEXP_REPLACE(useragent, '/.+', '') AS key FROM shares
                WHERE poolid = @poolId AND created > @start AND created < @end
                GROUP BY key ORDER BY value DESC";

        const string queryByVersion = @"SELECT SUM(difficulty) AS value, useragent AS key FROM shares
            WHERE poolid = @poolId AND created > @start AND created < @end
            GROUP BY key ORDER BY value DESC";

        return (await con.QueryAsync<KeyValuePair<string, double>>(new CommandDefinition(!byVersion ? query : queryByVersion, new { poolId, start, end }, cancellationToken: ct)))
            .ToArray();
    }

    public async Task<string[]> GetRecentyUsedIpAddressesAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = @"SELECT DISTINCT s.ipaddress FROM (SELECT * FROM shares
            WHERE poolid = @poolId and miner = @miner ORDER BY CREATED DESC LIMIT 100) s";

        return (await con.QueryAsync<string>(new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct)))
            .ToArray();
    }
}
