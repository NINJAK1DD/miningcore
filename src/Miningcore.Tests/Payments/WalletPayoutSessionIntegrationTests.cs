using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Payments;
using Miningcore.Persistence.Postgres;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    private const string AdmitSessionBatch = """
        SELECT miningcore_payout_control.begin_batch(@token,@owner,1,'session-test',repeat('a',64))
        """;
    private const string FinishSessionBatch = """
        SELECT miningcore_payout_control.finish_batch(batch_id,owner_id,generation)
        FROM miningcore_payout_control.batches WHERE batch_id=@token
        """;

    [WalletControlFact]
    public async Task DisposedGuardClosesBackendInsteadOfReturningCompletionAuthorityToPool()
    {
        await using var db = await Database.Create();
        await db.SetPaused(false);
        var pooled = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = true, MaxPoolSize = 1 };
        var factory = new PgConnectionFactory(pooled.ConnectionString);
        var lease = new PostgresPayoutManagerLease(factory);
        int pid;
        Guid token;
        await using(lease)
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("pooled", new string('b',64))));
            pid = await db.Admin.ExecuteScalarAsync<int>("SELECT admitting_pid FROM miningcore_payout_control.batches");
            token = await db.Admin.ExecuteScalarAsync<Guid>("SELECT batch_id FROM miningcore_payout_control.batches");
        } // Outstanding work keeps durable ownership, but the physical guard must close.
        // pg_stat_activity can briefly still see a disconnect being processed by the server.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while(await db.Admin.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid=@pid)", new { pid }))
        {
            Assert.True(DateTime.UtcNow < deadline, "Disposed payout guard backend remained available for pooling");
            await Task.Delay(20);
        }
        using var next = await factory.OpenConnectionAsync();
        try
        {
            await next.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
            await Assert.ThrowsAsync<PostgresException>(() => next.ExecuteAsync(FinishSessionBatch, new { token }));
            Assert.Equal(1, (await db.State()).OutstandingBatches);
            await next.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        }
        finally
        {
            // Evict only this fixture's pool, never unrelated test pools.
            NpgsqlConnection.ClearPool((NpgsqlConnection) next);
        }
    }

    [WalletControlFact]
    public async Task CompletionRequiresOriginalBackend_EvenWithReadableIdentityAndReacquiredLock()
    {
        await using var db = await Database.Create();
        await db.SetPaused(false);
        await using var original = new NpgsqlConnection(db.ConnectionString);
        await original.OpenAsync();
        await original.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var completed = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await original.ExecuteScalarAsync<bool>(AdmitSessionBatch, completed));
        await original.ExecuteAsync(FinishSessionBatch, completed);
        await original.ExecuteAsync(FinishSessionBatch, completed); // Original-session retry is idempotent.
        var pending = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await original.ExecuteScalarAsync<bool>(AdmitSessionBatch, pending));

        await using var replacement = new NpgsqlConnection(db.ConnectionString);
        await replacement.OpenAsync();
        Assert.Equal(pending.owner, await replacement.ExecuteScalarAsync<Guid>(
            "SELECT owner_id FROM miningcore_payout_control.batches WHERE batch_id=@token", pending));
        // Even voluntary lock handoff while the original session lives conveys no authority.
        await original.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await replacement.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(FinishSessionBatch, pending));
        Assert.Equal("55000", rejected.SqlState);
        await original.CloseAsync(); // Physical close: the fixture explicitly disables pooling.
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(FinishSessionBatch, pending));
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(FinishSessionBatch, completed));
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(AdmitSessionBatch,
            new { token = Guid.NewGuid(), owner = Guid.NewGuid() }));
        await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(false));
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(
            "UPDATE miningcore_payout_control.batches SET admitting_pid=pg_backend_pid() WHERE batch_id=@token", pending));
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(
            "SELECT miningcore_payout_control.reconcile_batch(@token,2,'forged reconciliation')", pending));

        // Deterministically model PID reuse without waiting for the OS to recycle one.
        // Only the trusted test administrator can edit these fields.
        var pid = await replacement.ExecuteScalarAsync<int>("SELECT pg_backend_pid()");
        await db.Admin.ExecuteAsync("""
            UPDATE miningcore_payout_control.batches SET admitting_pid=@pid,
                admitting_backend_start=(SELECT backend_start-interval '1 second' FROM pg_stat_activity WHERE pid=@pid)
            WHERE batch_id=@token
            """, new { pid, pending.token });
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(FinishSessionBatch, pending));
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM miningcore_payout_control.events WHERE action='reconcile'"));

        await replacement.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await db.SetPaused(true);
        await db.Operator.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        await db.Operator.ExecuteAsync("SELECT miningcore_payout_control.reconcile_batch(@token,@revision,'verified original process death and wallet history')",
            new { pending.token, revision = (await db.State()).Revision });
        await db.Operator.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        Assert.Equal("reconciled", await db.Admin.ExecuteScalarAsync<string>(
            "SELECT resolution FROM miningcore_payout_control.batches WHERE batch_id=@token", pending));
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM miningcore_payout_control.events WHERE action='reconcile' AND batch_id=@token", pending));
        await db.SetPaused(false);
        await replacement.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var next = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await replacement.ExecuteScalarAsync<bool>(AdmitSessionBatch, next));
        await replacement.ExecuteAsync(FinishSessionBatch, next);
        Assert.Equal(0, (await db.State()).OutstandingBatches);
    }

    [WalletControlFact]
    public async Task ProtocolOneUpgrade_PreservesOutstandingHistoryAndRequiresOperatorReconciliation()
    {
        await using var db = await Database.Create(installControl: false);
        await db.Admin.ExecuteAsync("SELECT set_config('miningcore.wallet_control_app_role',@role,false)", new { role = db.AppRole });
        await db.Admin.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WalletControlV1.sql")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None));
        // Version 1 is deliberately incompatible with the new API reader; use SQL for setup.
        await db.Admin.ExecuteAsync("SELECT miningcore_payout_control.set_paused(false,1,'upgrade fixture')");
        await using var original = new NpgsqlConnection(db.ConnectionString);
        await original.OpenAsync();
        await original.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var completed = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await original.ExecuteScalarAsync<bool>(AdmitSessionBatch, completed));
        await original.ExecuteAsync(FinishSessionBatch, completed);
        var pending = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await original.ExecuteScalarAsync<bool>(AdmitSessionBatch, pending));
        var upgrade = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("upgrade_wallet_payout_control_v2.sql"));
        // Active guard and unpaused state each refuse the upgrade atomically.
        await Assert.ThrowsAsync<PostgresException>(() => db.Admin.ExecuteAsync(upgrade));
        await db.Admin.ExecuteAsync("ROLLBACK");
        await original.CloseAsync();
        // Reproduce the reported exploit against the archived protocol-1 SQL:
        // a replacement runtime login can complete a batch it did not admit.
        await using(var attacker = new NpgsqlConnection(db.ConnectionString))
        {
            await attacker.OpenAsync();
            await attacker.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
            await attacker.ExecuteAsync(FinishSessionBatch, pending);
            Assert.Equal("completed", await attacker.ExecuteScalarAsync<string>(
                "SELECT resolution FROM miningcore_payout_control.batches WHERE batch_id=@token", pending));
            Assert.Equal(0, await attacker.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM miningcore_payout_control.events WHERE action='reconcile'"));
            pending = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
            Assert.True(await attacker.ExecuteScalarAsync<bool>(AdmitSessionBatch, pending));
        }
        await Assert.ThrowsAsync<PostgresException>(() => db.Admin.ExecuteAsync(upgrade));
        await db.Admin.ExecuteAsync("ROLLBACK");
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT protocol_version FROM miningcore_payout_control.state"));
        Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='miningcore_payout_control'
                AND table_name='batches' AND column_name='admitting_pid'
            """));
        await db.Admin.ExecuteAsync("SELECT miningcore_payout_control.set_paused(true,2,'coordinated shutdown verified')");
        await using var replacement = new NpgsqlConnection(db.ConnectionString);
        await replacement.OpenAsync();
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(upgrade));
        await replacement.ExecuteAsync("ROLLBACK");
        await db.Admin.ExecuteAsync(upgrade);
        await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
        Assert.True((await db.State()).Paused);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        Assert.Equal(4, (await db.State()).Revision);
        Assert.Equal("completed", await replacement.ExecuteScalarAsync<string>(
            "SELECT resolution FROM miningcore_payout_control.batches WHERE batch_id=@token", completed));
        Assert.Equal(1, await replacement.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM miningcore_payout_control.events WHERE action='upgrade'"));
        await replacement.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        await Assert.ThrowsAsync<PostgresException>(() => replacement.ExecuteAsync(FinishSessionBatch, pending));
        await replacement.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await db.Admin.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        await db.Admin.ExecuteAsync("SELECT miningcore_payout_control.reconcile_batch(@token,4,'wallet history and accounting reconciled')", pending);
        await db.Admin.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await db.Admin.ExecuteAsync("SELECT miningcore_payout_control.set_paused(false,5,'reviewed recovery')");
        await replacement.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var next = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        Assert.True(await replacement.ExecuteScalarAsync<bool>(AdmitSessionBatch, next));
        await replacement.ExecuteAsync(FinishSessionBatch, next);
        await replacement.ExecuteAsync(FinishSessionBatch, next);
        await replacement.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await Assert.ThrowsAsync<PostgresException>(() => db.Admin.ExecuteAsync(upgrade));
        await db.Admin.ExecuteAsync("ROLLBACK");
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        Assert.Equal(6, (await db.State()).Revision);
        Assert.Equal(4, await replacement.ExecuteScalarAsync<int>("SELECT count(*) FROM miningcore_payout_control.batches"));
    }

    [Fact]
    public void ProtocolTwoInstallAndUpgradeUseIdenticalSecurityFunctions()
    {
        var install = File.ReadAllText(PostgresTestScripts.PathFor("add_wallet_payout_control.sql"));
        var upgrade = File.ReadAllText(PostgresTestScripts.PathFor("upgrade_wallet_payout_control_v2.sql"));
        foreach(var name in new[] { "begin_batch", "finish_batch", "set_paused", "reconcile_batch" })
        {
            var marker = "FUNCTION miningcore_payout_control." + name + "(";
            static string Body(string sql, string marker)
            {
                var start = sql.IndexOf(marker, StringComparison.Ordinal);
                var end = sql.IndexOf("END $$;", start, StringComparison.Ordinal);
                return sql[start..end].Replace("\r\n", "\n");
            }
            Assert.Equal(Body(install, marker), Body(upgrade, marker));
        }
    }
}
