using System.Data;
using System.Data.Common;
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
        using(var con = await OpenConnectionAsync(factory, ct))
        {
            using(var tx = await BeginTransactionAsync(con, isolation, ct))
            {
                try
                {
                    await action(con, tx);

                    if(autoCommit)
                        await CommitAsync(tx, ct, classifyCommitOutcome);
                }

                catch(Exception ex)
                {
                    if(ex is not TransactionCommitOutcomeUncertainException)
                        await TryRollbackAsync(tx, ex, ct);
                    throw;
                }
            }
        }
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
        using(var con = await OpenConnectionAsync(factory, ct))
        {
            using(var tx = await BeginTransactionAsync(con, isolation, ct))
            {
                try
                {
                    var result = await func(con, tx);

                    if(autoCommit)
                        await CommitAsync(tx, ct, classifyCommitOutcome);

                    return result;
                }

                catch(Exception ex)
                {
                    if(ex is not TransactionCommitOutcomeUncertainException)
                        await TryRollbackAsync(tx, ex, ct);
                    throw;
                }
            }
        }
    }

    internal const string RollbackExceptionDataKey = "Miningcore.RollbackException";

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
