using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Tests.Util;
using Newtonsoft.Json;
using Npgsql;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Persistence.Postgres;

// Reuse the isolated, loopback-only server lifecycle (also installed by CI).
// The collection owns one server; each test owns its schema and connection pool.
[Collection(PostgresPolicyCollection.Name)]
public class PostgresCommandTimeoutIntegrationTests(
    IsolatedPostgresServer server, ITestOutputHelper output)
{
    [PostgresLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentTransactionRollsBackAndReplaysExactlyOnce(bool cancel)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, cancel ? 300 : 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var payments = new PaymentRepository(mapper);
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));

        async Task Persist(bool interrupt, CancellationToken ct) => await db.Factory.RunTx(async (con, tx) =>
        {
            if(!await payments.TryBeginPaymentBatchAsync(con, tx, "ltc", "tx-1", DateTime.UtcNow))
                return;
            await payments.InsertAsync(con, tx, new Payment
            {
                PoolId = "ltc", Coin = "LTC", Address = "miner", Amount = 12.5m,
                TransactionConfirmationData = "tx-1", Created = DateTime.UtcNow,
            });
            await balances.AddAmountAsync(con, tx, "ltc", "miner", -12.5m, "payment");
            if(interrupt)
                await con.ExecuteAsync(new CommandDefinition("SELECT pg_sleep(5)", transaction: tx, cancellationToken: ct));
        }, ct: ct);

        await Interrupt(db, cancel, ct => Persist(true, ct));
        Assert.Equal(12.5m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(1, await db.Count("balance_changes"));
        Assert.Equal(0, await db.Count("payments"));
        Assert.Equal(0, await db.Count("payment_batches"));
        await db.AssertUsable();

        await Persist(false, CancellationToken.None);
        await Persist(false, CancellationToken.None);
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(2, await db.Count("balance_changes"));
        Assert.Equal(1, await db.Count("payments"));
        Assert.Equal(1, await db.Count("payment_batches"));
    }

    [PostgresLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PpsTimeoutOrCancellationRollsBackReceiptCreditAndRemainder(bool cancel)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, cancel ? 300 : 1);
        var repository = new ShareRepository(AutoMapperFactory.CreateMapper());
        var id = Guid.NewGuid();
        var created = DateTime.UtcNow;
        var batch = new ShareAccountingBatch
        {
            AccountingId = id, PayloadHash = new string('A', 64), Created = created,
            Shares = new[] { new Share
            {
                PoolId = "ltc", Miner = "miner", BlockHeight = 100, Difficulty = 1,
                NetworkDifficulty = 100, IpAddress = "127.0.0.1", Created = created,
                AccountingId = id, AccountingRole = 1, RewardBasisSatoshis = 5_000_000_000,
            } },
            PpsCredits = new[] { new PpsShareCredit
            {
                PoolId = "ltc", Address = "miner", AccountingId = id,
                CalculatedAmount = 0.2500000000005m, Difficulty = 1,
                NetworkDifficulty = 100, RewardBasisSatoshis = 5_000_000_000, Created = created,
            } },
        };
        // This blocks inside the real financial SQL after the receipt/share insertion
        // and remainder seed, rather than substituting a repository exception.
        await DelayWrites(db, "balances", "INSERT");
        Task<ShareAccountingInsertResult> Persist(CancellationToken ct) => db.Factory.RunTx(
            (con, tx) => repository.InsertAccountingBatchAsync(con, tx, batch, ct),
            ct: ct, classifyCommitOutcome: true);

        await Interrupt(db, cancel, ct => Persist(ct));
        foreach(var table in new[] { "shares", "share_accounting_groups", "pps_share_credits",
                    "pps_credit_remainders", "balance_changes", "balances" })
            Assert.Equal(0, await db.Count(table));
        await db.AssertUsable();

        await RemoveDelay(db, "balances");
        Assert.Equal(ShareAccountingInsertResult.Inserted, await Persist(CancellationToken.None));
        Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await Persist(CancellationToken.None));
        foreach(var table in new[] { "shares", "share_accounting_groups", "pps_share_credits", "balance_changes" })
            Assert.Equal(1, await db.Count(table));
        Assert.Equal(0.25m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(0.0000000000005m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM pps_credit_remainders"));
    }

    [PostgresLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryJournalSurvivesCopyTimeoutAndCommitArchiveInterruption(bool interruptArchive)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
        var directory = Path.Combine(Path.GetTempPath(), "miningcore-timeout-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filename = Path.Combine(directory, "shares.txt");
        var mapper = AutoMapperFactory.CreateMapper();
        var config = new ClusterConfig
        {
            Pools = new[] { new PoolConfig { Id = "ltc" } }, ShareRecoveryFile = filename,
            ShareRecoveryStateDirectory = Path.Combine(directory, "state"),
        };
        using var recorder = new ShareRecorder(db.Factory, mapper, new JsonSerializerSettings(),
            new ShareRepository(mapper), new BlockRepository(mapper), config, Substitute.For<IMessageBus>());
        try
        {
            await recorder.WriteRecoveryJournalAsync(new[] { new Miningcore.Blockchain.Share
            {
                PoolId = "ltc", Miner = "miner", IpAddress = "127.0.0.1", Difficulty = 1,
                NetworkDifficulty = 100, BlockHeight = 100, Created = DateTime.UtcNow,
            } });
            var original = await File.ReadAllBytesAsync(filename);
            await DelayWrites(db, "shares", "INSERT");
            // Npgsql 9 COPY completion disables the separate PostgreSQL cancellation
            // request and breaks the connection on timeout. PostgreSQL can finish the
            // current work before observing that disconnect. Interrupt waits for the
            // server transaction to end before inspecting rollback. No COMMIT was sent.
            await Interrupt(db, false, _ => recorder.RecoverSharesAsync(filename));
            Assert.Equal(original, await File.ReadAllBytesAsync(filename));
            Assert.True(File.Exists(recorder.RecoveryImportStateFilename));
            Assert.True(File.Exists(recorder.RecoveryTerminalStateFilename));
            Assert.Equal(0, await db.Count("shares"));
            Assert.Equal(0, await db.Count("share_recovery_imports"));
            await db.AssertUsable();
            await RemoveDelay(db, "shares");

            if(interruptArchive)
            {
                recorder.RecoveryArchiveMove = (_, _) => throw new IOException("test interruption after commit");
                await Assert.ThrowsAsync<IOException>(() => recorder.RecoverSharesAsync(filename));
                Assert.Equal(1, await db.Count("shares"));
                Assert.Equal(1, await db.Count("share_recovery_imports"));
                Assert.Equal(original, await File.ReadAllBytesAsync(filename));
                recorder.RecoveryArchiveMove = File.Move;
            }
            var archive = await recorder.RecoverSharesAsync(filename);
            Assert.True(File.Exists(archive));
            Assert.False(File.Exists(filename));
            Assert.False(File.Exists(recorder.RecoveryImportStateFilename));
            Assert.False(File.Exists(recorder.RecoveryTerminalStateFilename));
            Assert.Equal(1, await db.Count("shares"));
            Assert.Equal(1, await db.Count("share_recovery_imports"));
        }
        finally
        {
            // Only this test's generated absolute temporary directory is removed.
            Directory.Delete(directory, true);
        }
    }

    [PostgresLiveFact]
    public async Task PayoutHandlerRetriesDatabasePersistenceWithoutDoubleDebit()
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        await DelayWrites(db, "balances", "UPDATE");
        var retries = 0;
        var handler = new ObservedRetryHandler(db.Factory, mapper, error =>
        {
            retries++;
            Assert.IsType<NpgsqlException>(error);
            // A broken client connection need not mean the server has released locks yet.
            db.WaitForTransactionEndAsync().GetAwaiter().GetResult();
            Assert.Equal(12.5m, db.Observer.ExecuteScalar<decimal>("SELECT amount FROM balances"));
            Assert.Equal(0, db.Observer.ExecuteScalar<int>("SELECT count(*) FROM payments"));
            Assert.Equal(0, db.Observer.ExecuteScalar<int>("SELECT count(*) FROM payment_batches"));
            Assert.Equal(1, db.Observer.ExecuteScalar<int>("SELECT count(*) FROM balance_changes"));
            db.Observer.Execute("DROP TRIGGER delay_write ON balances");
        });
        var payment = new[] { new Balance { PoolId = "ltc", Address = "miner", Amount = 12.5m } };
        await handler.Persist(payment).WaitAsync(TimeSpan.FromSeconds(40));
        await handler.Persist(payment);
        Assert.Equal(1, retries);
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(1, await db.Count("payments"));
        Assert.Equal(1, await db.Count("payment_batches"));
        Assert.Equal(2, await db.Count("balance_changes"));
        await db.AssertUsable();
    }

    private async Task Interrupt(PostgresPersistenceTestDatabase db, bool cancel, Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        var observed = cancel ? CancelWhenSleeping(db, cancellation) : Task.CompletedTask;
        var error = await Record.ExceptionAsync(() => action(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20)));
        await observed;
        if(cancel)
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        else
        {
            var driverError = Assert.IsType<NpgsqlException>(error);
            Assert.IsType<TimeoutException>(driverError.InnerException);
        }
        // Observe server completion before callers inspect durable state or issue DDL.
        await db.WaitForTransactionEndAsync();
        output.WriteLine($"PostgreSQL {await db.Observer.ExecuteScalarAsync<string>("SHOW server_version")}; " +
            $"Npgsql {typeof(NpgsqlConnection).Assembly.GetName().Version}; " +
            $"{(cancel ? "caller cancellation" : "command timeout")}: {error.GetType().Name}");
    }

    [PostgresLiveFact]
    public async Task CommitTimeoutPreservesUncertaintyUntilDatabaseReconciliation()
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var payments = new PaymentRepository(mapper);
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        // The delayed work runs at COMMIT, after the transaction delegate returned.
        await db.Observer.ExecuteAsync("""
            CREATE FUNCTION delay_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(5); RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER delay_commit AFTER INSERT ON payment_batches
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION delay_commit();
            """);
        Task Persist() => db.Factory.RunTx(async (con, tx) =>
        {
            if(await payments.TryBeginPaymentBatchAsync(con, tx, "ltc", "commit-tx", DateTime.UtcNow))
                await balances.AddAmountAsync(con, tx, "ltc", "miner", -12.5m, "payment");
        }, classifyCommitOutcome: true);

        var error = await Assert.ThrowsAsync<TransactionCommitOutcomeUncertainException>(
            () => Persist().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<TimeoutException>(Assert.IsType<NpgsqlException>(error.InnerException).InnerException);
        await db.WaitForTransactionEndAsync();
        // Reconcile durable evidence; the client exception alone cannot select the outcome.
        var committed = await db.Count("payment_batches");
        Assert.InRange(committed, 0, 1);
        Assert.Equal(committed == 1 ? 0m : 12.5m,
            await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(committed + 1, await db.Count("balance_changes"));
        await db.Observer.ExecuteAsync("DROP TRIGGER delay_commit ON payment_batches");
        await Persist();
        await Persist();
        Assert.Equal(1, await db.Count("payment_batches"));
        Assert.Equal(2, await db.Count("balance_changes"));
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        await db.AssertUsable();
        output.WriteLine($"COMMIT timeout remained uncertain; database reconciliation found {committed} committed batch(es).");
    }

    [PostgresLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayoutHandlerCommitTimeoutReconcilesWithoutAnotherWalletSubmission(bool perRecipient)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        await db.Observer.ExecuteAsync("""
            CREATE FUNCTION delay_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(5); RETURN NEW; END $$;
            CREATE CONSTRAINT TRIGGER delay_commit AFTER INSERT ON payment_batches
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION delay_commit();
            """);
        var retries = 0;
        var submissions = 0;
        var handler = new ObservedRetryHandler(db.Factory, mapper, error =>
        {
            retries++;
            Assert.IsType<TimeoutException>(Assert.IsType<NpgsqlException>(error).InnerException);
            db.WaitForTransactionEndAsync().GetAwaiter().GetResult();
            var committed = db.Observer.ExecuteScalar<int>("SELECT count(*) FROM payment_batches");
            Assert.InRange(committed, 0, 1);
            Assert.Equal(committed, db.Observer.ExecuteScalar<int>("SELECT count(*) FROM payments"));
            Assert.Equal(committed + 1, db.Observer.ExecuteScalar<int>("SELECT count(*) FROM balance_changes"));
            Assert.Equal(committed == 1 ? 0m : 12.5m,
                db.Observer.ExecuteScalar<decimal>("SELECT amount FROM balances"));
            // Remove the fault only after reconciliation; the production retry policy
            // and default RunTx commit handling remain unchanged in both overloads.
            db.Observer.Execute("DROP TRIGGER delay_commit ON payment_batches");
        });
        var payment = new[] { new Balance { PoolId = "ltc", Address = "miner", Amount = 12.5m } };
        await handler.Pay(payment, () =>
        {
            submissions++;
            return Task.FromResult("tx-1");
        }, perRecipient).WaitAsync(TimeSpan.FromSeconds(40));
        await handler.Persist(payment, perRecipient);

        Assert.Equal(1, submissions);
        Assert.Equal(1, retries);
        Assert.Equal(1, await db.Count("payment_batches"));
        Assert.Equal(1, await db.Count("payments"));
        Assert.Equal(2, await db.Count("balance_changes"));
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(12.5m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM payments"));
        await db.AssertUsable();
    }

    [PostgresLiveFact]
    public async Task DatabaseSetupRestoresAuthenticationAndTlsWithoutRedundantRestarts()
    {
        await PostgresTestCleanup.RunAsync(async () =>
        {
            await server.PasswordAuthentication(true);
            await server.UseCertificate("off");
            await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
            await db.AssertUsable();
            Assert.True(await db.Observer.ExecuteScalarAsync<bool>(
                "SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()"));
            Assert.Equal("trust", await db.Observer.ExecuteScalarAsync<string>(
                "SELECT auth_method FROM pg_hba_file_rules WHERE type='host'"));
            // Re-selecting the current state must leave this open connection alive.
            await server.PasswordAuthentication(false);
            await server.UseCertificate("valid");
            Assert.Equal(1, await db.Observer.ExecuteScalarAsync<int>("SELECT 1"));
        }, () => server.PasswordAuthentication(false));
    }

    // Fault injection belongs to the timeout suite; the shared database fixture
    // owns only setup, observation, pool checks and cleanup.
    private static Task DelayWrites(PostgresPersistenceTestDatabase db, string table, string operation) => db.Observer.ExecuteAsync($"""
        CREATE OR REPLACE FUNCTION delay_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN PERFORM pg_sleep(5); RETURN NEW; END $$;
        CREATE TRIGGER delay_write AFTER {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION delay_write();
        """);

    private static Task RemoveDelay(PostgresPersistenceTestDatabase db, string table) =>
        db.Observer.ExecuteAsync($"DROP TRIGGER delay_write ON {table}");

    private static async Task CancelWhenSleeping(PostgresPersistenceTestDatabase db, CancellationTokenSource cancellation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!await db.Observer.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@schema AND wait_event='PgSleep')",
            new { schema = db.SchemaAndApplicationName }, cancellationToken: deadline.Token)))
            await Task.Delay(25, deadline.Token);
        cancellation.Cancel();
    }

    private sealed class ObservedRetryHandler(IConnectionFactory factory, IMapper mapper, Action<Exception> retry)
        : PayoutPersistenceTestHandler(factory, mapper)
    {
        protected override void OnRetry(Exception ex, TimeSpan timeSpan, int attempt, object context) => retry(ex);
    }
}
