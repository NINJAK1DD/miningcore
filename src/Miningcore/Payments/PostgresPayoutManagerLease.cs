using System.Data;
using Dapper;
using Miningcore.Persistence;
using NLog;
using Npgsql;

namespace Miningcore.Payments;

/// <summary>
/// Holds both a PostgreSQL session advisory lock and a durable ownership record for the
/// complete payout-manager lifetime. The durable record is cleared only after a clean stop.
/// If the guard session or process is lost, replacement startup remains blocked until an
/// operator has confirmed that the previous process is dead and clears the stale record.
/// This deliberately favours financial safety over automatic failover.
/// </summary>
public sealed class PostgresPayoutManagerLease : IPayoutManagerLease
{
    public PostgresPayoutManagerLease(IConnectionFactory cf)
    {
        this.cf = cf;
    }

    // "MC" and "PAY" expressed as stable signed 32-bit advisory-lock keys.
    internal const int LockNamespace = 0x4D43;
    internal const int LockKey = 0x504159;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IConnectionFactory cf;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object financialStateGate = new();
    private readonly Guid ownerId = Guid.NewGuid();
    private IDbConnection connection;
    private long generation;
    private bool acquired;
    private int disposed;
    private int activeFinancialOperations;
    private bool financialOutcomeUncertain;

    internal bool CanReleaseOwnership
    {
        get
        {
            lock(financialStateGate)
                return activeFinancialOperations == 0 && !financialOutcomeUncertain;
        }
    }

    public void BeginFinancialOperation()
    {
        lock(financialStateGate)
        {
            if(disposed != 0)
                throw new ObjectDisposedException(nameof(PostgresPayoutManagerLease));

            if(financialOutcomeUncertain)
                throw new InvalidOperationException(
                    "A previous wallet submission has an unknown outcome and requires reconciliation");

            activeFinancialOperations++;
        }
    }

    public void CompleteFinancialOperation()
    {
        lock(financialStateGate)
        {
            if(activeFinancialOperations <= 0)
                throw new InvalidOperationException("No payout financial operation is active");

            activeFinancialOperations--;
        }
    }

    public void MarkFinancialOutcomeUncertain()
    {
        lock(financialStateGate)
        {
            if(activeFinancialOperations > 0)
                activeFinancialOperations--;

            financialOutcomeUncertain = true;
        }
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct)
    {
        if(Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(PostgresPayoutManagerLease));
        await gate.WaitAsync(ct);

        try
        {
            if(acquired)
                return true;

            var candidate = await cf.OpenConnectionAsync();

            try
            {
                var command = new CommandDefinition(
                    "SELECT pg_try_advisory_lock(@lockNamespace, @lockKey)",
                    new { lockNamespace = LockNamespace, lockKey = LockKey },
                    cancellationToken: ct);
                var locked = await candidate.ExecuteScalarAsync<bool>(command);

                if(!locked)
                {
                    candidate.Dispose();
                    return false;
                }

                using(var tx = candidate.BeginTransaction())
                {
                    const string verifyPaymentBatchSchema = @"SELECT EXISTS(
                        SELECT 1
                        FROM pg_constraint con
                        JOIN pg_class rel ON rel.oid = con.conrelid
                        JOIN pg_namespace ns ON ns.oid = rel.relnamespace
                        JOIN pg_index idx ON idx.indexrelid = con.conindid
                        WHERE ns.nspname = current_schema()
                          AND rel.relname = 'payment_batches'
                          AND con.contype = 'p'
                          AND NOT con.condeferrable
                          AND idx.indisunique
                          AND idx.indisvalid
                          AND idx.indisready
                          AND idx.indimmediate
                          AND pg_get_constraintdef(con.oid) ILIKE
                              'PRIMARY KEY (poolid, transactionconfirmationdata)%')";
                    var paymentBatchSchemaValid = await candidate.ExecuteScalarAsync<bool>(
                        new CommandDefinition(verifyPaymentBatchSchema,
                            transaction: tx, cancellationToken: ct));

                    if(!paymentBatchSchemaValid)
                        throw new InvalidOperationException(
                            "The payment-batch idempotency schema is missing or malformed. Apply " +
                            "src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql before enabling payment processing.");

                    const string ensureRow = @"INSERT INTO payout_manager_ownership(
                            id, generation, owner_id, owner_host, owner_process_id, acquired, released)
                        VALUES(1, 0, NULL, NULL, NULL, NULL, NULL)
                        ON CONFLICT(id) DO NOTHING";
                    await candidate.ExecuteAsync(new CommandDefinition(ensureRow,
                        transaction: tx, cancellationToken: ct));

                    const string acquireOwnership = @"UPDATE payout_manager_ownership
                        SET generation = generation + 1,
                            owner_id = @ownerId,
                            owner_host = @ownerHost,
                            owner_process_id = @ownerProcessId,
                            acquired = now(),
                            released = NULL
                        WHERE id = 1 AND owner_id IS NULL
                        RETURNING generation";
                    generation = await candidate.QuerySingleOrDefaultAsync<long>(
                        new CommandDefinition(acquireOwnership, new
                        {
                            ownerId,
                            ownerHost = Environment.MachineName,
                            ownerProcessId = Environment.ProcessId,
                        }, tx, cancellationToken: ct));

                    if(generation == 0)
                    {
                        tx.Rollback();
                    }
                    else
                        tx.Commit();
                }

                if(generation == 0)
                {
                    candidate.Dispose();
                    return false;
                }

                connection = candidate;
                acquired = true;
                logger.Info(() => $"Acquired durable PostgreSQL payout-manager ownership generation {generation} [{ownerId}]");
                return true;
            }

            catch(PostgresException ex) when(ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                candidate.Dispose();
                throw new InvalidOperationException(
                    "The payout-manager ownership schema is missing. Apply " +
                    "src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql before enabling payment processing.", ex);
            }

            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        finally
        {
            gate.Release();
        }
    }

    public async Task EnsureHeldAsync(CancellationToken ct)
    {
        if(Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(PostgresPayoutManagerLease));
        await gate.WaitAsync(ct);

        try
        {
            if(!acquired || connection == null || connection.State != ConnectionState.Open)
                throw new InvalidOperationException(
                    "The PostgreSQL payout-manager guard session is no longer available");

            const string query = @"SELECT EXISTS(
                    SELECT 1 FROM payout_manager_ownership
                    WHERE id = 1 AND owner_id = @ownerId AND generation = @generation)";
            var held = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(query,
                new { ownerId, generation }, cancellationToken: ct));

            if(!held)
                throw new InvalidOperationException(
                    "The durable PostgreSQL payout-manager ownership token no longer matches this process");
        }

        catch(Exception ex) when(ex is not OperationCanceledException)
        {
            acquired = false;
            throw new InvalidOperationException(
                "Lost the PostgreSQL payout-manager guard session; payment processing is stopping before the next cycle",
                ex);
        }

        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool retainOwnership;

        lock(financialStateGate)
        {
            if(disposed != 0)
                return;

            disposed = 1;
            retainOwnership = activeFinancialOperations > 0 || financialOutcomeUncertain;
        }

        await gate.WaitAsync();

        try
        {
            if(connection != null)
            {
                if(acquired && connection.State == ConnectionState.Open && !retainOwnership)
                {
                    try
                    {
                        const string release = @"UPDATE payout_manager_ownership
                            SET owner_id = NULL,
                                owner_host = NULL,
                                owner_process_id = NULL,
                                released = now()
                            WHERE id = 1 AND owner_id = @ownerId AND generation = @generation";
                        var released = await connection.ExecuteAsync(new CommandDefinition(release,
                            new { ownerId, generation }, commandTimeout: 5));

                        if(released == 0)
                            logger.Error(() => "Unable to clear durable payout-manager ownership because the stored token changed");
                    }

                    catch(Exception ex)
                    {
                        // Fail closed. Leaving the durable row owned prevents an unsafe
                        // replacement process from starting after an uncertain shutdown.
                        logger.Error(ex, () => "Unable to clear durable payout-manager ownership; manual release is required after confirming this process is stopped");
                    }
                }

                else if(retainOwnership)
                {
                    logger.Error(() => "Retaining durable payout-manager ownership because a wallet submission is still active or has an unknown outcome. Reconcile wallet history before manual release");
                }

                // The session advisory lock is released when this connection closes.
                connection.Dispose();
                connection = null;
            }

            acquired = false;
        }

        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }
}
