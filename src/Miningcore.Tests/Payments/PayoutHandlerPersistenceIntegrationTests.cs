using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Extensions;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Tests.Persistence.Postgres;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Miningcore.Tests.Payments;

// Share the isolated server and TLS/environment protection with other live PG tests.
[Collection(PostgresPolicyCollection.Name)]
public class PayoutHandlerPersistenceIntegrationTests(
    IsolatedPostgresServer server, ITestOutputHelper output)
{
    // Unlike COPY's uncancellable delay, this sleep must survive until cancellation
    // arrives and arms the advisory gate, including scheduling jitter on busy CI hosts.
    private const int CommitGateCancellationWindowSeconds = 15;
    // Let an unarmed gate finish its sleep and expose the payout's outcome before
    // the observer deadline wins. Keep this relationship when tuning the window.
    private const int CommitOverlapObservationTimeoutSeconds = CommitGateCancellationWindowSeconds + 8;
    // The second retry adds a failed 1s command, up to 2s cancellation, the real
    // 4s second backoff, and reconnect/observation slack. Linear slack is valid only
    // up to two retries: production backoff doubles, hence the InRange cap below.
    // Revisit both the cap and budget when changing the production retry policy.
    private const int SecondRetryObservationBudgetSeconds = 8;

    [PostgresTimeoutTheory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task ProductionRetryOverlapsUnfinishedCommitWithoutAnotherWalletSubmission(bool perRecipient, int requiredRetryCount)
    {
        await using var db = await PostgresPersistenceTestDatabase.Create(server, 1);
        var mapper = AutoMapperFactory.CreateMapper();
        var balances = new BalanceRepository(mapper);
        await db.Factory.RunTx((con, tx) => balances.AddAmountAsync(con, tx, "ltc", "miner", 12.5m, "seed"));
        // Keep the real driver's cancellation budget and the real Polly hook/backoff.
        // This test-only trigger absorbs the first cancellation, making COMMIT outlive
        // the broken client connection. An observer-owned advisory lock holds it until
        // the requested number of distinct production retries have visibly blocked
        // on the original payment-batch key. Earlier retries must time out naturally.
        await db.Observer.ExecuteAsync($"""
            CREATE SEQUENCE commit_fault;
            CREATE FUNCTION gate_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF nextval('commit_fault') = 1 THEN
                    BEGIN
                        PERFORM pg_sleep({CommitGateCancellationWindowSeconds});
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
        var handler = new PayoutPersistenceTestHandler(db.Factory, mapper); // Inherits normal OnRetry unchanged.
        var submissions = 0;
        var payment = new[] { new Balance { PoolId = "ltc", Address = "miner", Amount = 12.5m } };
        var payout = handler.Pay(payment, () =>
        {
            submissions++;
            return Task.FromResult("tx-1");
        }, perRecipient);
        // The subject is awaited in the final cleanup action: even a failed overlap
        // assertion must release the gate first, then observe the payout's outcome.
        await PostgresTestCleanup.RunAsync(
            () => WaitForRetriesBlockedByCommitAsync(db, payout, requiredRetryCount),
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
        output.WriteLine($"Observed {requiredRetryCount} distinct production retry transaction(s) blocked by the unfinished COMMIT; one wallet submission and one durable payment.");
    }

    private sealed class BlockedRetry
    {
        public int Pid { get; set; }
        public DateTime TransactionStarted { get; set; }
    }

    private static async Task WaitForRetriesBlockedByCommitAsync(
        PostgresPersistenceTestDatabase db, Task payout, int requiredRetryCount)
    {
        // The linear budget below covers only the first two exponential backoffs.
        Assert.InRange(requiredRetryCount, 1, 2);
        // Covers the current production 1s command timeout, 2s cancellation
        // budget, Polly's 2s first retry delay, reconnect and lock observation.
        // Also leaves eight seconds after the gate window for late-cancel
        // completion diagnostics. Revisit the slack if the retry policy changes.
        var observationTimeoutSeconds = CommitOverlapObservationTimeoutSeconds +
            (requiredRetryCount - 1) * SecondRetryObservationBudgetSeconds;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(observationTimeoutSeconds));
        // A retry may reuse the same pooled backend. Transaction start time
        // distinguishes attempts; polling the same blocked attempt never counts twice.
        var observedRetries = new HashSet<(int Pid, DateTime TransactionStarted)>();
        try
        {
            // Only this test's COMMIT trigger waits on an advisory lock under
            // this generated application identity; no driver SQL spelling needed.
            while(true)
            {
                var blocked = await db.Observer.QueryAsync<BlockedRetry>(new CommandDefinition("""
                    SELECT retry.pid AS Pid, retry.xact_start AS TransactionStarted
                    FROM pg_stat_activity original
                    JOIN pg_stat_activity retry ON original.pid=ANY(pg_blocking_pids(retry.pid))
                    WHERE original.application_name=@schema AND retry.application_name=@schema
                      AND original.wait_event='advisory' AND retry.wait_event_type='Lock'
                      AND retry.xact_start IS NOT NULL
                    """, new { schema = db.ApplicationName }, cancellationToken: deadline.Token)).ConfigureAwait(false);
                foreach(var retry in blocked)
                    observedRetries.Add((retry.Pid, retry.TransactionStarted));
                if(observedRetries.Count >= requiredRetryCount)
                    return;
                Assert.False(payout.IsCompleted,
                    $"Payout completed with status {payout.Status} after observing {observedRetries.Count} of {requiredRetryCount} required retry transactions blocked by the COMMIT gate. " +
                    "The payout outcome is awaited after gate release. For successful completion, check whether " +
                    "query_canceled arrived before the gate sleep ended or the original backend terminated.");
                await Task.Delay(25, deadline.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException error) when(deadline.IsCancellationRequested)
        {
            throw new XunitException(
                $"Observed {observedRetries.Count} of {requiredRetryCount} required retry transactions blocked by the COMMIT advisory gate within {observationTimeoutSeconds} seconds. " +
                "Check cancellation delivery, original backend termination, and the production retry delay/reconnect budget.", error);
        }
    }
}
