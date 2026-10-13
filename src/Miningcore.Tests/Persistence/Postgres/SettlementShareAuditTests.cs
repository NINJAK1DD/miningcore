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
                ('s-aux', 100, 1, '00000000-0000-0000-0000-000000000011', 3);
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
                ('pps-extreme', 1e308, 1e-300, 100, 99, '00000000-0000-0000-0000-000000000013');
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
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            var identified = 0;
            while(await reader.ReadAsync())
            {
                Assert.Equal("s-parent", reader.GetString(0));
                Assert.Equal(Guid.Parse($"00000000-0000-0000-0000-{++identified:D12}"), reader.GetGuid(1));
            }
            Assert.Equal(2, identified);
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            // PPS pairs are found without any corresponding pps-parent shares.
            // Multiple matching peers must not multiply the parent's liability.
            Assert.Equal("pps-parent", reader.GetString(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(3000m, reader.GetDecimal(2));
            Assert.Equal(2998m, reader.GetDecimal(3));
            Assert.False(await reader.ReadAsync());
            Assert.True(await reader.NextResultAsync());
            identified = 0;
            while(await reader.ReadAsync())
            {
                Assert.Equal("pps-parent", reader.GetString(0));
                Assert.Equal(Guid.Parse($"00000000-0000-0000-0000-{++identified:D12}"), reader.GetGuid(1));
            }
            Assert.Equal(2, identified);
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
