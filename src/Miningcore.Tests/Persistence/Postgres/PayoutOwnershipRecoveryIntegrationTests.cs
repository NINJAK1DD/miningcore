using System;
using System.IO;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using System.Data;
using Miningcore.Persistence;
using Miningcore.Tests.Util.Postgres;
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
        await db.Recovery.ReleaseAsync(snapshot, "incident-267", CancellationToken.None);
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
        await db.Connection.ExecuteAsync("SELECT pg_advisory_lock(@LockNamespace, @LockKey)", LockParameters);
        try
        {
            var locked = await db.Recovery.InspectAsync(CancellationToken.None);
            Assert.True(locked.LockHeld);
            await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(locked, "incident-267", CancellationToken.None));
            await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(beforeLock, "incident-267", CancellationToken.None));
            Assert.Equal(db.Owner, await db.OwnerAsync());
        }
        finally
        {
            await db.Connection.ExecuteAsync("SELECT pg_advisory_unlock(@LockNamespace, @LockKey)", LockParameters);
        }
    }

    [PostgresIntegrationFact]
    public async Task ChangedGenerationOrToken_AndRepeatedRelease_AreRefused()
    {
        await using var db = await Database.CreateAsync();
        var old = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync("UPDATE payout_manager_ownership SET generation = generation + 1");
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(old, "incident-267", CancellationToken.None));
        var next = await db.Recovery.InspectAsync(CancellationToken.None);
        var replacement = Guid.NewGuid();
        await db.Connection.ExecuteAsync("UPDATE payout_manager_ownership SET owner_id = @replacement", new { replacement });
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(next, "incident-267", CancellationToken.None));
        Assert.Equal(replacement, await db.OwnerAsync());
        var current = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Recovery.ReleaseAsync(current, "incident-267", CancellationToken.None);
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(current, "incident-267", CancellationToken.None));
    }

    [PostgresIntegrationFact]
    public async Task ReplacedTable_WithIdenticalOwner_IsRefused()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync(@"ALTER TABLE payout_manager_ownership RENAME TO old_ownership;
            CREATE TABLE payout_manager_ownership (LIKE old_ownership INCLUDING ALL);
            INSERT INTO payout_manager_ownership SELECT * FROM old_ownership");
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(snapshot, "incident-267", CancellationToken.None));
        Assert.Equal(db.Owner, await db.OwnerAsync());
    }

    [PostgresIntegrationFact]
    public async Task DifferentResolvedSchema_CannotReleaseInspectedOwner()
    {
        await using var first = await Database.CreateAsync();
        await using var second = await Database.CreateAsync();
        var snapshot = await first.Recovery.InspectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<PayoutRecoveryException>(() =>
            second.Recovery.ReleaseAsync(snapshot, "incident-267", CancellationToken.None));
        Assert.Equal(first.Owner, await first.OwnerAsync());
        Assert.Equal(second.Owner, await second.OwnerAsync());
    }

    [PostgresIntegrationFact]
    public async Task CancelledRelease_LeavesOwnerAndLockAvailable()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            db.Recovery.ReleaseAsync(snapshot, "incident-267", new CancellationToken(true)));
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
                await db.Recovery.ReleaseAsync(snapshot, "incident-267", CancellationToken.None);
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
        await db.Recovery.ReleaseAsync(await db.Recovery.InspectAsync(CancellationToken.None), "incident-267", CancellationToken.None);
        await using(var lease = db.NewLease())
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            lease.BeginFinancialOperation();
            lease.MarkFinancialOutcomeUncertain();
            await db.Connection.ExecuteAsync(@"SELECT pg_terminate_backend(pid) FROM pg_locks
                WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND classid = @LockNamespace::oid AND objid = @LockKey::oid AND objsubid = 2 AND granted", LockParameters);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.EnsureHeldAsync(CancellationToken.None));
        }
        await using var replacement = db.NewLease();
        Assert.False(await replacement.TryAcquireAsync(CancellationToken.None));
        var retained = await db.Recovery.InspectAsync(CancellationToken.None);
        Assert.NotNull(retained.OwnerId);
        // The store is called only after the assistant's explicit operator reconciliation confirmations.
        await db.Recovery.ReleaseAsync(retained, "incident-267", CancellationToken.None);
        Assert.True(await replacement.TryAcquireAsync(CancellationToken.None));
    }

    private static object LockParameters => new { PostgresPayoutManagerLease.LockNamespace, PostgresPayoutManagerLease.LockKey };

    [PostgresIntegrationFact]
    public async Task MissingOwnershipSchema_HasMigrationGuidance()
    {
        await using var db = await Database.CreateAsync();
        await db.Connection.ExecuteAsync("DROP TABLE payout_manager_ownership");
        var error = await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.InspectAsync(CancellationToken.None));
        Assert.Contains("add_payout_manager_ownership.sql", error.Message);
        Assert.Equal(1, error.ExitCode);
    }

    [PostgresIntegrationFact]
    public async Task ActiveLock_SkipsBlockedHistory_AndInspectorDoesNotAcquireLock()
    {
        await using var db = await Database.CreateAsync();
        await db.Connection.ExecuteAsync("SELECT pg_advisory_lock(@LockNamespace, @LockKey)", LockParameters);
        using(var tx = db.Connection.BeginTransaction())
        {
            await db.Connection.ExecuteAsync("LOCK TABLE payment_batches IN ACCESS EXCLUSIVE MODE", transaction: tx);
            var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
            Assert.True(snapshot.LockHeld);
            Assert.Empty(snapshot.Batches);
            Assert.Null(snapshot.EvidenceWarning);
        }
        await db.Connection.ExecuteAsync("SELECT pg_advisory_unlock(@LockNamespace, @LockKey)", LockParameters);
        using(var tx = db.Connection.BeginTransaction())
        {
            await db.Connection.ExecuteAsync("LOCK TABLE payment_batches IN ACCESS EXCLUSIVE MODE", transaction: tx);
            var inspection = db.Recovery.InspectAsync(CancellationToken.None);
            // While evidence retrieval is blocked, inspection holds no advisory lock.
            await Task.Delay(100);
            Assert.True(await db.Connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_xact_lock(@LockNamespace, @LockKey)", LockParameters, tx));
            var snapshot = await inspection;
            Assert.NotNull(snapshot.EvidenceWarning);
            Assert.Equal(db.Owner, snapshot.OwnerId);
        }
    }

    [PostgresIntegrationFact]
    public async Task LargeHistory_UsesIndex_AndReturnsLatestTen()
    {
        await using var db = await Database.CreateAsync();
        await db.Connection.ExecuteAsync(@"UPDATE payment_batches SET created=now()+interval '1 day';
            INSERT INTO payment_batches
            SELECT 'large', i::text, now()-i*interval '1 second' FROM generate_series(1,100000) i;
            ANALYZE payment_batches");
        var plan = await db.Connection.ExecuteScalarAsync<string>(@"EXPLAIN (ANALYZE, FORMAT JSON)
            SELECT poolid, transactionconfirmationdata, created FROM payment_batches
            ORDER BY created DESC,poolid,transactionconfirmationdata LIMIT 10");
        Assert.Contains("idx_payment_batches_recovery_created", plan);
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        Assert.Null(snapshot.EvidenceWarning);
        Assert.Equal(10, snapshot.Batches.Count);
        Assert.Equal("test-transaction", snapshot.Batches[0].TransactionId);
    }

    [PostgresIntegrationFact]
    public async Task DifferentBatchSchema_IsIdentifiedAndNeverShownAsEvidence()
    {
        await using var db = await Database.CreateAsync();
        await using var other = await Database.CreateAsync();
        await db.Connection.ExecuteAsync("DROP TABLE payment_batches");
        var factory = new PgConnectionFactory(new NpgsqlConnectionStringBuilder(db.ConnectionString)
        { SearchPath = $"\"{db.Schema}\",\"{other.Schema}\"" }.ConnectionString);
        var snapshot = await new PostgresPayoutOwnershipRecovery(factory).InspectAsync(CancellationToken.None);
        Assert.Equal(db.Schema, snapshot.Schema);
        Assert.Equal(other.Schema, snapshot.BatchSchema);
        Assert.NotNull(snapshot.BatchRelationId);
        Assert.NotNull(snapshot.EvidenceWarning);
        Assert.Empty(snapshot.Batches);
    }

    [PostgresIntegrationFact]
    public async Task AuditFailure_RollsBackRelease_AndAuditSurvivesNextLease()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync("ALTER TABLE payout_manager_recovery_events ADD CONSTRAINT reject_fixture CHECK(false)");
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(snapshot, "incident", CancellationToken.None));
        Assert.Equal(db.Owner, await db.OwnerAsync());
        await db.Connection.ExecuteAsync("ALTER TABLE payout_manager_recovery_events DROP CONSTRAINT reject_fixture");
        var receipt = await db.Recovery.ReleaseAsync(snapshot, "incident-267", CancellationToken.None);
        await using(var lease = db.NewLease())
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        Assert.Equal(db.Owner, await db.Connection.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_recovery_events WHERE event_id=@EventId", receipt));
        Assert.Equal("incident-267", await db.Connection.ExecuteScalarAsync<string>("SELECT incident_reference FROM payout_manager_recovery_events"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Connection.ExecuteAsync("DELETE FROM payout_manager_recovery_events"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Connection.ExecuteAsync("UPDATE payout_manager_recovery_events SET incident_reference='changed'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Connection.ExecuteAsync("TRUNCATE payout_manager_recovery_events"));
    }

    [PostgresIntegrationFact]
    public async Task LostCommitResponse_ReinspectionDistinguishesCommittedAndRolledBack()
    {
        foreach(var commitFirst in new[] { false, true })
        {
            await using var db = await Database.CreateAsync();
            var recovery = new PostgresPayoutOwnershipRecovery(db.Factory, tx =>
            {
                if(commitFirst) tx.Commit();
                throw new NpgsqlException("simulated lost commit response");
            });
            var snapshot = await recovery.InspectAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<PayoutRecoveryException>(() => recovery.ReleaseAsync(snapshot, "incident", CancellationToken.None));
            Assert.Contains("commit outcome is unknown", error.Message);
            var after = await db.Recovery.InspectAsync(CancellationToken.None);
            Assert.Equal(commitFirst, after.OwnerId == null);
            Assert.Equal(commitFirst ? 1 : 0, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_recovery_events"));
        }
    }

    [PostgresIntegrationFact]
    public async Task IdentityChanges_AreRefusedWithoutAuditOrMutation()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        foreach(var changed in new[] { snapshot with { Database = "other" }, snapshot with { DatabaseId = -1 },
                    snapshot with { DatabaseRole = "other" }, snapshot with { ServerAddress = "192.0.2.1" },
                    snapshot with { ServerPort = -1 } })
            await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(changed, "incident", CancellationToken.None));
        Assert.Equal(db.Owner, await db.OwnerAsync());
        Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_recovery_events"));
    }

    [PostgresIntegrationFact]
    public async Task ChangedActualDatabaseTarget_WithSameSchemaAndOwner_IsRefused()
    {
        await using var original = await Database.CreateAsync();
        var target = "recovery_target_" + Guid.NewGuid().ToString("N");
        await original.Connection.ExecuteAsync($"CREATE DATABASE {target}");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"))
            { Database = target, Pooling = false }.ConnectionString;
            await using var replacement = await Database.CreateAsync(connection, original.Schema);
            await replacement.Connection.ExecuteAsync("UPDATE payout_manager_ownership SET owner_id=@Owner", new { original.Owner });
            var inspected = await original.Recovery.InspectAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<PayoutRecoveryException>(() => replacement.Recovery.ReleaseAsync(inspected, "incident", CancellationToken.None));
            Assert.Contains("database, role, endpoint", error.Message);
            Assert.Equal(original.Owner, await replacement.OwnerAsync());
        }
        finally { await original.Connection.ExecuteAsync($"DROP DATABASE {target} WITH (FORCE)"); }
    }

    [PostgresIntegrationFact]
    public async Task ChangedActualRole_AndMissingPrivileges_AreReportedSeparately()
    {
        await using var db = await Database.CreateAsync();
        var role = "recovery_reader_" + Guid.NewGuid().ToString("N");
        await db.Connection.ExecuteAsync($"CREATE ROLE {role}; GRANT USAGE ON SCHEMA \"{db.Schema}\" TO {role}");
        var restricted = new PostgresPayoutOwnershipRecovery(new DelegateFactory(async () =>
        {
            var connection = new NpgsqlConnection(db.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync($"SET ROLE {role}; SET search_path TO \"{db.Schema}\"");
            return connection;
        }));
        try
        {
            var denied = await Assert.ThrowsAsync<PayoutRecoveryException>(() => restricted.InspectAsync(CancellationToken.None));
            Assert.Contains("grant SELECT", denied.Message);
            await db.Connection.ExecuteAsync($"GRANT SELECT, UPDATE ON payout_manager_ownership TO {role}");
            var snapshot = await restricted.InspectAsync(CancellationToken.None);
            Assert.Equal(role, snapshot.DatabaseRole);
            Assert.NotNull(snapshot.EvidenceWarning);
            var original = await db.Recovery.InspectAsync(CancellationToken.None);
            var changed = await Assert.ThrowsAsync<PayoutRecoveryException>(() => restricted.ReleaseAsync(original, "incident", CancellationToken.None));
            Assert.Contains("database, role, endpoint", changed.Message);
            var auditDenied = await Assert.ThrowsAsync<PayoutRecoveryException>(() => restricted.ReleaseAsync(snapshot, "incident", CancellationToken.None));
            Assert.Contains("add_payout_recovery_audit.sql", auditDenied.Message);
            Assert.Equal(db.Owner, await db.OwnerAsync());
        }
        finally { await db.Connection.ExecuteAsync($"DROP OWNED BY {role}; DROP ROLE {role}"); }
    }

    [PostgresIntegrationFact]
    public async Task ReplacedHistoryAfterConfirmation_IsRefused()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        await db.Connection.ExecuteAsync("ALTER TABLE payment_batches RENAME TO old_batches; CREATE TABLE payment_batches (LIKE old_batches INCLUDING ALL)");
        var error = await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(snapshot, "incident", CancellationToken.None));
        Assert.Contains("payment-history relation changed", error.Message);
        Assert.Equal(db.Owner, await db.OwnerAsync());
    }

    [PostgresIntegrationFact]
    public async Task TimestampDetection_OnlyUsesCurrentSchema()
    {
        await using var db = await Database.CreateAsync();
        await using var other = await Database.CreateAsync();
        await other.Connection.ExecuteAsync("CREATE TABLE shares(created timestamp without time zone)");
        Assert.Null(await Program.GetPostgresColumnType(db.Factory, "shares", "created"));
        await db.Connection.ExecuteAsync("CREATE TABLE shares(created timestamptz)");
        Assert.Equal("timestamp with time zone", await Program.GetPostgresColumnType(db.Factory, "shares", "created"));
    }

    [PostgresIntegrationFact]
    public async Task DeadlineDuringRelease_RollsBackOwnerAndAudit()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        using var blocker = db.Connection.BeginTransaction();
        await db.Connection.ExecuteAsync("LOCK TABLE payout_manager_recovery_events IN ACCESS EXCLUSIVE MODE", transaction: blocker);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Recovery.ReleaseAsync(snapshot, "incident", deadline.Token));
        blocker.Rollback();
        Assert.Equal(db.Owner, await db.OwnerAsync());
        Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_recovery_events"));
    }

    [PostgresIntegrationFact]
    public async Task Migration_RejectsWrongIndex_AndPreservesLegacyAuditReceipts()
    {
        await using var db = await Database.CreateAsync();
        var migration = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_payout_recovery_audit.sql"));
        await db.Connection.ExecuteAsync("DROP INDEX idx_payment_batches_recovery_created; CREATE INDEX idx_payment_batches_recovery_created ON payment_batches(poolid)");
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Connection.ExecuteAsync(migration));
        Assert.Contains("Invalid idx_payment_batches", error.MessageText);
        await db.Connection.ExecuteAsync("ROLLBACK; DROP INDEX idx_payment_batches_recovery_created");
        await db.Connection.ExecuteAsync(migration);
        var receipt = await db.Recovery.ReleaseAsync(await db.Recovery.InspectAsync(CancellationToken.None), "legacy-receipt", CancellationToken.None);
        await db.Connection.ExecuteAsync($"ALTER TABLE {db.AuditSchema}.payout_manager_recovery_events SET SCHEMA \"{db.Schema}\"; DROP SCHEMA {db.AuditSchema} CASCADE");
        await db.Connection.ExecuteAsync(migration);
        Assert.Equal(db.Owner, await db.Connection.ExecuteScalarAsync<Guid>("SELECT owner_id FROM payout_manager_recovery_events WHERE event_id=@EventId", receipt));
        Assert.Equal("legacy-receipt", await db.Connection.ExecuteScalarAsync<string>("SELECT incident_reference FROM payout_manager_recovery_events"));
    }

    [PostgresIntegrationFact]
    public async Task Migration_RejectsApplicationOwnedAuditLookalikes()
    {
        await using var db = await Database.CreateAsync();
        var role = "audit_impostor_" + Guid.NewGuid().ToString("N");
        await db.Connection.ExecuteAsync($"CREATE ROLE {role}; CREATE TABLE payout_manager_recovery_events (forged text); ALTER TABLE payout_manager_recovery_events OWNER TO {role}");
        try
        {
            var migration = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_payout_recovery_audit.sql"));
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Connection.ExecuteAsync(migration));
            Assert.Contains("plain table owned by this administrator", error.MessageText);
            await db.Connection.ExecuteAsync("ROLLBACK");
            Assert.Equal(db.Owner, await db.OwnerAsync());
            Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {db.AuditSchema}.payout_manager_recovery_events"));
        }
        finally { await db.Connection.ExecuteAsync($"ROLLBACK; DROP OWNED BY {role}; DROP ROLE {role}"); }
    }

    [PostgresIntegrationFact]
    public async Task AuditAppend_RequiresOwnExclusiveTwoKeyLock_AndDuplicateReceiptKeepsOwner()
    {
        await using var db = await Database.CreateAsync();
        var snapshot = await db.Recovery.InspectAsync(CancellationToken.None);
        var append = $"SELECT * FROM {db.AuditSchema}.record_payout_recovery(@eventId,@owner,7::bigint,'test',123,now(),now(),'test',123,'preclaimed')";
        var args = new { eventId = Guid.NewGuid(), owner = db.Owner };
        var keys = $"{PostgresPayoutManagerLease.LockNamespace},{PostgresPayoutManagerLease.LockKey}";
        await using(var other = new NpgsqlConnection(db.ConnectionString))
        {
            await other.OpenAsync();
            await other.ExecuteAsync($"SELECT pg_advisory_lock({keys})");
            var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Connection.QueryAsync(append, args));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, denied.SqlState);
        }
        foreach(var wrongLock in new[] { "SELECT 1", $"SELECT pg_advisory_xact_lock_shared({keys})",
            $"SELECT pg_advisory_xact_lock({((long) PostgresPayoutManagerLease.LockNamespace << 32) | PostgresPayoutManagerLease.LockKey}::bigint)" })
        {
            using var tx = db.Connection.BeginTransaction();
            await db.Connection.ExecuteAsync(wrongLock, transaction: tx);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Connection.QueryAsync(append, args, tx));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, denied.SqlState);
            tx.Rollback();
        }
        Assert.Equal(0, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_recovery_events"));
        Assert.Equal(2, await db.Connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@AuditSchema AND 'search_path=pg_catalog, pg_temp'=ANY(p.proconfig)", new { db.AuditSchema }));
        using(var tx = db.Connection.BeginTransaction())
        {
            await db.Connection.ExecuteAsync($"SELECT pg_advisory_xact_lock({keys})", transaction: tx);
            await db.Connection.QueryAsync(append, args, tx);
            tx.Commit();
        }
        var error = await Assert.ThrowsAsync<PayoutRecoveryException>(() => db.Recovery.ReleaseAsync(snapshot, "incident", CancellationToken.None));
        Assert.Contains("audit event already exists", error.Message);
        Assert.Contains(db.AuditSchema, error.Message);
        Assert.Equal(ProcessExitCodes.PayoutOwnershipBlocked, error.ExitCode);
        Assert.Equal(db.Owner, await db.OwnerAsync());
        Assert.Equal(1, await db.Connection.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_recovery_events"));
    }

    [PostgresIntegrationFact]
    public async Task FreshProcesses_ApplyExplicitTimestampMode_AndMainReturns78ForBothDetectionFailures()
    {
        await using var db = await Database.CreateAsync();
        var target = "recovery_timestamp_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), target);
        Directory.CreateDirectory(directory);
        await db.Connection.ExecuteAsync($"CREATE DATABASE {target}");
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Database = target, Pooling = false };
            await using var fixture = new NpgsqlConnection(settings.ConnectionString);
            await fixture.OpenAsync();
            await fixture.ExecuteAsync("CREATE TABLE payout_manager_ownership (id smallint PRIMARY KEY, generation bigint, owner_id uuid, owner_host text, owner_process_id int, acquired timestamptz, released timestamptz); INSERT INTO payout_manager_ownership VALUES(1,0,NULL,NULL,NULL,NULL,NULL)");
            var config = JObject.Parse("""
                {"paymentProcessing":{"enabled":false},"api":{"enabled":false},
                 "logging":{"level":"info","enableConsoleLog":true,"enableConsoleColors":false},
                 "pools":[{"id":"timestamp-test","coin":"bitcoin","enabled":true,"enableInternalStratum":false,"address":"fixture","ports":{"3032":{"difficulty":1}},
                   "daemons":[{"host":"127.0.0.1","port":1,"user":"fixture","password":"fixture"}],
                   "paymentProcessing":{"enabled":false}}]}
                """);
            var postgres = new JObject { ["host"] = settings.Host, ["port"] = settings.Port, ["user"] = settings.Username,
                ["password"] = settings.Password, ["database"] = target };
            config["persistence"] = new JObject { ["postgres"] = postgres };
            config["shareRecoveryFile"] = Path.Combine(directory, "shares.txt");
            config["shareRecoveryStateDirectory"] = directory;
            var configFile = Path.Combine(directory, "config.json");
            foreach(var legacy in new[] { true, false })
            {
                postgres["enableLegacyTimestamps"] = legacy;
                await File.WriteAllTextAsync(configFile, config.ToString());
                var result = await RunProcessAsync(directory, "payout-timestamp-probe", configFile, legacy.ToString());
                Assert.True(result.ExitCode == 0, result.Output);
            }
            postgres.Remove("enableLegacyTimestamps");
            await File.WriteAllTextAsync(configFile, config.ToString());
            foreach(var legacy in new[] { false, true })
            {
                if(legacy) await fixture.ExecuteAsync("CREATE TABLE shares(created timestamp without time zone)");
                var result = await RunProcessAsync(directory, "payout-main", "-c", configFile, "--no-payout-recovery-prompt");
                Assert.True(result.ExitCode == 78, result.Output);
                Assert.Contains(legacy ? "Legacy PostgreSQL timestamps detected" : "Unable to determine the PostgreSQL timestamp type", result.Output);
            }
        }
        finally
        {
            await db.Connection.ExecuteAsync($"DROP DATABASE {target} WITH (FORCE)");
            Directory.Delete(directory, true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string directory, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach(var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
            "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
            Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll") }) start.ArgumentList.Add(argument);
        foreach(var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class DelegateFactory(Func<Task<IDbConnection>> open) : IConnectionFactory
    {
        public Task<IDbConnection> OpenConnectionAsync() => open();
    }

    private sealed class Database : IAsyncDisposable
    {
        public string Schema { get; private set; } = $"Recovery_{Guid.NewGuid():N}"; // Exercise quoted mixed-case schemas.
        public Guid Owner { get; } = Guid.NewGuid();
        public NpgsqlConnection Connection { get; private set; }
        public string ConnectionString { get; private set; }
        public string AuditSchema { get; private set; }
        public PgConnectionFactory Factory { get; private set; }
        public PostgresPayoutOwnershipRecovery Recovery { get; private set; }

        public static async Task<Database> CreateAsync(string connectionString = null, string schema = null)
        {
            var db = new Database();
            if(schema != null) db.Schema = schema;
            connectionString ??= Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES");
            // Every fixture creates a distinct search_path/database pool key. Do not
            // retain idle connectors after dropping its schema in a shared CI server.
            connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            db.ConnectionString = connectionString;
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
                await db.Connection.ExecuteAsync(await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_payout_recovery_audit.sql")));
                db.AuditSchema = PostgresPayoutOwnershipRecovery.AuditSchema(await db.Connection.ExecuteScalarAsync<long>("SELECT 'payout_manager_ownership'::regclass::oid::bigint"));
                await db.Connection.ExecuteAsync($"SET search_path TO \"{db.Schema}\", {db.AuditSchema}, public");
                db.Factory = new PgConnectionFactory(new NpgsqlConnectionStringBuilder(connectionString)
                {
                    SearchPath = $"\"{db.Schema}\",public",
                }.ConnectionString);
                db.Recovery = new PostgresPayoutOwnershipRecovery(db.Factory);
                return db;
            }
            catch
            {
                await db.Connection.ExecuteAsync("ROLLBACK");
                await db.DisposeAsync();
                throw;
            }
        }

        public PostgresPayoutManagerLease NewLease() => new(Factory);
        public Task<Guid?> OwnerAsync() => Connection.ExecuteScalarAsync<Guid?>("SELECT owner_id FROM payout_manager_ownership");

        public async ValueTask DisposeAsync()
        {
            if(AuditSchema != null) await Connection.ExecuteAsync($"DROP SCHEMA IF EXISTS {AuditSchema} CASCADE");
            await Connection.ExecuteAsync($"SET search_path TO public; DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            await Connection.DisposeAsync();
        }
    }
}
