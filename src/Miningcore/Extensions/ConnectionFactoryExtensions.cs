using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Miningcore.Persistence;

namespace Miningcore.Extensions;

public static class ConnectionFactoryExtensions
{
    /// <summary>
    /// Run the specified action providing it with a fresh connection returing its result.
    /// </summary>
    /// <returns>The result returned by the action</returns>
    public static async Task Run(this IConnectionFactory factory,
        Func<IDbConnection, Task> action)
    {
        using(var con = await factory.OpenConnectionAsync())
        {
            await action(con);
        }
    }

    /// <summary>
    /// Run the specified action providing it with a fresh connection returing its result.
    /// </summary>
    /// <returns>The result returned by the action</returns>
    public static async Task<T> Run<T>(this IConnectionFactory factory,
        Func<IDbConnection, Task<T>> action)
    {
        using(var con = await factory.OpenConnectionAsync())
        {
            return await action(con);
        }
    }

    /// <summary>
    /// Run the specified action inside a transaction. If the action throws an exception,
    /// the transaction is rolled back. Otherwise it is commited.
    /// </summary>
    public static async Task RunTx(this IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task> action,
        bool autoCommit = true, IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default,
        bool classifyCommitOutcome = false)
    {
        await RunTxCore(factory, async (con, tx) =>
        {
            await action(con, tx);
            return true;
        }, autoCommit, isolation, ct, classifyCommitOutcome);
    }

    /// <summary>
    /// Run the specified action inside a transaction. If the action throws an exception,
    /// the transaction is rolled back. Otherwise it is commited.
    /// </summary>
    /// <returns>The result returned by the action</returns>
    public static async Task<T> RunTx<T>(this IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task<T>> func,
        bool autoCommit = true, IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct = default,
        bool classifyCommitOutcome = false)
    {
        return await RunTxCore(factory, func, autoCommit, isolation, ct,
            classifyCommitOutcome);
    }

    private static async Task<T> RunTxCore<T>(IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task<T>> func,
        bool autoCommit, IsolationLevel isolation, CancellationToken ct,
        bool classifyCommitOutcome)
    {
        IDbConnection con = null;
        IDbTransaction tx = null;
        var outcome = TransactionOutcome.NotCommitted;
        Exception failure = null;
        T result = default;

        try
        {
            con = await OpenConnectionAsync(factory, ct);
            tx = await BeginTransactionAsync(con, isolation, ct);

            try
            {
                result = await func(con, tx);

                if(autoCommit)
                {
                    try
                    {
                        await CommitAsync(tx, ct, classifyCommitOutcome);
                        outcome = TransactionOutcome.Committed;
                    }
                    catch(TransactionCommitOutcomeUncertainException ex)
                    {
                        outcome = TransactionOutcome.CommitUncertain;
                        failure = ex;
                    }
                }
            }
            catch(Exception ex)
            {
                failure = ex;
            }

            if(failure != null && outcome == TransactionOutcome.NotCommitted)
                await TryRollbackAsync(tx, failure, ct);
        }
        catch(Exception ex)
        {
            failure ??= ex;
        }
        finally
        {
            var cleanupFailure = await DisposeTransactionResourcesAsync(tx, con);

            if(cleanupFailure != null)
            {
                if(failure != null)
                    failure.Data[CleanupExceptionDataKey] = cleanupFailure;
                else if(outcome == TransactionOutcome.Committed)
                    failure = new TransactionCommittedCleanupException(
                        "The database transaction committed, but transaction cleanup failed",
                        cleanupFailure);
                else
                    failure = cleanupFailure;
            }
        }

        if(failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return result;
    }

    internal const string RollbackExceptionDataKey = "Miningcore.RollbackException";
    internal const string CleanupExceptionDataKey = "Miningcore.TransactionCleanupException";

    private enum TransactionOutcome
    {
        NotCommitted,
        CommitUncertain,
        Committed,
    }

    private static async Task<Exception> DisposeTransactionResourcesAsync(
        IDbTransaction tx, IDbConnection con)
    {
        List<Exception> failures = null;

        await DisposeResourceSafelyAsync(tx, failures ??= new List<Exception>());
        await DisposeResourceSafelyAsync(con, failures);

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "Multiple database transaction cleanup operations failed", failures),
        };
    }

    private static async Task DisposeResourceSafelyAsync(object resource,
        ICollection<Exception> failures)
    {
        if(resource == null)
            return;

        try
        {
            if(resource is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if(resource is IDisposable disposable)
                disposable.Dispose();
        }
        catch(Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static Task<IDbConnection> OpenConnectionAsync(
        IConnectionFactory factory, CancellationToken ct)
    {
        if(factory is ICancellableConnectionFactory cancellable)
            return cancellable.OpenConnectionAsync(ct);

        // Third-party/test factories retain source compatibility. The bounded wait prevents the
        // queue worker from hanging even when the legacy factory cannot abort its underlying open.
        return factory.OpenConnectionAsync().WaitAsync(ct);
    }

    private static async Task<IDbTransaction> BeginTransactionAsync(
        IDbConnection connection, IsolationLevel isolation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if(connection is DbConnection dbConnection)
            return await dbConnection.BeginTransactionAsync(isolation, ct);

        return connection.BeginTransaction(isolation);
    }

    private static async Task CommitAsync(IDbTransaction tx,
        CancellationToken ct, bool classifyCommitOutcome)
    {
        // Cancellation observed before the commit call proves the transaction was not submitted.
        // Only failures after entering the provider commit API are outcome-uncertain.
        ct.ThrowIfCancellationRequested();

        try
        {
            if(tx is DbTransaction dbTransaction)
                await dbTransaction.CommitAsync(ct);
            else
                tx.Commit();
        }
        catch(Exception ex) when(classifyCommitOutcome)
        {
            throw new TransactionCommitOutcomeUncertainException(
                "The database transaction commit outcome is uncertain", ex);
        }
    }

    private static async Task TryRollbackAsync(IDbTransaction tx,
        Exception originalException, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if(tx is DbTransaction dbTransaction)
                await dbTransaction.RollbackAsync(ct);
            else
                tx.Rollback();
        }
        catch(Exception rollbackException)
        {
            // A connection failure can dispose the Npgsql transaction before control reaches
            // this catch block. Preserve the original database exception so callers can apply
            // their retry or durable-recovery policy; retain the secondary rollback failure for
            // diagnostics without replacing the actionable exception.
            originalException.Data[RollbackExceptionDataKey] = rollbackException;
        }
    }
}

public sealed class TransactionCommitOutcomeUncertainException :
    InvalidOperationException
{
    public TransactionCommitOutcomeUncertainException(string message,
        Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class TransactionCommittedCleanupException :
    InvalidOperationException
{
    public TransactionCommittedCleanupException(string message,
        Exception innerException) : base(message, innerException)
    {
    }
}
