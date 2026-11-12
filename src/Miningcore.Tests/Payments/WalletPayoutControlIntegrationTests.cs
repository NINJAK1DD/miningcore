using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Dapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Rpc;
using Miningcore.Tests.Blockchain.Bitcoin;
using Miningcore.Tests.Util.Postgres;
using Miningcore.Time;
using Newtonsoft.Json.Linq;
using Npgsql;
using NSubstitute;
using Xunit;
using Node = Miningcore.Tests.Blockchain.Bitcoin.BitcoinPayoutHandlerRegtestTests.BitcoinCoreRegtestNode;

namespace Miningcore.Tests.Payments;

[Collection(BitcoinCorePayoutIntegrationCollection.Name)]
public class WalletPayoutControlIntegrationTests : TestBase
{
    [WalletControlFact]
    public async Task NonOwnerRuntime_PreflightAdmissionMetadataAndApiContract()
    {
        await using var db = await Database.Create();
        await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
        using var app = await db.Factory.OpenConnectionAsync();
        Assert.False(await app.ExecuteScalarAsync<bool>("SELECT relowner=(SELECT oid FROM pg_roles WHERE rolname=current_user) FROM pg_class WHERE oid='payout_manager_ownership'::regclass"));
        var controller = Api.Controllers.AdminApiPaymentProcessingTests.CreateController(db.Factory, true);
        var paused = (await controller.GetWalletPayoutControl(CancellationToken.None)).Value;
        Assert.True(paused.Paused);
        Assert.Equal(1, paused.ProtocolVersion);
        Assert.Equal(1, paused.Revision);
        Assert.Equal(0, paused.OutstandingBatches);
        await db.SetPaused(false);
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        var batch = WalletPayoutBatch.Create("pool-a", new[] { new Balance { Address = "private-recipient", Amount = 3m } });
        Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, batch));
        Assert.Equal(batch.PoolId, await db.Admin.ExecuteScalarAsync<string>("SELECT pool_id FROM miningcore_payout_control.batches"));
        Assert.Equal(batch.Digest, await db.Admin.ExecuteScalarAsync<string>("SELECT payout_digest FROM miningcore_payout_control.batches"));
        var active = (await controller.GetWalletPayoutControl(CancellationToken.None)).Value;
        Assert.False(active.Paused);
        Assert.Equal(2, active.Revision);
        Assert.Equal(1, active.OutstandingBatches);
        Assert.DoesNotContain("private-recipient", Newtonsoft.Json.JsonConvert.SerializeObject(active));
        await lease.CompleteWalletPayoutAsync(CancellationToken.None);
        Assert.Equal(0, (await controller.GetWalletPayoutControl(CancellationToken.None)).Value.OutstandingBatches);
    }

    [WalletControlFact]
    public async Task PreflightRejectsMissingDeniedIncompatibleAndUnsafeControlWithoutOwnership()
    {
        foreach(var failure in new[] { "missing", "denied", "version", "write", "operator" })
        {
            await using var db = await Database.Create(installControl: failure != "missing");
            if(failure == "denied") await db.RevokeAdmission();
            if(failure == "version") await db.Admin.ExecuteAsync("ALTER TABLE miningcore_payout_control.state DROP CONSTRAINT state_protocol_version_check; UPDATE miningcore_payout_control.state SET protocol_version=2");
            if(failure == "write") await db.Admin.ExecuteAsync($"GRANT pg_write_all_data TO {db.AppRole}");
            if(failure == "operator") await db.Admin.ExecuteAsync($"GRANT {db.OperatorRole} TO {db.AppRole}");
            await using var lease = new PostgresPayoutManagerLease(db.Factory);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => lease.ValidateWalletControlAsync(CancellationToken.None));
            Assert.Contains("preflight", error.Message);
            Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payout_manager_ownership WHERE owner_id IS NOT NULL"));
            Assert.True(lease.CanReleaseOwnership);
        }
    }

    [WalletControlFact]
    public async Task MigrationRejectsEffectiveWritePrivilegesAtomically()
    {
        await using var db = await Database.Create(installControl: false);
        foreach(var role in new[] { "pg_write_all_data", "pg_execute_server_program", "pg_read_server_files", "pg_write_server_files" })
        {
            await db.Admin.ExecuteAsync($"GRANT {role} TO {db.AppRole}");
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.InstallControl());
            Assert.Contains("unsafe effective", error.MessageText);
            await db.Admin.ExecuteAsync("ROLLBACK");
            Assert.False(await db.Admin.ExecuteScalarAsync<bool>("SELECT to_regnamespace('miningcore_payout_control') IS NOT NULL"));
            await db.Admin.ExecuteAsync($"REVOKE {role} FROM {db.AppRole}");
        }
        await db.InstallControl();
        await WalletPayoutControlState.PreflightAsync(db.Factory, CancellationToken.None);
    }

    [WalletControlFact]
    public async Task OperatorLockAndHistoricalLoad_DoNotStrandConclusiveBatches()
    {
        await using var db = await Database.Create();
        await db.SetPaused(false);
        // Review the active lookup with a substantial completed history, without
        // asserting hardware-dependent throughput or a brittle query plan.
        await db.Admin.ExecuteAsync("""
            INSERT INTO miningcore_payout_control.batches(batch_id,owner_id,generation,pool_id,payout_digest,completed,resolution)
            SELECT gen_random_uuid(),gen_random_uuid(),1,'historical',repeat('b',64),now(),'completed' FROM generate_series(1,10000)
            """);
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        var batch = new WalletPayoutBatch("load", new string('a',64));
        var readers = Task.Run(async () => { for(var i = 0; i < 60; i++) await db.State(); });
        for(var i = 0; i < 30; i++)
        {
            Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, batch));
            await lease.CompleteWalletPayoutAsync(CancellationToken.None);
        }
        await readers;
        Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, batch));
        await using(var tx = await db.Operator.BeginTransactionAsync())
        {
            await db.Operator.ExecuteAsync("SELECT miningcore_payout_control.set_paused(true,2,'open incident transaction')", transaction: tx);
            // Must complete while the operator still holds its row lock.
            await lease.CompleteWalletPayoutAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, batch)); // server lock_timeout
            Assert.True(lease.CanReleaseOwnership);
            await Task.Delay(TimeSpan.FromSeconds(6)); // operator outlasts client command timeout
            await tx.CommitAsync();
        }
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        await lease.DisposeAsync();
        // Identical completion is idempotent; foreign identity and reconciliation
        // cannot be reinterpreted as a completed handler batch.
        await db.Admin.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        await db.Admin.ExecuteAsync("""
            SELECT miningcore_payout_control.finish_batch(batch_id,owner_id,generation)
            FROM miningcore_payout_control.batches WHERE pool_id='load'
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.Admin.ExecuteAsync("""
            SELECT miningcore_payout_control.finish_batch(batch_id,gen_random_uuid(),generation)
            FROM miningcore_payout_control.batches WHERE pool_id='load' LIMIT 1
            """));
        await db.Admin.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        await using var replacement = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await replacement.TryAcquireAsync(CancellationToken.None));
        // First completion returns a conclusive serialization failure; the
        // second succeeds on the same guard session, without another admission.
        await db.Admin.ExecuteAsync($"""
            ALTER FUNCTION miningcore_payout_control.finish_batch(uuid,uuid,bigint) RENAME TO finish_original;
            CREATE SEQUENCE miningcore_payout_control.completion_attempt;
            CREATE FUNCTION miningcore_payout_control.finish_batch(t uuid,o uuid,g bigint) RETURNS void
            LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
            BEGIN
                IF nextval('miningcore_payout_control.completion_attempt')=1 THEN
                    RAISE EXCEPTION 'injected serialization retry' USING ERRCODE='40001';
                END IF;
                PERFORM miningcore_payout_control.finish_original(t,o,g);
            END $$;
            REVOKE ALL ON FUNCTION miningcore_payout_control.finish_batch(uuid,uuid,bigint) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION miningcore_payout_control.finish_batch(uuid,uuid,bigint) TO {db.AppRole};
            """);
        await db.SetPaused(false);
        Assert.True(await replacement.TryBeginWalletPayoutAsync(CancellationToken.None, batch));
        await replacement.CompleteWalletPayoutAsync(CancellationToken.None);
        Assert.True(replacement.CanReleaseOwnership);
        Assert.Equal(2, await db.Admin.ExecuteScalarAsync<int>("SELECT last_value FROM miningcore_payout_control.completion_attempt"));
    }

    [IsolatedPostgresFact]
    public async Task PostgreSqlRestart_PreservesStopOutstandingBatchAndOwnership()
    {
        await using var server = new IsolatedPostgresServer();
        await server.Initialize();
        var settings = new NpgsqlConnectionStringBuilder
        { Host = "127.0.0.1", Port = server.Port, Username = "miningcore", Database = "postgres", SslMode = SslMode.Disable };
        await using var db = await Database.Create(settings.ConnectionString);
        await db.SetPaused(false);
        await using(var lease = new PostgresPayoutManagerLease(db.Factory))
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64))));
            await db.SetPaused(true);
            await server.Restart("valid");
            await db.ReconnectObservers();
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => lease.CompleteWalletPayoutAsync(CancellationToken.None));
        }
        Assert.True((await db.State()).Paused);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        await using var replacement = new PostgresPayoutManagerLease(db.Factory);
        Assert.False(await replacement.TryAcquireAsync(CancellationToken.None));
        Assert.True(replacement.RequiresRecovery);
        await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(false));
    }

    [WalletControlFact]
    public async Task CompetingProcesses_PauseSurvivesCleanAndUncleanRestart()
    {
        await using var db = await Database.Create();
        await db.SetPaused(false);
        using(var first = db.StartProbe())
        {
            try
            {
                Assert.Equal("admitted", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                using var second = db.StartProbe();
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(1, second.ExitCode);
                await db.SetPaused(true);
                await first.StandardInput.WriteLineAsync("complete");
                await first.StandardInput.FlushAsync();
                await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, first.ExitCode);
            }
            finally { if(!first.HasExited) first.Kill(true); }
        }
        using(var restart = db.StartProbe())
        {
            try
            {
                Assert.Equal("paused", await restart.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
                await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, restart.ExitCode);
            }
            finally { if(!restart.HasExited) restart.Kill(true); }
        }
        await db.SetPaused(false);
        using(var crash = db.StartProbe())
        {
            try { Assert.Equal("admitted", await crash.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))); }
            finally { if(!crash.HasExited) crash.Kill(true); }
            await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        await db.SetPaused(true);
        using var blocked = db.StartProbe();
        await blocked.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(78, blocked.ExitCode);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(false));
        await db.Admin.ExecuteAsync(await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_payout_recovery_audit.sql")));
        var recovery = new PostgresPayoutOwnershipRecovery(db.AdminFactory);
        var inspected = await recovery.InspectAsync(CancellationToken.None);
        Assert.Contains("reconcile_batch before", inspected.WalletControlWarning);
        var output = new StringWriter();
        Assert.Equal(PayoutRecoveryResult.Kept, await PayoutOwnershipRecoveryAssistant.RunAsync(
            recovery, new StringReader("3\n"), output, true, CancellationToken.None));
        Assert.Contains("reconcile_batch before", output.ToString());
        await Assert.ThrowsAsync<PayoutRecoveryException>(() => recovery.ReleaseAsync(inspected, "too early", CancellationToken.None));
        await db.Operator.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var token = await db.Admin.ExecuteScalarAsync<Guid>("SELECT batch_id FROM miningcore_payout_control.batches WHERE completed IS NULL");
        await db.Operator.ExecuteAsync("SELECT miningcore_payout_control.reconcile_batch(@token,@revision,'confirmed dead; lab reconciled')",
            new { token, revision = (await db.State()).Revision });
        await db.Operator.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        inspected = await recovery.InspectAsync(CancellationToken.None);
        Assert.Null(inspected.WalletControlWarning);
        await recovery.ReleaseAsync(inspected, "reviewed crash recovery", CancellationToken.None);
        await using var recovered = new PostgresPayoutManagerLease(db.Factory);
        await recovered.ValidateWalletControlAsync(CancellationToken.None);
        Assert.True(await recovered.TryAcquireAsync(CancellationToken.None));
        Assert.False(await recovered.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a',64))));
    }

    [WalletControlFact]
    public async Task RealWallet_PauseAccountingResumeAndAmbiguousOutcome_PreserveBothDurableMarkers()
    {
        await using var db = await Database.Create();
        await using var node = await Node.StartAsync(walletBroadcast: true);
        var mapper = container.Resolve<IMapper>();
        var blocks = new BlockRepository(mapper);
        var shares = new ShareRepository(mapper);
        var balances = new BalanceRepository(mapper);
        var payments = new PaymentRepository(mapper);
        var bus = Substitute.For<IMessageBus>();
        var clock = Substitute.For<IMasterClock>();
        clock.Now.Returns(DateTime.UtcNow);
        var address = await node.GetNewAddressAsync();
        var config = new PoolConfig
        {
            Id = "emergency-stop", Template = ModuleInitializer.CoinTemplates["bitcoin"],
            Daemons = new[] { node.WalletEndpoint }, RewardRecipients = Array.Empty<RewardRecipient>(),
            PaymentProcessing = new PoolPaymentProcessingConfig
            { Enabled = true, MinimumPayment = 1, PayoutScheme = PayoutScheme.SOLO },
        };
        var cluster = new ClusterConfig { PaymentProcessing = new ClusterPaymentProcessingConfig() };
        var pool = Substitute.For<IMiningPool>();
        pool.Config.Returns(config);
        var handler = new ControlledHandler(container, db.Factory, mapper, shares, blocks, balances, payments, clock, bus);
        await handler.ConfigureAsync(cluster, config, CancellationToken.None);
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        await using var competitor = new PostgresPayoutManagerLease(db.Factory);
        Assert.False(await competitor.TryAcquireAsync(CancellationToken.None));
        var manager = new PayoutManager(container, db.Factory, blocks, shares, balances, cluster, bus, lease, new ProcessStatus());
        var hash = (await node.RootRpcAsync("getblockhash", 1)).Value<string>();
        var chainBlock = await node.RootRpcAsync("getblock", hash);
        await db.Factory.RunTx((con, tx) => blocks.InsertAsync(con, tx, new Block
        {
            PoolId = config.Id, Type = "block", Hash = hash, BlockHeight = 1,
            TransactionConfirmationData = chainBlock["tx"][0].Value<string>(),
            Status = BlockStatus.Pending, Miner = address, Created = DateTime.UtcNow.AddHours(-1),
            Effort = 1, MinerEffort = 1, NetworkDifficulty = 1,
        }));
        await manager.UpdatePoolBalancesAsync(pool, config, handler, new SOLOPaymentScheme(shares, balances), CancellationToken.None);
        Assert.Equal("confirmed", await db.Admin.ExecuteScalarAsync<string>("SELECT status FROM blocks"));
        var owed = await db.Admin.ExecuteScalarAsync<decimal>("SELECT amount FROM balances");
        Assert.True(owed > 0);
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        Assert.Equal(0, handler.Submissions);
        Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));

        await db.SetPaused(false);
        // Admission wins the race, then pause commits before the first actual wallet request.
        handler.BeforeSend = async () =>
        {
            await db.SetPaused(true);
            Assert.Equal(1, (await db.State()).OutstandingBatches);
            await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(false));
        };
        await manager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None);
        Assert.Equal(1, handler.Submissions);
        Assert.Equal(0m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(owed, await db.Admin.ExecuteScalarAsync<decimal>("SELECT amount FROM payments"));
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payment_batches"));
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        Assert.True((await db.State()).Paused);

        await lease.DisposeAsync();
        await using var restarted = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await restarted.TryAcquireAsync(CancellationToken.None));
        Assert.False(await restarted.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64))));
        await db.SetPaused(false);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, config.Id, address, 1m, "test credit"));
        var restartedManager = new PayoutManager(container, db.Factory, blocks, shares, balances, cluster, bus, restarted, new ProcessStatus());
        handler.BeforeSend = null;
        handler.LoseResponse = true;
        await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() =>
            restartedManager.PayoutPoolBalancesAsync(pool, config, handler, CancellationToken.None));
        Assert.Equal(2, handler.Submissions);
        Assert.NotNull(await node.WalletRpcAsync("gettransaction", handler.LastTransaction));
        Assert.Equal(1m, await db.Admin.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(1, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM payments"));
        Assert.False(restarted.CanReleaseOwnership);
        await db.SetPaused(true);
        await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(false));
        await restarted.DisposeAsync();
        await using var replacement = new PostgresPayoutManagerLease(db.Factory);
        Assert.False(await replacement.TryAcquireAsync(CancellationToken.None));
        Assert.True(replacement.RequiresRecovery);
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        // Reconciliation is separate, explicit, and cannot clear payout ownership.
        await db.Operator.ExecuteAsync("SELECT pg_advisory_lock(19779,5259609)");
        var token = await db.Admin.ExecuteScalarAsync<Guid>("SELECT batch_id FROM miningcore_payout_control.batches WHERE completed IS NULL");
        await db.Operator.ExecuteAsync("SELECT miningcore_payout_control.reconcile_batch(@token,@revision,'lab wallet/accounting reconciled')",
            new { token, revision = (await db.State()).Revision });
        await db.Operator.ExecuteAsync("SELECT pg_advisory_unlock(19779,5259609)");
        Assert.True(await db.Admin.ExecuteScalarAsync<bool>("SELECT owner_id IS NOT NULL FROM payout_manager_ownership"));
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        Assert.Equal("reconcile", await db.Admin.ExecuteScalarAsync<string>("SELECT action FROM miningcore_payout_control.events ORDER BY revision DESC LIMIT 1"));
    }

    [WalletControlFact]
    public async Task PauseWinsAdmissionRace_AndConcurrentOperatorsUseRevisionCompareAndSwap()
    {
        await using var db = await Database.Create();
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        await db.SetPaused(false);
        var revision = (await db.State()).Revision;
        await using var transaction = await db.Operator.BeginTransactionAsync();
        await db.Operator.ExecuteAsync("SELECT miningcore_payout_control.set_paused(true,@revision,'race')", new { revision }, transaction);
        var admission = lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(!await db.Admin.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%begin_batch%')",
            cancellationToken: timeout.Token)))
            await Task.Delay(20, timeout.Token);
        Assert.False(admission.IsCompleted);
        await transaction.CommitAsync();
        Assert.False(await admission);
        Assert.True(lease.CanReleaseOwnership);
        Assert.Equal(0, (await db.State()).OutstandingBatches);
        await Assert.ThrowsAsync<PostgresException>(() => db.Operator.ExecuteAsync(
            "SELECT miningcore_payout_control.set_paused(false,@revision,'stale resume')", new { revision }));
        Assert.True((await db.State()).Paused);
    }

    [WalletControlFact]
    public async Task DatabaseSessionLossBeforeAndAfterAdmission_FailsClosedAcrossRestart()
    {
        await using var db = await Database.Create();
        await db.SetPaused(false);
        await using(var lease = new PostgresPayoutManagerLease(db.Factory))
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            Assert.True(await lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64))));
            await db.KillGuard();
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => lease.CompleteWalletPayoutAsync(CancellationToken.None));
            Assert.False(lease.CanReleaseOwnership);
        }
        Assert.Equal(1, (await db.State()).OutstandingBatches);
        await using var restart = new PostgresPayoutManagerLease(db.Factory);
        Assert.False(await restart.TryAcquireAsync(CancellationToken.None));
        Assert.True(restart.RequiresRecovery);

        await using var second = await Database.Create();
        await second.SetPaused(false);
        await using(var lease = new PostgresPayoutManagerLease(second.Factory))
        {
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            await second.KillGuard();
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64))));
        }
        Assert.Equal(0, (await second.State()).OutstandingBatches);
        Assert.True(await second.Admin.ExecuteScalarAsync<bool>("SELECT owner_id IS NOT NULL FROM payout_manager_ownership"));
    }

    [WalletControlFact]
    public async Task PermissionsAuditRollbackAndMissingState_CannotOpenTheGate()
    {
        await using var db = await Database.Create();
        using var app = await db.Factory.OpenConnectionAsync();
        foreach(var sql in new[]
        {
            "SELECT miningcore_payout_control.set_paused(false,1,'unauthorized')",
            "SELECT miningcore_payout_control.reconcile_batch(gen_random_uuid(),1,'unauthorized')",
            "UPDATE miningcore_payout_control.state SET paused=false",
            "DELETE FROM miningcore_payout_control.batches",
            "INSERT INTO miningcore_payout_control.events(revision,action,incident) VALUES(2,'resume','forged')",
            "ALTER TABLE miningcore_payout_control.events DISABLE TRIGGER ALL",
            "DROP SCHEMA miningcore_payout_control CASCADE",
            "CREATE FUNCTION miningcore_payout_control.evil() RETURNS int LANGUAGE sql AS 'SELECT 1'",
        })
            await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(sql));
        await Assert.ThrowsAsync<PostgresException>(() => db.Operator.ExecuteAsync("UPDATE miningcore_payout_control.state SET paused=false"));
        await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("SELECT miningcore_payout_control.begin_batch(gen_random_uuid(),gen_random_uuid(),1,'lab',repeat('a',64))"));
        await db.SetPaused(false);
        Assert.Equal(db.OperatorRole, await db.Admin.ExecuteScalarAsync<string>("SELECT database_role FROM miningcore_payout_control.events WHERE revision=2"));
        await db.Admin.ExecuteAsync("""
            CREATE FUNCTION reject_control_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER reject_control_audit BEFORE INSERT ON miningcore_payout_control.events FOR EACH ROW EXECUTE FUNCTION reject_control_audit();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.SetPaused(true));
        Assert.False((await db.State()).Paused);
        Assert.Equal(2, (await db.State()).Revision);
        await db.Admin.ExecuteAsync("DROP TRIGGER reject_control_audit ON miningcore_payout_control.events");
        await using var lease = new PostgresPayoutManagerLease(db.Factory);
        Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
        await db.Admin.ExecuteAsync("DELETE FROM miningcore_payout_control.state");
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.TryBeginWalletPayoutAsync(CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64))));
        Assert.True(lease.CanReleaseOwnership);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.State());
        var recovery = new PostgresPayoutOwnershipRecovery(db.AdminFactory);
        Assert.Contains("could not be read", (await recovery.InspectAsync(CancellationToken.None)).WalletControlWarning);
        Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM miningcore_payout_control.batches"));
    }

    [WalletControlFact]
    public async Task MissingDeniedOrIncompatibleProtocol_AndCancelledAdmission_FailClosed()
    {
        foreach(var failure in new[] { "missing", "denied", "version", "cancelled" })
        {
            await using var db = await Database.Create();
            await db.SetPaused(false);
            await using var lease = new PostgresPayoutManagerLease(db.Factory);
            Assert.True(await lease.TryAcquireAsync(CancellationToken.None));
            if(failure == "missing")
                await db.Admin.ExecuteAsync("ALTER SCHEMA miningcore_payout_control RENAME TO unavailable_control");
            else if(failure == "denied")
                await db.RevokeAdmission();
            else if(failure == "version")
                await db.Admin.ExecuteAsync("ALTER TABLE miningcore_payout_control.state DROP CONSTRAINT state_protocol_version_check; UPDATE miningcore_payout_control.state SET protocol_version=2");
            else
            {
                // Hold admission at the database boundary until its command is cancelled.
                await db.Admin.ExecuteAsync("BEGIN; SELECT * FROM miningcore_payout_control.state FOR UPDATE");
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var admission = () => lease.TryBeginWalletPayoutAsync(
                failure == "cancelled" ? deadline.Token : CancellationToken.None, new WalletPayoutBatch("lab", new string('a', 64)));
            if(failure == "cancelled") await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(admission);
            else await Assert.ThrowsAsync<InvalidOperationException>(admission);
            Assert.Equal(failure != "cancelled", lease.CanReleaseOwnership);
            if(failure == "cancelled") await db.Admin.ExecuteAsync("ROLLBACK");
            if(failure != "missing")
                Assert.Equal(0, await db.Admin.ExecuteScalarAsync<int>("SELECT count(*) FROM miningcore_payout_control.batches"));
            await lease.DisposeAsync();
            Assert.Equal(failure == "cancelled", await db.Admin.ExecuteScalarAsync<bool>("SELECT owner_id IS NOT NULL FROM payout_manager_ownership"));
            await using var restart = new PostgresPayoutManagerLease(db.Factory);
            Assert.Equal(failure != "cancelled", await restart.TryAcquireAsync(CancellationToken.None));
        }
    }

    private sealed class ControlledHandler(IComponentContext context, PgConnectionFactory factory, IMapper mapper,
        ShareRepository shares, BlockRepository blocks, BalanceRepository balances, PaymentRepository payments,
        IMasterClock clock, IMessageBus bus) : BitcoinPayoutHandler(context, factory, mapper, shares, blocks, balances,
            payments, clock, bus, new ActiveBlockGracePeriodTracker())
    {
        public Func<Task> BeforeSend { get; set; }
        public bool LoseResponse { get; set; }
        public int Submissions { get; private set; }
        public string LastTransaction { get; private set; }
        protected override async Task<RpcResponse<string>> SendManyAsync(object[] args, CancellationToken ct)
        {
            if(BeforeSend != null) await BeforeSend();
            var result = await base.SendManyAsync(args, ct);
            Assert.Null(result.Error);
            LastTransaction = result.Response;
            Submissions++;
            return LoseResponse ? new RpcResponse<string>(null, new JsonRpcError(-500,"lost response",null)) : result;
        }
    }

    internal sealed class Database : IAsyncDisposable
    {
        private readonly string name = "wallet_control_" + Guid.NewGuid().ToString("N");
        public string AppRole => name + "_app";
        private string OwnerRole => name + "_owner";
        public string OperatorRole => name + "_op";
        private NpgsqlConnection root;
        public NpgsqlConnection Admin { get; private set; }
        public NpgsqlConnection Operator { get; private set; }
        public PgConnectionFactory Factory { get; private set; }
        public PgConnectionFactory AdminFactory { get; private set; }
        private string appConnectionString;
        public Process StartProbe()
        {
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"));
            start.ArgumentList.Add("wallet-control-probe");
            start.Environment["MININGCORE_TEST_POSTGRES"] = appConnectionString;
            return Process.Start(start);
        }
        public async Task ReconnectObservers()
        {
            foreach(var connection in new[] { root, Admin, Operator })
            {
                await connection.CloseAsync();
                await connection.OpenAsync();
            }
        }
        public static async Task<Database> Create(string connectionString = null, bool installControl = true)
        {
            var db = new Database();
            try
            {
                var settings = new NpgsqlConnectionStringBuilder(connectionString ?? Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES"))
                { Pooling = false, SearchPath = "public", CommandTimeout = 15 };
                db.root = new NpgsqlConnection(settings.ConnectionString);
                await db.root.OpenAsync();
                var password = Guid.NewGuid().ToString("N");
                await db.root.ExecuteAsync($"CREATE ROLE {db.AppRole} LOGIN PASSWORD '{password}'; CREATE ROLE {db.OperatorRole} LOGIN PASSWORD '{password}'; CREATE ROLE {db.OwnerRole}");
                await db.root.ExecuteAsync($"CREATE DATABASE {db.name} OWNER {db.OwnerRole}");
                settings.Database = db.name;
                db.AdminFactory = new PgConnectionFactory(settings.ConnectionString);
                db.Admin = new NpgsqlConnection(settings.ConnectionString);
                await db.Admin.OpenAsync();
                var schema = await File.ReadAllTextAsync(PostgresTestScripts.PathFor("createdb.sql"));
                await db.Admin.ExecuteAsync(schema.Replace("SET ROLE miningcore;", $"SET ROLE {db.OwnerRole};"));
                await db.Admin.ExecuteAsync("RESET ROLE");
                await db.Admin.ExecuteAsync($"GRANT USAGE ON SCHEMA public TO {db.AppRole}; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {db.AppRole}; GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO {db.AppRole}");
                if(installControl) await db.InstallControl();
                settings.Username = db.AppRole; settings.Password = password;
                db.appConnectionString = settings.ConnectionString;
                db.Factory = new PgConnectionFactory(settings.ConnectionString);
                settings.Username = db.OperatorRole;
                db.Operator = new NpgsqlConnection(settings.ConnectionString);
                await db.Operator.OpenAsync();
                return db;
            }
            catch { await db.DisposeAsync(); throw; }
        }
        public Task<WalletPayoutControlState> State() => WalletPayoutControlState.ReadAsync(Factory, CancellationToken.None);
        public async Task InstallControl()
        {
            await Admin.ExecuteAsync("SELECT set_config('miningcore.wallet_control_app_role',@role,false)", new { role = AppRole });
            await Admin.ExecuteAsync(await File.ReadAllTextAsync(PostgresTestScripts.PathFor("add_wallet_payout_control.sql")));
            await Admin.ExecuteAsync($"""
                GRANT USAGE ON SCHEMA miningcore_payout_control TO {OperatorRole};
                GRANT SELECT ON ALL TABLES IN SCHEMA miningcore_payout_control TO {OperatorRole};
                GRANT EXECUTE ON FUNCTION miningcore_payout_control.set_paused(boolean,bigint,text),
                    miningcore_payout_control.reconcile_batch(uuid,bigint,text) TO {OperatorRole};
                """);
        }
        public async Task SetPaused(bool paused) => await Operator.ExecuteAsync(
            "SELECT miningcore_payout_control.set_paused(@paused,@revision,'integration incident')",
            new { paused, revision = (await State()).Revision });
        public Task KillGuard() => Admin.ExecuteAsync("""
            SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype='advisory'
            AND database=(SELECT oid FROM pg_database WHERE datname=current_database())
            AND classid=19779 AND objid=5259609 AND granted
            """);
        public Task RevokeAdmission() => Admin.ExecuteAsync($"REVOKE EXECUTE ON FUNCTION miningcore_payout_control.begin_batch(uuid,uuid,bigint,text,text) FROM {AppRole}");
        public async ValueTask DisposeAsync()
        {
            if(Operator != null) await Operator.DisposeAsync();
            if(Admin != null) await Admin.DisposeAsync();
            if(root != null)
            {
                await root.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
                await root.ExecuteAsync($"DROP ROLE IF EXISTS {AppRole}; DROP ROLE IF EXISTS {OperatorRole}; DROP ROLE IF EXISTS {OwnerRole}");
                await root.DisposeAsync();
            }
        }
    }
}

public sealed class WalletControlFactAttribute : FactAttribute
{
    public WalletControlFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")) ||
           string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_BITCOIND")))
            Skip = "Requires the isolated PostgreSQL and Bitcoin Core test lab";
    }
}
