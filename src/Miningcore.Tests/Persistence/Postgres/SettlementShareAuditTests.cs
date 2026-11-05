using System;
using System.IO;
using System.Threading.Tasks;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class SettlementShareAuditTests
{
    [PostgresIntegrationFact]
    public async Task ArithmeticOwnershipPreflight_RejectsLegacyGrantOnlyOwnersBeforeMigration_AndAllowsReconciledWriter()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"miningcore_owner_{suffix}";
        var app = $"audit_writer_{suffix}";
        var tables = new[] { "share_accounting_groups", "share_accounting_prune_state", "pps_share_credits", "pps_credit_remainders" };
        try
        {
            await using(var setup = new NpgsqlCommand($"""
                CREATE ROLE {app} NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;
                CREATE SCHEMA {schema};
                GRANT USAGE ON SCHEMA {schema} TO {app};
                SET search_path TO {schema}, pg_catalog, pg_temp;
                """, connection))
                await setup.ExecuteNonQueryAsync();
            var createdb = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"));
            // Use the real accounting definitions before their generated arithmetic
            // append. No frozen production schema or shared application role is altered.
            const string marker = "-- BEGIN GENERATED PPS ARITHMETIC MIGRATION";
            Assert.Contains(marker, createdb);
            await using(var install = new NpgsqlCommand(createdb[..createdb.IndexOf(marker, StringComparison.Ordinal)]
                .Replace("SET ROLE miningcore;", ""), connection))
                await install.ExecuteNonQueryAsync();
            await using(var sharesOwner = new NpgsqlCommand($"ALTER TABLE shares OWNER TO {app}", connection))
                await sharesOwner.ExecuteNonQueryAsync();
            foreach(var table in tables)
            {
                await using var align = new NpgsqlCommand($"ALTER TABLE {table} OWNER TO {app}", connection);
                await align.ExecuteNonQueryAsync();
            }
            var preflight = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "check-pps-arithmetic-ownership.sql"));
            foreach(var table in tables)
            {
                // Reproduce the old database-owner + application-DML-grants shape.
                await using(var mismatch = new NpgsqlCommand($"ALTER TABLE {table} OWNER TO CURRENT_USER; GRANT ALL ON {table} TO {app}", connection))
                    await mismatch.ExecuteNonQueryAsync();
                await using(var check = new NpgsqlCommand(preflight, connection))
                {
                    var error = await Assert.ThrowsAsync<PostgresException>(() => check.ExecuteReaderAsync());
                    Assert.Equal("55000", error.SqlState);
                }
                await using(var rollback = new NpgsqlCommand("ROLLBACK", connection))
                    await rollback.ExecuteNonQueryAsync();
                await using(var untouched = new NpgsqlCommand($"""
                    SELECT to_regclass('pps_arithmetic_transitions') IS NULL
                      AND NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid='pps_share_credits'::regclass AND attname='arithmeticversion')
                      AND (SELECT relowner FROM pg_class WHERE oid='{table}'::regclass)
                          = (SELECT oid FROM pg_roles WHERE rolname=current_user)
                    """, connection))
                    Assert.True((bool) await untouched.ExecuteScalarAsync());
                await using var repair = new NpgsqlCommand($"ALTER TABLE {table} OWNER TO {app}", connection);
                await repair.ExecuteNonQueryAsync();
            }
            await using(var hide = new NpgsqlCommand("ALTER TABLE share_accounting_prune_state RENAME TO hidden_prune_state", connection))
                await hide.ExecuteNonQueryAsync();
            await using(var check = new NpgsqlCommand(preflight, connection))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => check.ExecuteReaderAsync());
                Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
            }
            await using(var restore = new NpgsqlCommand("ROLLBACK; ALTER TABLE hidden_prune_state RENAME TO share_accounting_prune_state", connection))
                await restore.ExecuteNonQueryAsync();
            await using(var check = new NpgsqlCommand(preflight, connection))
                await check.ExecuteNonQueryAsync();
            var migration = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_pps_arithmetic_version.sql"));
            await using(var migrate = new NpgsqlCommand(migration.Replace("\\set ON_ERROR_STOP on", ""), connection))
                await migrate.ExecuteNonQueryAsync();
            await using(var writer = new NpgsqlCommand($"""
                SET ROLE {app};
                INSERT INTO share_accounting_groups(accountingid, projectioncount, payloadhash, created)
                  VALUES('00000000-0000-0000-0000-000000000001', 1, repeat('A',64), now());
                INSERT INTO pps_share_credits(poolid, accountingid, address, calculatedamount, creditedamount,
                  difficulty, networkdifficulty, rewardbasissatoshis, created)
                  VALUES('owner-fixture', '00000000-0000-0000-0000-000000000001', 'fixture-miner', 2, 1, 10, 1, 1000, now());
                SELECT arithmeticversion=0 AND has_table_privilege(current_user, 'pps_arithmetic_transitions', 'SELECT')
                  AND NOT has_table_privilege(current_user, 'pps_arithmetic_transitions', 'INSERT,UPDATE,DELETE,TRUNCATE')
                FROM pps_share_credits WHERE poolid='owner-fixture';
                """, connection))
                Assert.True((bool) await writer.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"ROLLBACK; RESET ROLE; SET search_path TO pg_catalog; DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE IF EXISTS {app}", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresIntegrationFact]
    public async Task FullSettlementAudit_RunsOnFreshAccountingSchemaWithoutChangingReceipts()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        var schema = $"miningcore_audit_full_{Guid.NewGuid():N}";
        try
        {
            await using(var setup = new NpgsqlCommand($"CREATE SCHEMA {schema}; SET search_path TO {schema}, pg_catalog, pg_temp", connection))
                await setup.ExecuteNonQueryAsync();
            var createdb = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"));
            // Match the existing isolated-schema fixtures: use the supplied test
            // role rather than changing shared roles or the database-wide schema.
            await using(var install = new NpgsqlCommand(createdb.Replace("SET ROLE miningcore;", ""), connection))
                await install.ExecuteNonQueryAsync();
            await using(var seed = new NpgsqlCommand("""
                INSERT INTO share_accounting_groups(accountingid, projectioncount, payloadhash, created)
                    VALUES('00000000-0000-0000-0000-000000000001', 1, repeat('A',64), now());
                INSERT INTO pps_share_credits(poolid, accountingid, address, calculatedamount, creditedamount,
                    difficulty, networkdifficulty, rewardbasissatoshis, created)
                    VALUES('full-pps', '00000000-0000-0000-0000-000000000001', 'fixture-miner', 2, 1,
                           10, 1, 1000, now());
                """, connection))
                await seed.ExecuteNonQueryAsync();
            var before = await SnapshotAsync(connection);
            var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-settlement-shares.sql"));
            await using(var audit = new NpgsqlCommand(sql, connection))
            await using(var reader = await audit.ExecuteReaderAsync())
            {
                for(var result = 0; result < 5; result++)
                {
                    Assert.False(await reader.ReadAsync());
                    Assert.True(await reader.NextResultAsync());
                }
                Assert.True(await reader.ReadAsync());
                Assert.Equal("full-pps", reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(2m, reader.GetDecimal(2));
                Assert.Equal(1m, reader.GetDecimal(3));
                Assert.Equal(1.8m, reader.GetDecimal(4));
                Assert.False(await reader.ReadAsync());
                for(var result = 0; result < 4; result++)
                {
                    Assert.True(await reader.NextResultAsync());
                    Assert.False(await reader.ReadAsync());
                }
            }
            Assert.Equal(before, await SnapshotAsync(connection));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"ROLLBACK; SET search_path TO pg_catalog; DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresIntegrationFact]
    public async Task LegacySharesOnlyAudit_ScreensUnmigratedSchemaReadOnlyWithoutClearingMissingReceipts()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        await using(var setup = new NpgsqlCommand("""
            SET search_path TO pg_temp;
            CREATE TEMP TABLE shares(poolid text, miner text DEFAULT 'miner', created timestamptz DEFAULT now(),
                blockheight bigint DEFAULT 1, difficulty double precision, networkdifficulty double precision);
            INSERT INTO shares(poolid, difficulty, networkdifficulty) VALUES
                ('bad', 0, 1), ('bad', -1, 1), ('bad', 'NaN', 1), ('bad', 'Infinity', 1),
                ('bad', '-Infinity', 1), ('bad', 1, 0), ('bad', 1, 'NaN'), ('bad', 1, 'Infinity'),
                ('bad', 1, '-Infinity'), ('bad', NULL, 1), ('bad', 1, NULL),
                ('clean', 1, 2), ('oversized', 10, 1), ('extreme', 1e308, 1e-300), ('ergo-scale', 2, 300);
            INSERT INTO shares(poolid, miner, difficulty, networkdifficulty)
                SELECT 'limit', 'miner-' || i, 0, 1 FROM generate_series(1, 120) i;
            """, connection))
            await setup.ExecuteNonQueryAsync();

        await AssertLegacyAccountingSchemaAbsentAsync(connection);
        var before = await LegacySnapshotAsync(connection);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-legacy-settlement-shares.sql"));
        // Observe the real transaction before its COMMIT without changing any
        // audit query or granting the script write access to permanent data.
        sql = sql.Replace("COMMIT;", """
            SELECT current_setting('transaction_read_only'), current_setting('transaction_isolation');
            COMMIT;
            """);
        await using(var audit = new NpgsqlCommand(sql, connection))
        await using(var reader = await audit.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("audit_scope", reader.GetName(0));
            Assert.Equal("legacy-shares-only", reader.GetString(0));
            Assert.Contains("no PPS receipt", reader.GetString(1));
            Assert.Contains("Missing/pruned financial evidence is not cleared", reader.GetString(1));
            Assert.False(await reader.ReadAsync());

            Assert.True(await reader.NextResultAsync());
            foreach(var expected in new (string Pool, long Count)[] { ("bad", 11), ("limit", 120) })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(expected.Pool, reader.GetString(0));
                Assert.Equal(expected.Count, reader.GetInt64(1));
            }
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            var sampled = 0;
            while(await reader.ReadAsync())
            {
                Assert.Contains(reader.GetString(0), new[] { "bad", "limit" });
                sampled++;
            }
            Assert.Equal(100, sampled);
            Assert.True(await reader.NextResultAsync());
            foreach(var pool in new[] { "extreme", "oversized" })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("raw_oversized_rows", reader.GetName(1));
                Assert.Equal(pool, reader.GetString(0));
                Assert.Equal(1L, reader.GetInt64(1));
            }
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("on", reader.GetString(0));
            Assert.Equal("repeatable read", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
            Assert.False(await reader.NextResultAsync());
        }
        Assert.Equal(before, await LegacySnapshotAsync(connection));
        await AssertLegacyAccountingSchemaAbsentAsync(connection);

        // Scope choice must never turn a failing full financial audit into a
        // clean result. No empty receipt table or migration is installed here.
        var fullSql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-settlement-shares.sql"));
        await using(var full = new NpgsqlCommand(fullSql, connection))
        {
            // Failure must occur before a reader can expose even an empty share
            // report, not while advancing to a later financial result set.
            var missing = await Assert.ThrowsAsync<PostgresException>(() => full.ExecuteReaderAsync());
            Assert.Equal(PostgresErrorCodes.UndefinedTable, missing.SqlState);
        }
        await using(var rollback = new NpgsqlCommand("ROLLBACK", connection))
            await rollback.ExecuteNonQueryAsync();
        Assert.Equal(before, await LegacySnapshotAsync(connection));
        await AssertLegacyAccountingSchemaAbsentAsync(connection);
    }

    [PostgresIntegrationFact]
    public async Task FullSettlementAudit_StillRejectsMissingShareAccountingColumns()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        await using(var setup = new NpgsqlCommand("""
            SET search_path TO pg_temp;
            CREATE TEMP TABLE shares(poolid text, miner text, created timestamptz,
                blockheight bigint, difficulty double precision, networkdifficulty double precision);
            CREATE TEMP TABLE pps_share_credits(poolid text, accountingid uuid, address text,
                created timestamptz, difficulty double precision, networkdifficulty double precision,
                calculatedamount numeric, creditedamount numeric);
            """, connection))
            await setup.ExecuteNonQueryAsync();
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-settlement-shares.sql"));
        await using var audit = new NpgsqlCommand(sql, connection);
        var missing = await Assert.ThrowsAsync<PostgresException>(() => audit.ExecuteReaderAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, missing.SqlState);
    }

    private static async Task AssertLegacyAccountingSchemaAbsentAsync(NpgsqlConnection connection)
    {
        await using var schema = new NpgsqlCommand("""
            SELECT to_regclass('pps_share_credits') IS NULL AND NOT EXISTS (
                SELECT 1 FROM pg_attribute WHERE attrelid = 'shares'::regclass
                  AND attname IN ('accountingid', 'accountingrole'))
            """, connection);
        Assert.True((bool) await schema.ExecuteScalarAsync());
    }

    private static async Task<string> LegacySnapshotAsync(NpgsqlConnection connection)
    {
        await using var snapshot = new NpgsqlCommand("""
            SELECT jsonb_agg(to_jsonb(s) ORDER BY poolid, miner, created, blockheight, difficulty, networkdifficulty)::text
            FROM shares s
            """, connection);
        return (string) await snapshot.ExecuteScalarAsync();
    }

    [PostgresIntegrationFact]
    public async Task SettlementAudit_DetectsSpecialAndInvalidLegacyRowsWithoutChangingData()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        // Session-local shadow table: this fixture cannot inspect production or
        // another fixture's shares, even if their schema is in the search path.
        await using(var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE shares(poolid text, miner text DEFAULT 'miner', created timestamptz DEFAULT now(),
                blockheight bigint DEFAULT 1, difficulty double precision, networkdifficulty double precision,
                accountingid uuid, accountingrole smallint);
            INSERT INTO shares(poolid, difficulty, networkdifficulty) VALUES
                ('bad', 0, 1), ('bad', -1, 1), ('bad', 'NaN', 1), ('bad', 'Infinity', 1),
                ('bad', '-Infinity', 1), ('bad', 1, 0), ('bad', 1, 'NaN'), ('bad', 1, 'Infinity'),
                ('bad', 1, '-Infinity'), ('bad', NULL, 1), ('bad', 1, NULL),
                ('clean', 1, 2), ('large', 10, 1), ('ergo-scale', 2, 300);
            CREATE TEMP TABLE pps_share_credits(poolid text, accountingid uuid DEFAULT gen_random_uuid(),
                address text DEFAULT 'pps-miner', created timestamptz DEFAULT now(),
                difficulty double precision, networkdifficulty double precision,
                calculatedamount numeric, creditedamount numeric);
            INSERT INTO pps_share_credits(poolid, difficulty, networkdifficulty, calculatedamount, creditedamount) VALUES
                ('pps-bad', 0, 1, 1, 1), ('pps-bad', -1, 1, 1, 1),
                ('pps-bad', 'NaN', 1, 1, 1), ('pps-bad', 'Infinity', 1, 1, 1),
                ('pps-bad', 1, 'NaN', 1, 1), ('pps-bad', 1, 0, 1, 1),
                ('pps-bad', 1, 1, 'NaN', 1), ('pps-bad', 1, 1, 0, 1),
                ('pps-bad', 1, 1, 1, -1), ('pps-bad', 1, 1, 1, 'NaN'),
                ('pps-bad', NULL, 1, 1, 1), ('pps-bad', 1, NULL, 1, 1),
                ('pps-large', 10, 1, 100, 99), ('pps-large', 5, 1, 150, 149),
                ('pps-clean', 1, 2, 5, 5), ('pps-clean', 1, 2, 0.000000000000000000000001, 0);
            """, connection))
            await setup.ExecuteNonQueryAsync();

        var before = await SnapshotAsync(connection);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "audit-settlement-shares.sql"));
        await using(var audit = new NpgsqlCommand(sql, connection))
        await using(var reader = await audit.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("bad", reader.GetString(0));
            Assert.Equal(11L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            var identified = 0;
            while(await reader.ReadAsync())
            {
                Assert.Equal("bad", reader.GetString(0));
                identified++;
            }
            Assert.Equal(11, identified);
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("large", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("pps-bad", reader.GetString(0));
            Assert.Equal(12L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            identified = 0;
            while(await reader.ReadAsync())
            {
                Assert.Equal("pps-bad", reader.GetString(0));
                identified++;
            }
            Assert.Equal(12, identified);
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            // These receipts have no corresponding rows in the shares table.
            Assert.Equal("pps-large", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(250m, reader.GetDecimal(2));
            Assert.Equal(248m, reader.GetDecimal(3));
            Assert.Equal("oversized_calculated_amount", reader.GetName(2));
            Assert.Equal("oversized_credited_amount", reader.GetName(3));
            Assert.Equal(210m, reader.GetDecimal(4));
            Assert.False(await reader.ReadAsync());
            // Unrelated IDs must not turn ordinary receipts into paired suspects.
            for(var i = 0; i < 4; i++)
            {
                Assert.True(await reader.NextResultAsync());
                Assert.False(await reader.ReadAsync());
            }
        }
        Assert.Equal(before, await SnapshotAsync(connection));
        await AssertMergedPairAuditAsync(connection, sql);
        // The raw report cannot diagnose a nonidentity handler. Apply Ergo's
        // documented factor using numeric arithmetic, without a risky quotient.
        await using var normalized = new NpgsqlCommand("""
            SELECT count(*) FROM shares
            WHERE poolid = 'ergo-scale' AND difficulty::numeric * 256 > networkdifficulty::numeric
            """, connection);
        Assert.Equal(1L, await normalized.ExecuteScalarAsync());
        await using(var missingSchema = new NpgsqlCommand("SET search_path TO pg_temp; DROP TABLE pg_temp.pps_share_credits", connection))
            await missingSchema.ExecuteNonQueryAsync();
        await using var incompleteAudit = new NpgsqlCommand(sql, connection);
        var missing = await Assert.ThrowsAsync<PostgresException>(() => incompleteAudit.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedTable, missing.SqlState);
    }

    private static async Task AssertMergedPairAuditAsync(NpgsqlConnection connection, string sql)
    {
        await using(var setup = new NpgsqlCommand("""
            INSERT INTO shares(poolid, difficulty, networkdifficulty, accountingid, accountingrole) VALUES
                ('s-parent', 10, 100, '00000000-0000-0000-0000-000000000001', 2),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000001', 3),
                ('s-aux-extra', 20, 2, '00000000-0000-0000-0000-000000000001', 3),
                ('s-parent', 20, 100, '00000000-0000-0000-0000-000000000002', 2),
                ('s-aux', 20, 2, '00000000-0000-0000-0000-000000000002', 3),
                ('s-same-pool', 10, 100, '00000000-0000-0000-0000-000000000003', 2),
                ('s-same-pool', 10, 1, '00000000-0000-0000-0000-000000000003', 3),
                ('s-wrong-parent-role', 10, 100, '00000000-0000-0000-0000-000000000004', 1),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000004', 3),
                ('s-wrong-aux-role', 10, 100, '00000000-0000-0000-0000-000000000005', 2),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000005', 1),
                ('s-null-id', 10, 100, NULL, 2), ('s-aux', 10, 1, NULL, 3),
                ('s-unrelated', 10, 100, '00000000-0000-0000-0000-000000000006', 2),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000007', 3),
                ('s-invalid-peer', 10, 100, '00000000-0000-0000-0000-000000000008', 2),
                ('s-aux', 'NaN', 1, '00000000-0000-0000-0000-000000000008', 3),
                ('s-invalid-parent', 'NaN', 100, '00000000-0000-0000-0000-000000000009', 2),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000009', 3),
                ('s-clean', 10, 100, '00000000-0000-0000-0000-000000000010', 2),
                ('s-aux', 10, 20, '00000000-0000-0000-0000-000000000010', 3),
                ('s-already-oversized', 100, 10, '00000000-0000-0000-0000-000000000011', 2),
                ('s-aux', 100, 1, '00000000-0000-0000-0000-000000000011', 3),
                ('s-reverse-parent', 10, 1, '00000000-0000-0000-0000-000000000014', 2),
                ('s-reverse-parent-extra', 20, 2, '00000000-0000-0000-0000-000000000014', 2),
                ('s-reverse-aux', 10, 100, '00000000-0000-0000-0000-000000000014', 3),
                ('s-reverse-same-pool', 10, 1, '00000000-0000-0000-0000-000000000015', 2),
                ('s-reverse-same-pool', 10, 100, '00000000-0000-0000-0000-000000000015', 3),
                ('s-reverse-wrong-role', 10, 100, '00000000-0000-0000-0000-000000000016', 3),
                ('s-reverse-parent', 10, 1, '00000000-0000-0000-0000-000000000016', 1),
                ('s-reverse-invalid-peer', 10, 100, '00000000-0000-0000-0000-000000000017', 3),
                ('s-reverse-parent', 10, 'NaN', '00000000-0000-0000-0000-000000000017', 2),
                ('s-reverse-invalid-aux', 'NaN', 100, '00000000-0000-0000-0000-000000000018', 3),
                ('s-reverse-parent', 10, 1, '00000000-0000-0000-0000-000000000018', 2),
                ('s-aux-same-role', 10, 100, '00000000-0000-0000-0000-000000000019', 3),
                ('s-aux', 10, 1, '00000000-0000-0000-0000-000000000019', 3),
                ('s-parent-same-role', 10, 100, '00000000-0000-0000-0000-000000000020', 2),
                ('s-parent', 10, 1, '00000000-0000-0000-0000-000000000020', 2),
                ('s-reverse-null-id', 10, 100, NULL, 3), ('s-reverse-parent', 10, 1, NULL, 2);
            INSERT INTO pps_share_credits(poolid, difficulty, networkdifficulty, calculatedamount, creditedamount, accountingid) VALUES
                ('pps-parent', 10, 100, 1000, 999, '00000000-0000-0000-0000-000000000001'),
                ('pps-aux', 10, 1, 100, 99, '00000000-0000-0000-0000-000000000001'),
                ('pps-aux-extra', 20, 2, 200, 199, '00000000-0000-0000-0000-000000000001'),
                ('pps-parent', 20, 100, 2000, 1999, '00000000-0000-0000-0000-000000000002'),
                ('pps-aux', 20, 2, 200, 199, '00000000-0000-0000-0000-000000000002'),
                ('pps-same-pool', 10, 100, 1, 1, '00000000-0000-0000-0000-000000000003'),
                ('pps-same-pool', 10, 1, 1, 1, '00000000-0000-0000-0000-000000000003'),
                ('pps-null-id', 10, 100, 1, 1, NULL), ('pps-aux', 10, 1, 1, 1, NULL),
                ('pps-unrelated', 10, 100, 1, 1, '00000000-0000-0000-0000-000000000006'),
                ('pps-aux', 10, 1, 1, 1, '00000000-0000-0000-0000-000000000007'),
                ('pps-invalid-peer', 10, 100, 1, 1, '00000000-0000-0000-0000-000000000008'),
                ('pps-aux', 'NaN', 1, 1, 1, '00000000-0000-0000-0000-000000000008'),
                ('pps-invalid-parent', 10, 100, 'NaN', 1, '00000000-0000-0000-0000-000000000009'),
                ('pps-aux', 10, 1, 1, 1, '00000000-0000-0000-0000-000000000009'),
                ('pps-clean-pair', 10, 100, 1, 1, '00000000-0000-0000-0000-000000000010'),
                ('pps-aux', 10, 20, 1, 1, '00000000-0000-0000-0000-000000000010'),
                ('pps-already-oversized', 100, 10, 1, 1, '00000000-0000-0000-0000-000000000011'),
                ('pps-aux', 100, 1, 1, 1, '00000000-0000-0000-0000-000000000011'),
                ('pps-invalid-money-peer', 10, 100, 1, 1, '00000000-0000-0000-0000-000000000012'),
                ('pps-aux', 10, 1, 'NaN', 1, '00000000-0000-0000-0000-000000000012'),
                ('pps-extreme', 1e308, 1e-300, 100, 99, '00000000-0000-0000-0000-000000000013'),
                ('pps-reverse-parent', 10, 1, 100, 99, '00000000-0000-0000-0000-000000000014'),
                ('pps-reverse-parent-extra', 20, 2, 200, 199, '00000000-0000-0000-0000-000000000014'),
                ('pps-reverse-aux', 10, 100, 3000, 2999, '00000000-0000-0000-0000-000000000014');
            """, connection))
            await setup.ExecuteNonQueryAsync();

        var before = await SnapshotAsync(connection);
        await using(var audit = new NpgsqlCommand(sql, connection))
        await using(var reader = await audit.ExecuteReaderAsync())
        {
            // Inspect the directly oversized PPS estimate before the paired reports.
            for(var i = 0; i < 5; i++)
                Assert.True(await reader.NextResultAsync());
            var extremeFound = false;
            while(await reader.ReadAsync())
            {
                Assert.NotEqual("pps-parent", reader.GetString(0));
                if(reader.GetString(0) == "pps-extreme")
                {
                    Assert.Equal(100m, reader.GetDecimal(4));
                    extremeFound = true;
                }
            }
            Assert.True(extremeFound);
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            Assert.Equal("s-parent", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal("paired_nonoversized_rows", reader.GetName(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("s-reverse-aux", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            foreach(var expected in new (string Pool, int Id, short Role)[]
                { ("s-parent", 1, 2), ("s-parent", 2, 2), ("s-reverse-aux", 14, 3) })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(expected.Pool, reader.GetString(0));
                Assert.Equal(Guid.Parse($"00000000-0000-0000-0000-{expected.Id:D12}"), reader.GetGuid(1));
                Assert.Equal(expected.Role, reader.GetInt16(2));
            }
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            // PPS pairs are found without any corresponding pps-parent shares.
            // Multiple matching peers must not multiply the parent's liability.
            Assert.Equal("pps-parent", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(3000m, reader.GetDecimal(2));
            Assert.Equal(2998m, reader.GetDecimal(3));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("pps-reverse-aux", reader.GetString(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(3000m, reader.GetDecimal(2));
            Assert.Equal(2999m, reader.GetDecimal(3));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            foreach(var expected in new (string Pool, int Id)[]
                { ("pps-parent", 1), ("pps-parent", 2), ("pps-reverse-aux", 14) })
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(expected.Pool, reader.GetString(0));
                Assert.Equal(Guid.Parse($"00000000-0000-0000-0000-{expected.Id:D12}"), reader.GetGuid(1));
            }
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(before, await SnapshotAsync(connection));
    }

    private static async Task<string> SnapshotAsync(NpgsqlConnection connection)
    {
        await using var snapshot = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'shares', (SELECT jsonb_agg(to_jsonb(s) ORDER BY poolid, accountingid, accountingrole, difficulty, networkdifficulty) FROM shares s),
                'credits', (SELECT jsonb_agg(to_jsonb(c) ORDER BY poolid, accountingid, difficulty, networkdifficulty) FROM pps_share_credits c))::text
            """, connection);
        return (string) await snapshot.ExecuteScalarAsync();
    }
}
