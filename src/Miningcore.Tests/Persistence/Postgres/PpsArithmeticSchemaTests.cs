using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class PpsArithmeticSchemaTests
{
    private static readonly DateTime Cutoff = new(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
    private static string Script(string name) => File.ReadAllText(Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../Miningcore/Persistence/Postgres/Scripts", name)))
        .Replace("\\set ON_ERROR_STOP on", "").Replace("SET ROLE miningcore;", "");

    [PostgresIntegrationFact]
    public async Task PreflightRejectsEveryMalformedArithmeticContract()
    {
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await db.OpenAsync();
        var schema = "pps_contract_" + Guid.NewGuid().ToString("N");
        try
        {
            await db.ExecuteAsync($"CREATE SCHEMA {schema}; SET search_path TO {schema}, public");
            await db.ExecuteAsync(Script("createdb.sql"));
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            Assert.True(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None));
            var damage = new[]
            {
                "ALTER TABLE pps_share_credits ALTER COLUMN arithmeticversion SET DEFAULT 1",
                "ALTER TABLE pps_share_credits ALTER COLUMN arithmeticversion DROP DEFAULT",
                "ALTER TABLE pps_share_credits ALTER COLUMN arithmeticversion SET DEFAULT NULL",
                "ALTER TABLE pps_share_credits DISABLE TRIGGER trg_pps_arithmetic_credit",
                "DROP TRIGGER trg_pps_arithmetic_credit ON pps_share_credits",
                "DROP TRIGGER trg_pps_arithmetic_credit ON pps_share_credits; CREATE TRIGGER trg_pps_arithmetic_credit AFTER INSERT ON pps_share_credits FOR EACH ROW EXECUTE FUNCTION guard_pps_arithmetic_credit()",
                "CREATE FUNCTION noop() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$; DROP TRIGGER trg_pps_arithmetic_credit ON pps_share_credits; CREATE TRIGGER trg_pps_arithmetic_credit BEFORE INSERT ON pps_share_credits FOR EACH ROW EXECUTE FUNCTION noop()",
                "CREATE OR REPLACE FUNCTION guard_pps_arithmetic_credit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
                "DROP TRIGGER trg_pps_arithmetic_credit ON pps_share_credits; CREATE TRIGGER trg_pps_arithmetic_credit BEFORE INSERT ON pps_share_credits FOR EACH ROW WHEN (false) EXECUTE FUNCTION guard_pps_arithmetic_credit()",
                "ALTER FUNCTION guard_pps_arithmetic_credit() RESET search_path",
                "ALTER TABLE pps_arithmetic_transitions DISABLE TRIGGER trg_pps_arithmetic_transition_immutable",
                "DROP TRIGGER trg_pps_arithmetic_transition_immutable ON pps_arithmetic_transitions",
                "CREATE OR REPLACE FUNCTION guard_pps_arithmetic_transition() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
                "ALTER TABLE pps_arithmetic_transitions DISABLE TRIGGER trg_pps_arithmetic_transition_truncate",
                "DROP TRIGGER trg_pps_arithmetic_transition_truncate ON pps_arithmetic_transitions",
                "ALTER TABLE pps_arithmetic_transitions DROP CONSTRAINT pps_arithmetic_transitions_pkey",
                "ALTER TABLE pps_arithmetic_transitions DROP CONSTRAINT pps_arithmetic_transitions_version_check",
                "ALTER TABLE pps_arithmetic_transitions DROP CONSTRAINT pps_arithmetic_transitions_effectivefrom_check",
                "ALTER TABLE pps_share_credits DROP CONSTRAINT ck_pps_arithmetic_version",
                "ALTER TABLE pps_share_credits DROP CONSTRAINT ck_pps_arithmetic_version; ALTER TABLE pps_share_credits ADD CONSTRAINT ck_pps_arithmetic_version CHECK (arithmeticversion >= 0)",
                "CREATE OR REPLACE FUNCTION activate_pps_binary64(scope text,cutoff timestamptz) RETURNS void LANGUAGE plpgsql AS $$ BEGIN RETURN; END $$",
            };
            foreach(var sql in damage)
            {
                await using(var tx = await db.BeginTransactionAsync())
                {
                    await db.ExecuteAsync(sql, transaction: tx);
                    Assert.False(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None), sql);
                    await tx.RollbackAsync();
                }
                Assert.True(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None), sql);
            }
            foreach(var change in new[] { "SET DEFAULT 1", "DROP DEFAULT" })
            {
                await db.ExecuteAsync("ALTER TABLE pps_share_credits ALTER COLUMN arithmeticversion " + change);
                Assert.False(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None));
                await db.ExecuteAsync(Script("add_pps_arithmetic_version.sql"));
                Assert.True(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None));
            }
        }
        finally
        {
            await db.ExecuteAsync($"SET search_path TO public; DROP SCHEMA {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task ApplicationMigrationFailsBeforeDdlForFreshPartialAndCompleteSchemas()
    {
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await db.OpenAsync();
        foreach(var state in new[] { "fresh", "partial", "complete" })
        {
            var schema = "pps_admin_" + Guid.NewGuid().ToString("N");
            var app = "pps_login_" + Guid.NewGuid().ToString("N");
            try
            {
                await db.ExecuteAsync($"CREATE ROLE {app}; CREATE SCHEMA {schema}; GRANT ALL ON SCHEMA {schema} TO {app}; SET search_path TO {schema}, public; SET ROLE {app}");
                await db.ExecuteAsync(Script("createdb.sql").Split("-- BEGIN GENERATED PPS ARITHMETIC MIGRATION")[0]);
                await db.ExecuteAsync("RESET ROLE");
                if(state == "partial")
                    await db.ExecuteAsync("CREATE TABLE pps_arithmetic_transitions(poolid text PRIMARY KEY, effectivefrom timestamptz, version smallint)");
                else if(state == "complete")
                    await db.ExecuteAsync(Script("add_pps_arithmetic_version.sql"));
                await db.ExecuteAsync($"SET SESSION AUTHORIZATION {app}");
                var error = await Assert.ThrowsAsync<PostgresException>(() =>
                    db.ExecuteAsync(Script("add_pps_arithmetic_version.sql")));
                // The script's failed BEGIN needs rollback before restoring the administrator.
                await db.ExecuteAsync("ROLLBACK; RESET SESSION AUTHORIZATION");
                Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
                Assert.Equal("Run the PPS arithmetic migration as a separate database administrator", error.MessageText);
                Assert.Equal(state == "complete", await db.ExecuteScalarAsync<bool>(@"
                    SELECT EXISTS(SELECT 1 FROM pg_attribute WHERE attrelid='pps_share_credits'::regclass
                        AND attname='arithmeticversion' AND NOT attisdropped)"));
            }
            finally
            {
                await db.ExecuteAsync($"RESET SESSION AUTHORIZATION; RESET ROLE; SET search_path TO public; DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE {app}");
            }
        }
    }

    [PostgresIntegrationFact]
    public async Task StartupReconcilesConfiguredAndDurableCutoffsBeforeAdmission()
    {
        var schema = "pps_startup_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"))
            { SearchPath = schema + ", public" };
        await using var db = new NpgsqlConnection(settings.ConnectionString);
        await db.OpenAsync();
        try
        {
            await db.ExecuteAsync($"CREATE SCHEMA {schema}");
            await db.ExecuteAsync(Script("createdb.sql"));
            var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
            var pool = new PoolConfig { Id = "ltc", Enabled = true, PaymentProcessing = new()
                { Enabled = true, PayoutScheme = PayoutScheme.PPS } };
            var config = new ClusterConfig { Pools = new[] { pool }, Persistence = new() { Postgres = new() } };
            var factory = new PgConnectionFactory(settings.ConnectionString);
            Task Check() => Program.EnsureShareAccountingSchemaAsync(config, factory, repository, CancellationToken.None);
            await Check();
            pool.PaymentProcessing.PpsBinary64Activation = Cutoff;
            Assert.Equal("ltc", (await Assert.ThrowsAsync<PoolStartupException>(Check)).PoolId);
            await db.ExecuteAsync("SELECT activate_pps_binary64('LTC', @Cutoff)", new { Cutoff });
            await Assert.ThrowsAsync<PoolStartupException>(Check);
            await db.ExecuteAsync("SELECT activate_pps_binary64('ltc', @Cutoff)", new { Cutoff });
            await Check();
            pool.PaymentProcessing.PpsBinary64Activation = Cutoff.AddTicks(10);
            await Assert.ThrowsAsync<PoolStartupException>(Check);
            pool.PaymentProcessing.PpsBinary64Activation = null;
            await Assert.ThrowsAsync<PoolStartupException>(Check);
            // Another active PPS pool keeps schema preflight enabled, so this tests
            // the reconciliation filter rather than the method's early return.
            config.Pools = new[] { pool, new PoolConfig { Id = "doge", Enabled = true,
                PaymentProcessing = new() { Enabled = true, PayoutScheme = PayoutScheme.PPS } } };
            pool.PaymentProcessing.Enabled = false;
            await Check();
            pool.PaymentProcessing.Enabled = true;
            await Assert.ThrowsAsync<PoolStartupException>(Check);
            pool.Enabled = false;
            await Check();
        }
        finally
        {
            await db.ExecuteAsync($"SET search_path TO public; DROP SCHEMA {schema} CASCADE");
        }
    }

    [PostgresIntegrationFact]
    public async Task FreshAndUpgradedSchemasKeepAdministratorOwnershipAndAppReadAccess()
    {
        await using var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"));
        await db.OpenAsync();
        // These checks require the same administrator privileges as the deployment scripts.
        foreach(var upgrade in new[] { false, true })
        {
            var schema = "pps_roles_" + Guid.NewGuid().ToString("N");
            var app = "pps_app_" + Guid.NewGuid().ToString("N");
            try
            {
                await db.ExecuteAsync($"CREATE ROLE {app}; CREATE SCHEMA {schema}; GRANT ALL ON SCHEMA {schema} TO {app}; SET search_path TO {schema}, public; SET ROLE {app}");
                var fresh = Script("createdb.sql");
                await db.ExecuteAsync(upgrade ? fresh.Split("-- BEGIN GENERATED PPS ARITHMETIC MIGRATION")[0] : fresh);
                await db.ExecuteAsync("RESET ROLE");
                if(upgrade)
                    await db.ExecuteAsync(Script("add_share_accounting.sql"));
                // The standalone repair path must have the identical contract and be idempotent.
                await db.ExecuteAsync(Script("add_pps_arithmetic_version.sql"));
                await db.ExecuteAsync("SELECT activate_pps_binary64('ltc', @Cutoff)", new { Cutoff });
                await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync("TRUNCATE pps_arithmetic_transitions"));
                Assert.True(await db.ExecuteScalarAsync<bool>(
                    "SELECT relowner=(SELECT oid FROM pg_roles WHERE rolname=session_user) FROM pg_class WHERE oid='pps_arithmetic_transitions'::regclass"));
                await db.ExecuteAsync($"SET ROLE {app}");
                var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
                Assert.True(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None));
                Assert.True(await repository.HasMatchingPpsArithmeticTransitionAsync(db, "ltc", Cutoff, CancellationToken.None));
                await db.ExecuteAsync("RESET ROLE");
                foreach(var damage in new[]
                {
                    $"ALTER TABLE pps_arithmetic_transitions OWNER TO {app}",
                    $"ALTER FUNCTION guard_pps_arithmetic_credit() OWNER TO {app}",
                    $"ALTER FUNCTION guard_pps_arithmetic_transition() OWNER TO {app}",
                    $"ALTER FUNCTION activate_pps_binary64(text,timestamptz) OWNER TO {app}",
                    $"REVOKE SELECT ON pps_arithmetic_transitions FROM {app}",
                    $"GRANT INSERT ON pps_arithmetic_transitions TO {app}",
                    $"GRANT UPDATE ON pps_arithmetic_transitions TO {app}",
                    $"GRANT DELETE ON pps_arithmetic_transitions TO {app}",
                    $"GRANT TRUNCATE ON pps_arithmetic_transitions TO {app}",
                    $"GRANT UPDATE(effectivefrom) ON pps_arithmetic_transitions TO {app}",
                    $"CREATE ROLE {app}_writer; GRANT UPDATE ON pps_arithmetic_transitions TO {app}_writer; GRANT {app}_writer TO {app}",
                    $"DO $$ BEGIN EXECUTE format('GRANT %I TO {app}',session_user); END $$",
                    $"GRANT EXECUTE ON FUNCTION activate_pps_binary64(text,timestamptz) TO {app}",
                    $"GRANT EXECUTE ON FUNCTION guard_pps_arithmetic_credit() TO {app}",
                    $"GRANT EXECUTE ON FUNCTION guard_pps_arithmetic_transition() TO {app}",
                    "GRANT SELECT ON pps_arithmetic_transitions TO PUBLIC",
                    "GRANT SELECT(effectivefrom) ON pps_arithmetic_transitions TO PUBLIC",
                    "GRANT TRUNCATE ON pps_arithmetic_transitions TO PUBLIC",
                    "GRANT EXECUTE ON FUNCTION activate_pps_binary64(text,timestamptz) TO PUBLIC",
                })
                {
                    await using var tx = await db.BeginTransactionAsync();
                    await db.ExecuteAsync(damage, transaction: tx);
                    await db.ExecuteAsync($"SET LOCAL ROLE {app}", transaction: tx);
                    Assert.False(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None), damage);
                    await tx.RollbackAsync();
                }
                // Reapplying the administrator migration must remove both table
                // and column PUBLIC read drift while preserving explicit app access.
                await db.ExecuteAsync("GRANT SELECT ON pps_arithmetic_transitions TO PUBLIC; GRANT SELECT(poolid,effectivefrom,version) ON pps_arithmetic_transitions TO PUBLIC");
                await db.ExecuteAsync(Script("add_pps_arithmetic_version.sql"));
                await db.ExecuteAsync($"SET ROLE {app}");
                Assert.True(await repository.HasShareAccountingSchemaAsync(db, CancellationToken.None));
                foreach(var sql in new[]
                {
                    "TRUNCATE pps_arithmetic_transitions",
                    "DELETE FROM pps_arithmetic_transitions",
                    "UPDATE pps_arithmetic_transitions SET effectivefrom=effectivefrom",
                    "INSERT INTO pps_arithmetic_transitions VALUES ('doge', now(), 1)",
                    "SELECT activate_pps_binary64('doge', now())",
                    "ALTER TABLE pps_arithmetic_transitions DISABLE TRIGGER ALL",
                    "CREATE OR REPLACE FUNCTION guard_pps_arithmetic_credit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$",
                })
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                        (await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(sql))).SqlState);
                // A caller's temporary table must not shadow the durable boundary in the trigger.
                await db.ExecuteAsync("CREATE TEMP TABLE pps_arithmetic_transitions (poolid text, effectivefrom timestamptz, version smallint)");
                // Invoker guard must still read the admin-owned transition with app privileges.
                var id = Guid.NewGuid();
                await db.ExecuteAsync("INSERT INTO share_accounting_groups VALUES (@id, 1, @hash, @Cutoff)",
                    new { id, hash = new string('A', 64), Cutoff });
                const string credit = @"INSERT INTO pps_share_credits
                    (poolid,accountingid,address,calculatedamount,creditedamount,difficulty,networkdifficulty,rewardbasissatoshis,created,arithmeticversion)
                    VALUES ('ltc',@id,'miner',1,1,1,1,100000000,@Cutoff,@version)";
                await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsync(credit, new { id, Cutoff, version = 0 }));
                await db.ExecuteAsync(credit, new { id, Cutoff, version = 1 });
                await db.ExecuteAsync("DROP TABLE pg_temp.pps_arithmetic_transitions");
                // A real old writer never sends the version column. Its implicit
                // zero works before the boundary and is rejected at/after it.
                const string oldCredit = @"INSERT INTO pps_share_credits
                    (poolid,accountingid,address,calculatedamount,creditedamount,difficulty,networkdifficulty,rewardbasissatoshis,created)
                    VALUES ('ltc',@legacyId,'miner',1,1,1,1,100000000,@created)";
                foreach(var created in new[] { Cutoff.AddTicks(-10), Cutoff, Cutoff.AddTicks(10) })
                {
                    var legacyId = Guid.NewGuid();
                    await db.ExecuteAsync("INSERT INTO share_accounting_groups VALUES (@legacyId,1,@hash,@created)",
                        new { legacyId, created, hash = new string('B', 64) });
                    if(created < Cutoff)
                    {
                        await db.ExecuteAsync(oldCredit, new { legacyId, created });
                        Assert.Equal(0, await db.ExecuteScalarAsync<short>(
                            "SELECT arithmeticversion FROM pps_share_credits WHERE accountingid=@legacyId", new { legacyId }));
                    }
                    else
                    {
                        var rejected = await Assert.ThrowsAsync<PostgresException>(() =>
                            db.ExecuteAsync(oldCredit, new { legacyId, created }));
                        Assert.Contains("PPS arithmetic version conflicts", rejected.MessageText);
                        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
                            "SELECT count(*) FROM pps_share_credits WHERE accountingid=@legacyId", new { legacyId }));
                    }
                }

            }
            finally
            {
                await db.ExecuteAsync($"RESET ROLE; DROP TABLE IF EXISTS pg_temp.pps_arithmetic_transitions; SET search_path TO public; DROP SCHEMA IF EXISTS {schema} CASCADE; DROP ROLE {app}");
            }
        }
    }
}
