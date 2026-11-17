using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Payments;
using Miningcore.Tests.Persistence.Postgres;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Payments;

public partial class WalletPayoutControlIntegrationTests
{
    // These cases need PostgreSQL only; run them on every supported major in CI.
    [PostgresIntegrationFact]
    public async Task NonSuperuserControlOwner_InstallAndUpgradeRequireInheritedStatisticsPrivileges()
    {
        foreach(var upgrade in new[] { false, true })
        {
            await using var db = await Database.Create(installControl: false);
            await using var owner = await db.CreateControlOwner();
            Assert.False(await owner.ExecuteScalarAsync<bool>("SELECT rolsuper FROM pg_roles WHERE rolname=current_user"));
            await owner.ExecuteAsync("SELECT set_config('miningcore.wallet_control_app_role',@role,false)", new { role = db.AppRole });
            if(upgrade)
                await owner.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WalletControlV1.sql")));
            var sql = await File.ReadAllTextAsync(PostgresTestScripts.PathFor(
                upgrade ? "upgrade_wallet_payout_control_v2.sql" : "add_wallet_payout_control.sql"));
            // Membership without inheritance is insufficient on PostgreSQL 15 and 16+.
            foreach(var membershipOnly in new[] { false, true })
            {
                if(membershipOnly)
                    await db.Admin.ExecuteAsync($"ALTER ROLE {db.ControlOwnerRole} NOINHERIT; GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
                var error = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(sql));
                Assert.Equal("55000", error.SqlState);
                Assert.Contains("effective pg_read_all_stats", error.MessageText);
                await owner.ExecuteAsync("ROLLBACK");
                if(upgrade)
                {
                    Assert.Equal(1, await owner.ExecuteScalarAsync<int>("SELECT protocol_version FROM miningcore_payout_control.state"));
                    Assert.Equal(1, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM miningcore_payout_control.events"));
                    Assert.Equal(0, await owner.ExecuteScalarAsync<int>("""
                        SELECT count(*) FROM information_schema.columns WHERE table_schema='miningcore_payout_control'
                            AND table_name='batches' AND column_name='admitting_pid'
                        """));
                }
                else
                    Assert.False(await owner.ExecuteScalarAsync<bool>("SELECT to_regnamespace('miningcore_payout_control') IS NOT NULL"));
            }
            // Regrant after INHERIT: PostgreSQL 16+ captures inheritance on each grant.
            await db.Admin.ExecuteAsync($"REVOKE pg_read_all_stats FROM {db.ControlOwnerRole}; ALTER ROLE {db.ControlOwnerRole} INHERIT; GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
            await owner.ExecuteAsync(sql);
            await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
            Assert.True((await db.State()).Paused);
            await owner.ExecuteAsync("SELECT miningcore_payout_control.set_paused(false,@revision,'visibility verified')", new { revision = (await db.State()).Revision });
            using var runtime = await db.Factory.OpenConnectionAsync();
            await runtime.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
            var batch = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
            Assert.True(await runtime.ExecuteScalarAsync<bool>(AdmitSessionBatch, batch));
            Assert.True(await runtime.ExecuteScalarAsync<bool>("""
                SELECT admitting_pid=pg_backend_pid() AND admitting_backend_start=
                    (SELECT backend_start FROM pg_stat_activity WHERE pid=pg_backend_pid())
                FROM miningcore_payout_control.batches WHERE batch_id=@token
                """, batch));
            await runtime.ExecuteAsync(FinishSessionBatch, batch);
            Assert.Equal(0, (await db.State()).OutstandingBatches);
            Assert.False(await runtime.ExecuteScalarAsync<bool>("SELECT pg_has_role(current_user,'pg_read_all_stats','MEMBER')"));
        }
    }

    [PostgresIntegrationFact]
    public async Task NonSuperuserControlOwner_RevocationFailsPreflightAndPreservesOutstandingBatch()
    {
        await using var db = await Database.Create(installControl: false);
        await using var owner = await db.CreateControlOwner();
        await db.Admin.ExecuteAsync($"GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
        await owner.ExecuteAsync("SELECT set_config('miningcore.wallet_control_app_role',@role,false)", new { role = db.AppRole });
        await owner.ExecuteAsync(await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_wallet_payout_control.sql")));
        await owner.ExecuteAsync("SELECT miningcore_payout_control.set_paused(false,1,'visibility fixture')");
        await db.Admin.ExecuteAsync($"REVOKE pg_read_all_stats FROM {db.ControlOwnerRole}");
        await using(var lease = new PostgresPayoutManagerLease(db.Factory))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ValidateWalletControlAsync(CancellationToken.None));
            Assert.Contains("pg_read_all_stats", error.Message);
            Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_ownership WHERE owner_id IS NOT NULL"));
            Assert.True(lease.CanReleaseOwnership);
        }
        using var runtime = await db.Factory.OpenConnectionAsync();
        await runtime.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var batch = new { token = Guid.NewGuid(), owner = Guid.NewGuid() };
        var denied = await Assert.ThrowsAsync<PostgresException>(() => runtime.ExecuteScalarAsync<bool>(AdmitSessionBatch, batch));
        Assert.Equal("55000", denied.SqlState);
        Assert.Contains("cannot read backend_start", denied.MessageText);
        Assert.Equal(0, await owner.ExecuteScalarAsync<int>("SELECT count(*) FROM miningcore_payout_control.batches"));
        await db.Admin.ExecuteAsync($"GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
        await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
        Assert.True(await runtime.ExecuteScalarAsync<bool>(AdmitSessionBatch, batch));
        // SQL CHECK accepts UNKNOWN: both explicit non-null guards are necessary.
        foreach(var column in new[] { "admitting_pid", "admitting_backend_start" })
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecuteAsync(
                $"UPDATE miningcore_payout_control.batches SET {column}=NULL WHERE batch_id=@token", batch));
            Assert.Equal("23514", invalid.SqlState);
        }
        await db.Admin.ExecuteAsync($"REVOKE pg_read_all_stats FROM {db.ControlOwnerRole}");
        var unfinished = await Assert.ThrowsAsync<PostgresException>(() => runtime.ExecuteAsync(FinishSessionBatch, batch));
        Assert.Equal("55000", unfinished.SqlState);
        Assert.Contains("cannot read backend_start", unfinished.MessageText);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        await db.Admin.ExecuteAsync($"GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
        await runtime.ExecuteAsync(FinishSessionBatch, batch);
        await runtime.ExecuteAsync(FinishSessionBatch, batch);
        Assert.Equal(0, (await db.State()).OutstandingBatches);
    }

    [PostgresIntegrationFact]
    public async Task PreflightChecksBothActualFunctionOwnersForStatisticsVisibility()
    {
        foreach(var signature in new[] { "begin_batch(uuid,uuid,bigint,text,text)", "finish_batch(uuid,uuid,bigint)" })
        {
            await using var db = await Database.Create();
            await using var owner = await db.CreateControlOwner();
            await db.Admin.ExecuteAsync($"GRANT CREATE ON SCHEMA miningcore_payout_control TO {db.ControlOwnerRole}; ALTER FUNCTION miningcore_payout_control.{signature} OWNER TO {db.ControlOwnerRole}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None));
            await db.Admin.ExecuteAsync($"GRANT pg_read_all_stats TO {db.ControlOwnerRole}");
            await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
        }
    }
}
