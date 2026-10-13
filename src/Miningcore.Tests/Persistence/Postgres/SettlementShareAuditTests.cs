using System;
using System.IO;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class SettlementShareAuditTests
{
    [PostgresIntegrationFact]
    public async Task SettlementAudit_DetectsSpecialAndInvalidLegacyRowsWithoutChangingData()
    {
        await using var connection = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await connection.OpenAsync();
        // Session-local shadow table: this fixture cannot inspect production or
        // another fixture's shares, even if their schema is in the search path.
        await using(var setup = new NpgsqlCommand("""
            CREATE TEMP TABLE shares(poolid text, miner text DEFAULT 'miner', created timestamptz DEFAULT now(),
                blockheight bigint DEFAULT 1, difficulty double precision, networkdifficulty double precision);
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
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(before, await SnapshotAsync(connection));
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

    private static async Task<string> SnapshotAsync(NpgsqlConnection connection)
    {
        await using var snapshot = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'shares', (SELECT jsonb_agg(to_jsonb(s) ORDER BY poolid, difficulty, networkdifficulty) FROM shares s),
                'credits', (SELECT jsonb_agg(to_jsonb(c) ORDER BY accountingid) FROM pps_share_credits c))::text
            """, connection);
        return (string) await snapshot.ExecuteScalarAsync();
    }
}
