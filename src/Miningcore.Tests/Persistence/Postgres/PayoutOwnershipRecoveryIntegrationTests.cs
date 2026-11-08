using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

[Collection(PayoutManagerLeaseIntegrationCollection.Name)]
public class PayoutOwnershipRecoveryIntegrationTests
{
    [PostgresIntegrationFact]
    public async Task GuardedRelease_AllowsNextGenerationAndStillExcludesSecondManager()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        Assert.Equal(db.Schema, snapshot.Schema);
        Assert.Equal(db.Owner, snapshot.OwnerId);
        Assert.False(snapshot.LockHeld);
        Assert.Single(snapshot.Batches);
        await db.Recovery.ReleaseAsync(snapshot, CancellationToken.None);
        Assert.Null(await db.OwnerAsync());
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
        Assert.NotNull(await db.Connection.ExecuteScalarAsync<DateTime?>(
            "SELECT released FROM payout_manager_ownership"));

        await using var lease = db.NewLease();
        await using var other = db.NewLease();
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        Assert.Equal(8, await db.Connection.ExecuteScalarAsync<long>("SELECT generation FROM payout_manager_ownership"));
        Assert.False(await other.TryAcquireAsync(CancellationToken.None));
    }

    [PostgresIntegrationFact]
    public async Task ActiveLock_AndLockAcquiredDuringPrompt_BothRefuseRelease()
    {
        await using var db = await Database.CreateAsync();
        var beforeLock = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync("SELECT pg_advisory_lock(19779, 5259609)");
        try
        {
            var locked = await db.Recovery.InspectAsync(CancellationToken.None);
            Assert.True(locked.LockHeld);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(locked, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(beforeLock, CancellationToken.None));
            Assert.Equal(db.Owner, await db.OwnerAsync());
        }
        finally
        {
            await db.Connection.ExecuteAsync("SELECT pg_advisory_unlock(19779, 5259609)");
        }
    }

    [PostgresIntegrationFact]
    public async Task ChangedGenerationOrToken_AndRepeatedRelease_AreRefused()
    {
        await using var db = await Database.CreateAsync();
        var old = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync("UPDATE payout_manager_ownership SET generation = generation + 1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(old, CancellationToken.None));
        var next = await db.Recovery.InspectAsync(CancellationToken.None);
        var replacement = Guid.NewGuid();
        await db.Connection.ExecuteAsync("UPDATE payout_manager_ownership SET owner_id = @replacement", new { replacement });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(next, CancellationToken.None));
        Assert.Equal(replacement, await db.OwnerAsync());
        var current = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Recovery.ReleaseAsync(current, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(current, CancellationToken.None));
    }

    [PostgresIntegrationFact]
    public async Task ReplacedTable_WithIdenticalOwner_IsRefused()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync(@"ALTER TABLE payout_manager_ownership RENAME TO old_ownership;
            CREATE TABLE payout_manager_ownership (LIKE old_ownership INCLUDING ALL);
            INSERT INTO payout_manager_ownership SELECT * FROM old_ownership");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Recovery.ReleaseAsync(snapshot, CancellationToken.None));
        Assert.Equal(db.Owner, await db.OwnerAsync());
    }

    [PostgresIntegrationFact]
    public async Task DifferentResolvedSchema_CannotReleaseInspectedOwner()
    {
        await using var first = await Database.CreateAsync();
        await using var second = await Database.CreateAsync();
        var snapshot = await first.Recovery.InspectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            second.Recovery.ReleaseAsync(snapshot, CancellationToken.None));
        Assert.Equal(first.Owner, await first.OwnerAsync());
        Assert.Equal(second.Owner, await second.OwnerAsync());
    }

    [PostgresIntegrationFact]
    public async Task CancelledRelease_LeavesOwnerAndLockAvailable()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Recovery.ReleaseAsync(snapshot, new CancellationToken(true)));
        Assert.Equal(db.Owner, await db.OwnerAsync());
        Assert.False((await db.Recovery.InspectAsync(CancellationToken.None)).LockHeld);
    }

    [PostgresIntegrationFact]
    public async Task ConcurrentRecovery_OnlyOneReleaseSucceeds()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        async Task<bool> ReleaseAsync()
        {
            try
            {
                await db.Recovery.ReleaseAsync(snapshot, CancellationToken.None);
                return true;
            }
            catch(InvalidOperationException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(ReleaseAsync(), ReleaseAsync());
        Assert.Single(results, x => x);
    }

    [PostgresIntegrationFact]
    public async Task LostGuardSession_RemainsBlockedUntilExplicitRecovery()
    {
        await using var db = await Database.CreateAsync();
        await db.Recovery.ReleaseAsync(await db.Recovery.InspectAsync(CancellationToken.None), CancellationToken.None);
        await using(var lease = db.NewLease())
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            lease.BeginFinancialOperation();
            lease.MarkFinancialOutcomeUncertain();
            await db.Connection.ExecuteAsync(@"SELECT pg_terminate_backend(pid) FROM pg_locks
                WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND classid = 19779 AND objid = 5259609 AND objsubid = 2 AND granted");
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EnsureHeldAsync(CancellationToken.None));
        }
        await using var replacement = db.NewLease();
        Assert.False(await replacement.TryAcquireAsync(CancellationToken.None));
        var retained = await db.Recovery.InspectAsync(CancellationToken.None);
        Assert.NotNull(retained.OwnerId);
        // The store is called only after the assistant's explicit operator reconciliation confirmations.
        await db.Recovery.ReleaseAsync(retained, CancellationToken.None);
        Assert.True(await replacement.TryAcquireAsync(CancellationToken.None));
    }

    private sealed class Database : IAsyncDisposable
    {
        public string Schema { get; } = $"Recovery_{Guid.NewGuid():N}"; // Exercise quoted mixed-case schemas.
        public Guid Owner { get; } = Guid.NewGuid();
        public NpgsqlConnection Connection { get; private set; }
        public PgConnectionFactory Factory { get; private set; }
        public PostgresPayoutOwnershipRecovery Recovery { get; private set; }

        public static async Task<Database> CreateAsync()
        {
            var db = new Database();
            var connectionString = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES");
            db.Connection = new NpgsqlConnection(connectionString);
            await db.Connection.OpenAsync();
            try
            {
                await db.Connection.ExecuteAsync($@"
                    CREATE SCHEMA ""{db.Schema}"";
                    SET search_path TO ""{db.Schema}"", public;
                    CREATE TABLE payment_batches(poolid text NOT NULL, transactionconfirmationdata text NOT NULL,
                        created timestamptz NOT NULL, PRIMARY KEY(poolid, transactionconfirmationdata));
                    CREATE TABLE payout_manager_ownership(id smallint PRIMARY KEY CHECK(id = 1),
                        generation bigint NOT NULL DEFAULT 0, owner_id uuid, owner_host text,
                        owner_process_id int, acquired timestamptz, released timestamptz);
                    INSERT INTO payout_manager_ownership VALUES(1, 7, @owner, 'test-host', 123, now() - interval '1 hour', NULL);
                    INSERT INTO payment_batches VALUES('test-pool', 'test-transaction', now());", new { owner = db.Owner });
                db.Factory = new PgConnectionFactory(new NpgsqlConnectionStringBuilder(connectionString)
                {
                    SearchPath = $"\"{db.Schema}\",public",
                }.ConnectionString);
                db.Recovery = new PostgresPayoutOwnershipRecovery(db.Factory);
                return db;
            }
            catch
            {
                await db.DisposeAsync();
                throw;
            }
        }

        public PostgresPayoutManagerLease NewLease() => new(Factory);
        public Task<Guid?> OwnerAsync() => Connection.ExecuteScalarAsync<Guid?>("SELECT owner_id FROM payout_manager_ownership");

        public async ValueTask DisposeAsync()
        {
            await Connection.ExecuteAsync($"SET search_path TO public; DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await Connection.DisposeAsync();
        }
    }
}
