using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Tests.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlAuditPrivilegeTests
{
    [PostgresIntegrationFact]
    public async Task FreshAndUpgradedAuditHistoryIsAdminOwnedAndApplicationReadOnly()
    {
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await db.OpenAsync();
        foreach(var upgrade in new[] {false, true})
        {
            var schema = "pearl_audit_" + Guid.NewGuid().ToString("N");
            var app = "pearl_app_" + Guid.NewGuid().ToString("N");
            var inherited = "pearl_group_" + Guid.NewGuid().ToString("N");
            try
            {
                await db.ExecuteAsync($"CREATE ROLE {app} LOGIN; CREATE ROLE {inherited}; GRANT {inherited} TO {app}; CREATE SCHEMA {schema}; GRANT ALL ON SCHEMA {schema} TO {app}; SET search_path TO {schema}, public");
                var fresh = (await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"))).Replace("SET ROLE miningcore;", $"SET ROLE {app};");
                var migration = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_pearl_candidates.sql"));
                // The security block is identical in fresh and upgrade paths.
                string Security(string text) => text.Split("-- BEGIN PEARL AUDIT PRIVILEGES")[1].Split("-- END PEARL AUDIT PRIVILEGES")[0];
                Assert.Equal(Security(fresh), Security(migration));
                await db.ExecuteAsync(fresh);
                await db.ExecuteAsync("RESET ROLE");
                if(upgrade)
                {
                    await db.ExecuteAsync($"ALTER TABLE pearl_payout_intent_releases OWNER TO {app}; GRANT ALL ON pearl_payout_intent_releases TO PUBLIC, {inherited}; GRANT UPDATE(reason) ON pearl_payout_intent_releases TO {app}, PUBLIC, {inherited}");
                    await db.ExecuteAsync(migration);
                    await db.ExecuteAsync(migration);
                }
                Assert.True(await db.ExecuteScalarAsync<bool>("SELECT pg_get_userbyid(relowner)=session_user FROM pg_class WHERE oid='pearl_payout_intent_releases'::regclass"));
                foreach(var sql in new[]
                {
                    "INSERT INTO pearl_payout_intent_releases(poolid,marker,recipients,created,releasedby,auditsha256,reason) VALUES('p','m','{}',now(),'app',repeat('a',64),'unauthorized audit record')",
                    "UPDATE pearl_payout_intent_releases SET reason='unauthorized history edit'",
                    "DELETE FROM pearl_payout_intent_releases",
                    "TRUNCATE pearl_payout_intent_releases",
                    "ALTER TABLE pearl_payout_intent_releases ADD COLUMN tampered BOOLEAN"
                })
                {
                    await db.ExecuteAsync($"SET SESSION AUTHORIZATION {app}");
                    try
                    {
                        var error = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql));
                        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                        await db.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_payout_intent_releases");
                    }
                    finally { await db.ExecuteAsync("RESET SESSION AUTHORIZATION"); }
                }
                await db.ExecuteAsync("INSERT INTO pearl_payout_intents VALUES('p',repeat('m',64),'{}',NULL,now())");
                var release = await File.ReadAllTextAsync(PostgresTestScripts.OperatorPathFor("release_pearl_payout_intent.sql"));
                var action = release[release.IndexOf("DO $$", StringComparison.Ordinal)..release.LastIndexOf("COMMIT;", StringComparison.Ordinal)];
                await using(var tx = await db.BeginTransactionAsync())
                {
                    await db.ExecuteAsync("""
                        SELECT set_config('miningcore.release_pool','p',true),
                        set_config('miningcore.release_marker',repeat('m',64),true),
                        set_config('miningcore.release_audit',repeat('a',64),true),
                        set_config('miningcore.release_reason','Verified no-broadcast test audit',true),
                        set_config('miningcore.release_ack','NO_TRANSACTION_CREATED',true)
                        """, transaction: tx);
                    await db.ExecuteAsync(action, transaction: tx);
                    await tx.CommitAsync();
                }
                Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_payout_intent_releases"));
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_payout_intents"));
                await db.ExecuteAsync(migration); // Re-running never discards the audit trail.
                Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM pearl_payout_intent_releases"));
            }
            finally
            {
                await db.ExecuteAsync($"ROLLBACK; RESET SESSION AUTHORIZATION; RESET ROLE; SET search_path TO public; DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE IF EXISTS {app}; DROP ROLE IF EXISTS {inherited}");
            }
        }
    }
}
