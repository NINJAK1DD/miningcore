using System;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Payments;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

[CollectionDefinition(Name)]
public sealed class PayoutManagerLeaseIntegrationCollection
{
    public const string Name = "Payout manager lease integration";
}

internal static class PayoutManagerLeaseIntegrationAssertions
{
    public static async Task AssertAdvisoryLockAvailableAsync(string connectionString)
    {
        var verificationConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await using var connection = new NpgsqlConnection(verificationConnectionString);
        await connection.OpenAsync();
        var acquired = false;

        try
        {
            acquired = await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(@lockNamespace, @lockKey)", new
                {
                    lockNamespace = PostgresPayoutManagerLease.LockNamespace,
                    lockKey = PostgresPayoutManagerLease.LockKey,
                });
            Assert.True(acquired);
        }

        finally
        {
            if(acquired)
            {
                Assert.True(await connection.ExecuteScalarAsync<bool>(
                    "SELECT pg_advisory_unlock(@lockNamespace, @lockKey)", new
                    {
                        lockNamespace = PostgresPayoutManagerLease.LockNamespace,
                        lockKey = PostgresPayoutManagerLease.LockKey,
                    }));
            }
        }
    }
}
