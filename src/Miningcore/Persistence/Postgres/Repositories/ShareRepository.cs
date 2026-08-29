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
                      SELECT attribute.attname
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
                  AND contype = 'c' AND convalidated)";

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
            accountingid, accountingrole, rewardbasissatoshis, created)
            FROM STDIN (FORMAT BINARY)";

        await using(var writer = await pgCon.BeginBinaryImportAsync(query, ct))
        {
            foreach(var share in shares)
            {
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

                if(share.AccountingId.HasValue)
                    await writer.WriteAsync(share.AccountingId.Value, NpgsqlDbType.Uuid, ct);
                else
                    await writer.WriteNullAsync(ct);

                if(share.AccountingRole.HasValue)
                    await writer.WriteAsync(share.AccountingRole.Value, NpgsqlDbType.Smallint, ct);
                else
                    await writer.WriteNullAsync(ct);

                if(share.RewardBasisSatoshis.HasValue)
                    await writer.WriteAsync(share.RewardBasisSatoshis.Value, NpgsqlDbType.Bigint, ct);
                else
                    await writer.WriteNullAsync(ct);

                await writer.WriteAsync(share.Created, NpgsqlDbType.TimestampTz, ct);
            }

            await writer.CompleteAsync(ct);
        }
    }

    public async Task<ShareAccountingInsertResult> InsertAccountingBatchAsync(
        IDbConnection con, IDbTransaction tx, ShareAccountingBatch batch,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if(batch.AccountingId == Guid.Empty || batch.Shares is not { Length: 1 or 2 } ||
           batch.PpsCredits == null || batch.PayloadHash?.Length != 64 ||
           !batch.PayloadHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Malformed share-accounting batch");

        const string register = @"INSERT INTO share_accounting_groups(
                accountingid, projectioncount, payloadhash, created)
            VALUES(@AccountingId, @ProjectionCount, @PayloadHash, @Created)
            ON CONFLICT(accountingid) DO NOTHING";
        var inserted = await con.ExecuteAsync(new CommandDefinition(register, new
        {
            batch.AccountingId,
            ProjectionCount = (short) batch.Shares.Length,
            batch.PayloadHash,
            batch.Created,
        }, tx, cancellationToken: ct)) > 0;

        if(!inserted)
        {
            await VerifyCommittedAccountingBatchAsync(con, tx, batch, ct);
            return ShareAccountingInsertResult.AlreadyCommitted;
        }

        const string insertShare = @"INSERT INTO shares(poolid, blockheight,
                difficulty, networkdifficulty, sharedifficulty, actualdifficulty,
                miner, worker, useragent, ipaddress, source, sessionid,
                accountingid, accountingrole, rewardbasissatoshis, created)
            VALUES(@PoolId, @BlockHeight, @Difficulty, @NetworkDifficulty,
                @ShareDifficulty, @ActualDifficulty, @Miner, @Worker, @UserAgent,
                @IpAddress, @Source, @SessionId, @AccountingId, @AccountingRole,
                @RewardBasisSatoshis, @Created)";

        foreach(var share in batch.Shares)
            await con.ExecuteAsync(new CommandDefinition(insertShare, share, tx,
                cancellationToken: ct));

        foreach(var credit in batch.PpsCredits)
            await InsertPpsCreditAsync(con, tx, credit, ct);

        return ShareAccountingInsertResult.Inserted;
    }

    private static async Task InsertPpsCreditAsync(IDbConnection con,
        IDbTransaction tx, PpsShareCredit credit, CancellationToken ct)
    {
        if(credit.AccountingId == Guid.Empty || string.IsNullOrWhiteSpace(credit.PoolId) ||
           string.IsNullOrWhiteSpace(credit.Address) || credit.CalculatedAmount <= 0 ||
           !double.IsFinite(credit.Difficulty) || credit.Difficulty <= 0 ||
           !double.IsFinite(credit.NetworkDifficulty) || credit.NetworkDifficulty <= 0 ||
           credit.RewardBasisSatoshis <= 0)
            throw new InvalidDataException("Malformed PPS share credit");

        const string seedRemainder = @"INSERT INTO pps_credit_remainders(
                poolid, address, amount, updated)
            VALUES(@PoolId, @Address, 0, @Created)
            ON CONFLICT(poolid, address) DO NOTHING";
        await con.ExecuteAsync(new CommandDefinition(seedRemainder, credit, tx,
            cancellationToken: ct));

        const string lockRemainder = @"SELECT amount
            FROM pps_credit_remainders
            WHERE poolid = @PoolId AND address = @Address
            FOR UPDATE";
        var remainder = await con.QuerySingleAsync<decimal>(new CommandDefinition(
            lockRemainder, credit, tx, cancellationToken: ct));
        var accumulated = checked(remainder + credit.CalculatedAmount);
        var credited = decimal.Truncate(accumulated * 1_000_000_000_000m) /
            1_000_000_000_000m;
        remainder = accumulated - credited;

        const string updateRemainder = @"UPDATE pps_credit_remainders
            SET amount = @remainder, updated = @Created
            WHERE poolid = @PoolId AND address = @Address";
        await con.ExecuteAsync(new CommandDefinition(updateRemainder, new
        {
            credit.PoolId,
            credit.Address,
            credit.Created,
            remainder,
        }, tx, cancellationToken: ct));

        const string insertCredit = @"INSERT INTO pps_share_credits(poolid,
                accountingid, address, calculatedamount, creditedamount,
                difficulty, networkdifficulty, rewardbasissatoshis, created)
            VALUES(@PoolId, @AccountingId, @Address, @CalculatedAmount, @credited,
                @Difficulty, @NetworkDifficulty, @RewardBasisSatoshis, @Created)";
        await con.ExecuteAsync(new CommandDefinition(insertCredit, new
        {
            credit.PoolId,
            credit.AccountingId,
            credit.Address,
            credit.CalculatedAmount,
            credited,
            credit.Difficulty,
            credit.NetworkDifficulty,
            credit.RewardBasisSatoshis,
            credit.Created,
        }, tx, cancellationToken: ct));

        if(credited <= 0)
            return;

        var tag = $"pps-share:{credit.AccountingId:N}";
        const string insertChange = @"INSERT INTO balance_changes(poolid, address,
                amount, usage, tags, created)
            VALUES(@PoolId, @Address, @credited, 'PPS share credit', @tags, @Created)";
        await con.ExecuteAsync(new CommandDefinition(insertChange, new
        {
            credit.PoolId,
            credit.Address,
            credited,
            tags = new[] { "pps", tag },
            credit.Created,
        }, tx, cancellationToken: ct));

        const string updateBalance = @"INSERT INTO balances(poolid, address, amount,
                created, updated)
            VALUES(@PoolId, @Address, @credited, @Created, @Created)
            ON CONFLICT(poolid, address) DO UPDATE
            SET amount = balances.amount + EXCLUDED.amount,
                updated = EXCLUDED.updated";
        await con.ExecuteAsync(new CommandDefinition(updateBalance, new
        {
            credit.PoolId,
            credit.Address,
            credited,
            credit.Created,
        }, tx, cancellationToken: ct));
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
            throw new InvalidDataException(
                $"Share accounting id {batch.AccountingId:N} conflicts with committed evidence");

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
