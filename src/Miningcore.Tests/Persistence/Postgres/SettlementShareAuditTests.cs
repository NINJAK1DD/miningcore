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
                ('clean', 1, 2), ('large', 10, 1);
            """, connection))
            await setup.ExecuteNonQueryAsync();

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
        }
        await using var unchanged = new NpgsqlCommand("SELECT count(*) FROM shares", connection);
        Assert.Equal(13L, await unchanged.ExecuteScalarAsync());
    }
}
