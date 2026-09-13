using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using Npgsql;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Persistence.Postgres;

public sealed class PostgresTimeoutTheoryAttribute : TheoryAttribute
{
    internal const string SkipReason = "Set MININGCORE_TEST_POSTGRES_BIN to run isolated PostgreSQL timeout/rollback tests";
    public PostgresTimeoutTheoryAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = SkipReason;
    }
}

public sealed class PostgresTimeoutFactAttribute : FactAttribute
{
    public PostgresTimeoutFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = PostgresTimeoutTheoryAttribute.SkipReason;
    }
}

// Reuse the isolated, loopback-only server lifecycle (also installed by CI).
// The collection owns one server; each test owns its schema and connection pool.
[Collection(PostgresPolicyCollection.Name)]
public class PostgresCommandTimeoutIntegrationTests(
    IsolatedPostgresServer server, ITestOutputHelper output)
{
    [PostgresTimeoutTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentTransactionRollsBackAndReplaysExactlyOnce(bool cancel)
    {
        await using var db = await Database.Create(server, cancel ? 300 : 1);
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

    [PostgresTimeoutTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PpsTimeoutOrCancellationRollsBackReceiptCreditAndRemainder(bool cancel)
    {
        await using var db = await Database.Create(server, cancel ? 300 : 1);
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
        await db.DelayWrites("balances", "INSERT");
        Task<ShareAccountingInsertResult> Persist(CancellationToken ct) => db.Factory.RunTx(
            (con, tx) => repository.InsertAccountingBatchAsync(con, tx, batch, ct),
            ct: ct, classifyCommitOutcome: true);

        await Interrupt(db, cancel, ct => Persist(ct));
        foreach(var table in new[] { "shares", "share_accounting_groups", "pps_share_credits",
                    "pps_credit_remainders", "balance_changes", "balances" })
            Assert.Equal(0, await db.Count(table));
        await db.AssertUsable();

        await db.RemoveDelay("balances");
        Assert.Equal(ShareAccountingInsertResult.Inserted, await Persist(CancellationToken.None));
        Assert.Equal(ShareAccountingInsertResult.AlreadyCommitted, await Persist(CancellationToken.None));
        foreach(var table in new[] { "shares", "share_accounting_groups", "pps_share_credits", "balance_changes" })
            Assert.Equal(1, await db.Count(table));
        Assert.Equal(0.25m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(0.0000000000005m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM pps_credit_remainders"));
    }

    [PostgresTimeoutTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryJournalSurvivesCopyTimeoutAndCommitArchiveInterruption(bool interruptArchive)
    {
        await using var db = await Database.Create(server, 1);
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
            await db.DelayWrites("shares", "INSERT");
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
            await db.RemoveDelay("shares");

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

    [PostgresTimeoutFact]
    public async Task PayoutHandlerRetriesDatabasePersistenceWithoutDoubleDebit()
    {
        await using var db = await Database.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        await db.DelayWrites("balances", "UPDATE");
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

    private async Task Interrupt(Database db, bool cancel, Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource();
        var observed = cancel ? db.CancelWhenSleeping(cancellation) : Task.CompletedTask;
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

    [PostgresTimeoutFact]
    public async Task CommitTimeoutPreservesUncertaintyUntilDatabaseReconciliation()
    {
        await using var db = await Database.Create(server, 1);
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

    [PostgresTimeoutTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayoutHandlerCommitTimeoutReconcilesWithoutAnotherWalletSubmission(bool perRecipient)
    {
        await using var db = await Database.Create(server, 1);
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

    [PostgresTimeoutTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionRetryOverlapsUnfinishedCommitWithoutAnotherWalletSubmission(bool perRecipient)
    {
        await using var db = await Database.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        // Keep the real driver's cancellation budget and the real Polly hook/backoff.
        // This test-only trigger absorbs the first cancellation, making COMMIT outlive
        // the broken client connection. An observer-owned advisory lock holds it until
        // a production retry is visibly blocked on the original payment-batch key.
        await db.Observer.ExecuteAsync("""
            CREATE SEQUENCE commit_fault;
            CREATE FUNCTION gate_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('commit_fault') = 1 THEN
                    BEGIN
                        PERFORM pg_sleep(5);
                    EXCEPTION WHEN query_canceled THEN
                        PERFORM pg_advisory_xact_lock(TG_ARGV[0]::bigint);
                    END;
                END IF;
                RETURN NEW;
            END $$;
            """);
        var gate = Random.Shared.NextInt64(1, long.MaxValue);
        // gate is generated numeric test data, never an operator-supplied SQL fragment.
        await db.Observer.ExecuteAsync($"""
            CREATE CONSTRAINT TRIGGER gate_commit AFTER INSERT ON payment_batches
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION gate_commit('{gate}');
            """);
        await db.Observer.ExecuteAsync("SELECT pg_advisory_lock(@gate)", new { gate });
        var handler = new PersistenceHandler(db.Factory, mapper); // Inherits normal OnRetry unchanged.
        var submissions = 0;
        var payment = new[] { new Balance { PoolId = "ltc", Address = "miner", Amount = 12.5m } };
        var payout = handler.Pay(payment, () =>
        {
            submissions++;
            return Task.FromResult("tx-1");
        }, perRecipient);
        await PostgresTestCleanup.RunAsync(
            () => db.WaitForRetryBlockedByCommitAsync(),
            async () => { await db.Observer.ExecuteAsync("SELECT pg_advisory_unlock(@gate)", new { gate }); },
            () => payout.WaitAsync(TimeSpan.FromSeconds(40)));
        await db.WaitForTransactionEndAsync();
        await handler.Persist(payment, perRecipient);
        Assert.Equal(1, submissions);
        Assert.Equal(1, await db.Count("payment_batches"));
        Assert.Equal(1, await db.Count("payments"));
        Assert.Equal(2, await db.Count("balance_changes"));
        Assert.Equal(0m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM balances"));
        Assert.Equal(12.5m, await db.Observer.ExecuteScalarAsync<decimal>("SELECT amount FROM payments"));
        await db.AssertUsable();
        output.WriteLine("Observed a production retry blocked by the unfinished COMMIT; one wallet submission and one durable payment.");
    }

    [PostgresTimeoutFact]
    public async Task DatabaseSetupRestoresAuthenticationAndTlsWithoutRedundantRestarts()
    {
        await PostgresTestCleanup.RunAsync(async () =>
        {
            await server.PasswordAuthentication(true);
            await server.UseCertificate("off");
            await using var db = await Database.Create(server, 1);
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

    private sealed class ObservedRetryHandler(IConnectionFactory factory, IMapper mapper, Action<Exception> retry)
        : PersistenceHandler(factory, mapper)
    {
        protected override void OnRetry(Exception ex, TimeSpan timeSpan, int attempt, object context) => retry(ex);
    }

    private class PersistenceHandler(IConnectionFactory factory, IMapper mapper)
        : PayoutHandlerBase(factory, mapper, new ShareRepository(mapper), new BlockRepository(mapper),
            new BalanceRepository(mapper), new PaymentRepository(mapper), new StandardClock(), Substitute.For<IMessageBus>())
    {
        protected override string LogCategory => "timeout-test";
        public Task Persist(Balance[] balances, bool perRecipient = false)
        {
            logger = LogManager.GetCurrentClassLogger();
            poolConfig = new PoolConfig { Id = "ltc", Template = new BitcoinTemplate { Symbol = "LTC" }, RewardRecipients = Array.Empty<RewardRecipient>() };
            return perRecipient
                // Reconstruct the POCOs so reconciliation cannot rely on reference identity.
                ? PersistPaymentsAsync(balances.ToDictionary(balance => new Balance
                    { PoolId = balance.PoolId, Address = balance.Address, Amount = balance.Amount }, _ => "tx-1"))
                : PersistPaymentsAsync(balances, "tx-1");
        }

        public Task Pay(Balance[] balances, Func<Task<string>> submitWallet, bool perRecipient) =>
            TrackPayoutAsync(balances, async () =>
            {
                TrackPayoutSubmission(CancellationToken.None, balances);
                Assert.Equal("tx-1", await submitWallet());
                await Persist(balances, perRecipient);
            });
    }

    private sealed class Database : IAsyncDisposable
    {
        private readonly string schema = "timeout_" + Guid.NewGuid().ToString("N");
        private NpgsqlConnectionStringBuilder settings;
        public NpgsqlConnection Observer { get; private set; }
        public PgConnectionFactory Factory { get; private set; }

        public static async Task<Database> Create(IsolatedPostgresServer server, int timeout)
        {
            var db = new Database();
            try
            {
                // TLS tests in the same collection may leave the shared server in an
                // intentional non-verifying mode. Select the required state explicitly.
                await server.UseCertificate("valid");
                await server.PasswordAuthentication(false);
                var config = PostgresConnectionPolicyTests.Config();
                config.Host = "127.0.0.1";
                config.Port = server.Port;
                config.Database = "postgres";
                config.SslMode = PostgresSslMode.VerifyCA;
                config.TlsRootCert = server.Root;
                config.CommandTimeout = timeout;
                db.settings = PostgresConnectionPolicy.Build(config, null, out var diagnostic);
                Assert.Equal(timeout, diagnostic.CommandTimeout);
                db.settings.SearchPath = db.schema;
                db.settings.ApplicationName = db.schema;
                db.settings.MaxPoolSize = 1;
                db.Factory = new PgConnectionFactory(db.settings.ConnectionString);
                var control = new NpgsqlConnectionStringBuilder(db.settings.ConnectionString)
                { Pooling = false, CommandTimeout = 10, ApplicationName = db.schema + "_observer" };
                db.Observer = new NpgsqlConnection(control.ConnectionString);
                await db.Observer.OpenAsync();
                await db.Observer.ExecuteAsync($"CREATE SCHEMA {db.schema}");
                var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Miningcore/Persistence/Postgres/Scripts/createdb.sql"));
                await db.Observer.ExecuteAsync(await File.ReadAllTextAsync(script));
                return db;
            }
            catch { await db.DisposeAsync(); throw; }
        }

        public Task<int> Count(string table) => Observer.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table}");

        public Task DelayWrites(string table, string operation) => Observer.ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION delay_write() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_sleep(5); RETURN NEW; END $$;
            CREATE TRIGGER delay_write AFTER {operation} ON {table} FOR EACH ROW EXECUTE FUNCTION delay_write();
            """);

        public Task RemoveDelay(string table) => Observer.ExecuteAsync($"DROP TRIGGER delay_write ON {table}");

        public async Task CancelWhenSleeping(CancellationTokenSource cancellation)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!await Observer.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@schema AND wait_event='PgSleep')",
                new { schema }, cancellationToken: deadline.Token)))
                await Task.Delay(25, deadline.Token);
            cancellation.Cancel();
        }

        public async Task AssertUsable()
        {
            await WaitForTransactionEndAsync();
            // A one-slot pool must be usable after failure, without leaked transactions/locks.
            Assert.Equal(1, await Factory.Run(con => con.ExecuteScalarAsync<int>("SELECT 1")));
            Assert.Equal(0, await Observer.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name=@schema AND xact_start IS NOT NULL", new { schema }));
        }

        public async Task WaitForTransactionEndAsync()
        {
            // OnRetry is synchronous. ConfigureAwait(false) on every await here is
            // required to avoid deadlocking its caller under xUnit's sync context.
            // Move those callers to await if the production hook becomes asynchronous.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(await Observer.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@schema AND xact_start IS NOT NULL)",
                new { schema }, cancellationToken: deadline.Token)).ConfigureAwait(false))
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
        }

        public async Task WaitForRetryBlockedByCommitAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(!await Observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                    SELECT 1 FROM pg_stat_activity original
                    JOIN pg_stat_activity retry ON original.pid=ANY(pg_blocking_pids(retry.pid))
                    WHERE original.application_name=@schema AND retry.application_name=@schema
                      AND original.query ILIKE 'COMMIT%' AND original.wait_event='advisory'
                      AND retry.wait_event_type='Lock')
                """, new { schema }, cancellationToken: deadline.Token)))
                await Task.Delay(25, deadline.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if(Observer != null)
            {
                try
                {
                    if(Observer.State == ConnectionState.Open)
                    {
                        // Cleanup cannot mask a failed assertion with a DROP blocked by
                        // the deliberately sleeping test backend. Only this generated
                        // test identity on this private server can be terminated here.
                        await Observer.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name=@schema", new { schema });
                        await Observer.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                    }
                }
                finally { await Observer.DisposeAsync(); }
            }
            if(settings != null)
            {
                using var pool = new NpgsqlConnection(settings.ConnectionString);
                NpgsqlConnection.ClearPool(pool);
            }
        }
    }
}
