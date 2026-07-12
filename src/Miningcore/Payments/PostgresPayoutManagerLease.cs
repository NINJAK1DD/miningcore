using System.Data;
using Dapper;
using Miningcore.Persistence;
using NLog;

namespace Miningcore.Payments;

/// <summary>
/// Holds a PostgreSQL session advisory lock for the complete payout-manager lifetime.
/// Advisory locks are scoped to a database, so the same lock ids may safely be used by
/// independent Miningcore installations that use different databases. Losing the session
/// is detected before the next cycle, but does not fence financial work already in progress.
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
    private IDbConnection connection;
    private bool acquired;
    private int disposed;

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

                connection = candidate;
                acquired = true;
                logger.Info(() => "Acquired PostgreSQL payout-manager concurrent-start guard");
                return true;
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

            var command = new CommandDefinition("SELECT 1", cancellationToken: ct);
            await connection.ExecuteScalarAsync<int>(command);
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
        if(Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await gate.WaitAsync();

        try
        {
            if(connection != null)
            {
                // Session advisory locks are released by PostgreSQL when this connection
                // closes. Avoid a final network round-trip so shutdown cannot stall on an
                // unavailable database.
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
