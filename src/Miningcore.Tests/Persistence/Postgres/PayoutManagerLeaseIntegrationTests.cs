using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class PayoutManagerLeaseIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task AcquisitionFailure_DistinguishesDurableMarkerFromActiveLock()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MININGCORE_TEST_POSTGRES");
        var schema = $"miningcore_payout_lease_{Guid.NewGuid():N}";
        var ownerId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await connection.ExecuteAsync($@"
                CREATE SCHEMA {schema};
                SET search_path TO {schema}, public;
                CREATE TABLE payment_batches(
                    poolid text NOT NULL,
                    transactionconfirmationdata text NOT NULL,
                    created timestamptz NOT NULL,
                    PRIMARY KEY(poolid, transactionconfirmationdata));
                CREATE TABLE payout_manager_ownership(
                    id smallint NOT NULL PRIMARY KEY CHECK(id = 1),
                    generation bigint NOT NULL DEFAULT 0,
                    owner_id uuid NULL,
                    owner_host text NULL,
                    owner_process_id int NULL,
                    acquired timestamptz NULL,
                    released timestamptz NULL);
                INSERT INTO payout_manager_ownership(
                    id, generation, owner_id, owner_host, owner_process_id, acquired)
                VALUES(1, 6, @ownerId, 'pool-host', 4242, @acquired);",
                new
                {
                    ownerId,
                    acquired = new DateTime(2026, 7, 17, 9, 55, 49,
                        DateTimeKind.Utc),
                });

            var leaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = $"{schema},public",
            }.ConnectionString;

            await using(var staleMarkerLease = new PostgresPayoutManagerLease(
                new PgConnectionFactory(leaseConnectionString)))
            {
                Assert.False(await staleMarkerLease.TryAcquireAsync(
                    CancellationToken.None));
                Assert.Contains("without the advisory lock",
                    staleMarkerLease.AcquisitionFailure);
                Assert.Contains("generation 6", staleMarkerLease.AcquisitionFailure);
                Assert.Contains("host pool-host", staleMarkerLease.AcquisitionFailure);
                Assert.Contains("process 4242", staleMarkerLease.AcquisitionFailure);
                Assert.Contains(ownerId.ToString(), staleMarkerLease.AcquisitionFailure);
            }

            Assert.True(await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_lock(@lockNamespace, @lockKey)", new
                {
                    lockNamespace = PostgresPayoutManagerLease.LockNamespace,
                    lockKey = PostgresPayoutManagerLease.LockKey,
                }));

            try
            {
                await using var activeLease = new PostgresPayoutManagerLease(
                    new PgConnectionFactory(leaseConnectionString));
                Assert.False(await activeLease.TryAcquireAsync(CancellationToken.None));
                Assert.Contains("currently holds the PostgreSQL advisory lock",
                    activeLease.AcquisitionFailure);
                Assert.Contains("host pool-host", activeLease.AcquisitionFailure);
            }
            finally
            {
                Assert.True(await connection.ExecuteScalarAsync<bool>(
                    "SELECT pg_advisory_unlock(@lockNamespace, @lockKey)", new
                    {
                        lockNamespace = PostgresPayoutManagerLease.LockNamespace,
                        lockKey = PostgresPayoutManagerLease.LockKey,
                    }));
            }
        }
        finally
        {
            await connection.ExecuteAsync("SET search_path TO public");
            await connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }
}
