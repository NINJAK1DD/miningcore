using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Miningcore.Persistence;
using NLog;
using Npgsql;

namespace Miningcore.Extensions;

public static class ConnectionFactoryExtensions
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

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
        bool classifyCommitOutcome = false,
        TimeSpan? resourceCleanupTimeout = null)
    {
        await RunTxCore(factory, async (con, tx) =>
        {
            await action(con, tx);
            return true;
        }, autoCommit, isolation, ct, classifyCommitOutcome,
            resourceCleanupTimeout ?? DefaultResourceCleanupTimeout);
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
        bool classifyCommitOutcome = false,
        TimeSpan? resourceCleanupTimeout = null)
    {
        return await RunTxCore(factory, func, autoCommit, isolation, ct,
            classifyCommitOutcome,
            resourceCleanupTimeout ?? DefaultResourceCleanupTimeout);
    }

    private static async Task<T> RunTxCore<T>(IConnectionFactory factory,
        Func<IDbConnection, IDbTransaction, Task<T>> func,
        bool autoCommit, IsolationLevel isolation, CancellationToken ct,
        bool classifyCommitOutcome, TimeSpan resourceCleanupTimeout)
    {
        if(resourceCleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(resourceCleanupTimeout),
                "Database transaction resource cleanup timeout must be positive");

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
                    catch(PostgresException ex) when(classifyCommitOutcome &&
                        IsKnownRejectedCommit(ex))
                    {
                        // PostgreSQL has already completed this transaction by rejecting COMMIT.
                        // Do not issue a misleading rollback against the completed provider object.
                        outcome = TransactionOutcome.CommitRejected;
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
            // The bounded, ordered cleanup machinery exists to preserve a classified financial
            // outcome when provider cleanup hangs or fails. Keep ordinary callers on the prior
            // synchronous disposal path so API, statistics and payout transactions do not pay a
            // Task.Run and timer cost on every successful transaction.
            var cleanupFailure = classifyCommitOutcome
                ? await DisposeTransactionResourcesAsync(tx, con,
                    resourceCleanupTimeout, outcome)
                : DisposeTransactionResourcesSynchronously(tx, con);

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
    internal const string LateCleanupExceptionDataKey =
        "Miningcore.LateTransactionCleanupException";
    internal static readonly TimeSpan DefaultResourceCleanupTimeout =
        TimeSpan.FromSeconds(4);
    internal static readonly AsyncLocal<Func<Task<Exception>, TimeSpan,
        Task<Exception>>> ResourceCleanupWaitOverride = new();

    private enum TransactionOutcome
    {
        NotCommitted,
        CommitRejected,
        CommitUncertain,
        Committed,
    }

    private static async Task<Exception> DisposeTransactionResourcesAsync(
        IDbTransaction tx, IDbConnection con, TimeSpan timeout,
        TransactionOutcome outcome)
    {
        var progress = new TransactionCleanupProgress();
        var elapsed = Stopwatch.StartNew();
        var cleanupTask = Task.Run(() => DisposeTransactionResourcesOrderedAsync(
            tx, con, progress));

        try
        {
            var wait = ResourceCleanupWaitOverride.Value;
            return wait != null
                ? await wait(cleanupTask, timeout)
                : await cleanupTask.WaitAsync(timeout);
        }
        catch(TimeoutException ex)
        {
            var timedOutResource = progress.ActiveResource;
            var timeoutFailure = new TransactionResourceCleanupTimeoutException(
                $"Timed out after {timeout} while disposing database transaction resources " +
                $"in order (active resource: {timedOutResource})",
                timedOutResource, progress.ConnectionCleanupStarted, ex);

            if(cleanupTask.IsCompleted)
            {
                var completedFailure = GetCompletedCleanupFailure(cleanupTask);
                if(completedFailure != null)
                    timeoutFailure.Data[LateCleanupExceptionDataKey] = completedFailure;

                LogLateCleanupCompletion(cleanupTask, outcome, timedOutResource,
                    progress.ActiveResource, elapsed.Elapsed);
                return timeoutFailure;
            }

            // ADO.NET disposal has no cancellation contract. The ordered cleanup remains in the
            // background, but connection disposal cannot start until transaction disposal has
            // actually returned. Observe its eventual failure without replacing the already
            // classified financial outcome.
            _ = cleanupTask.ContinueWith(task =>
                LogLateCleanupCompletion(task, outcome, timedOutResource,
                    progress.ActiveResource, elapsed.Elapsed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return timeoutFailure;
        }
    }

    private static Exception DisposeTransactionResourcesSynchronously(
        IDbTransaction tx, IDbConnection con)
    {
        var failures = new List<Exception>();
        DisposeResourceSynchronously(tx, failures);
        DisposeResourceSynchronously(con, failures);

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(
                "Multiple database transaction cleanup operations failed", failures),
        };
    }

    private static void DisposeResourceSynchronously(object resource,
        ICollection<Exception> failures)
    {
        if(resource is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch(Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static Exception GetCompletedCleanupFailure(Task<Exception> task)
    {
        if(task.IsFaulted)
            return task.Exception;

        if(task.IsCanceled)
            return new TaskCanceledException(task);

        return task.Result;
    }

    private static void LogLateCleanupCompletion(Task<Exception> task,
        TransactionOutcome outcome, string timedOutResource,
        string finalResource, TimeSpan elapsed)
    {
        if(task.IsFaulted)
        {
            logger.Error(task.Exception,
                "Late database cleanup task faulted after its aggregate deadline. " +
                "Transaction outcome: {0}; timed-out resource: {1}; final resource: {2}; elapsed: {3}",
                outcome, timedOutResource, finalResource, elapsed);
            return;
        }

        if(task.IsCanceled)
        {
            logger.Error(
                "Late database cleanup task was cancelled after its aggregate deadline. " +
                "Transaction outcome: {0}; timed-out resource: {1}; final resource: {2}; elapsed: {3}",
                outcome, timedOutResource, finalResource, elapsed);
            return;
        }

        if(task.Result != null)
        {
            logger.Error(task.Result,
                "Late database cleanup completed with provider errors after its aggregate deadline. " +
                "Transaction outcome: {0}; timed-out resource: {1}; final resource: {2}; elapsed: {3}",
                outcome, timedOutResource, finalResource, elapsed);
            return;
        }

        logger.Debug(
            "Late database cleanup completed after its aggregate deadline. " +
            "Transaction outcome: {0}; timed-out resource: {1}; final resource: {2}; elapsed: {3}",
            outcome, timedOutResource, finalResource, elapsed);
    }

    private static async Task<Exception> DisposeTransactionResourcesOrderedAsync(
        IDbTransaction tx, IDbConnection con, TransactionCleanupProgress progress)
    {
        var failures = new List<Exception>();

        progress.ActiveResource = "transaction";
        await DisposeResourceSafelyAsync(tx, failures);
        progress.ConnectionCleanupStarted = true;
        progress.ActiveResource = "connection";
        await DisposeResourceSafelyAsync(con, failures);
        progress.ActiveResource = "complete";

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

    private sealed class TransactionCleanupProgress
    {
        private string activeResource = "not-started";
        private int connectionCleanupStarted;

        public string ActiveResource
        {
            get => Volatile.Read(ref activeResource);
            set => Volatile.Write(ref activeResource, value);
        }

        public bool ConnectionCleanupStarted
        {
            get => Volatile.Read(ref connectionCleanupStarted) != 0;
            set => Volatile.Write(ref connectionCleanupStarted, value ? 1 : 0);
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
        catch(PostgresException ex) when(classifyCommitOutcome &&
            IsKnownRejectedCommit(ex))
        {
            // A PostgreSQL ErrorResponse with a server-assigned SQLSTATE proves COMMIT was
            // rejected. Deadlocks, serialization failures and deferred-constraint failures are
            // therefore safely replayable and must retain their original provider exception.
            throw;
        }
        catch(Exception ex) when(classifyCommitOutcome)
        {
            // Cancellation after entering the provider commit API, transport failures and unknown
            // provider failures cannot prove whether PostgreSQL committed before communication
            // was lost. Preserve the fail-closed classification for every non-server response.
            throw new TransactionCommitOutcomeUncertainException(
                "The database transaction commit outcome is uncertain", ex);
        }
    }

    private static bool IsKnownRejectedCommit(PostgresException ex) =>
        !string.IsNullOrWhiteSpace(ex.SqlState);

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

internal sealed class TransactionResourceCleanupTimeoutException : TimeoutException
{
    public TransactionResourceCleanupTimeoutException(string message,
        string activeResource, bool connectionCleanupStarted,
        Exception innerException) : base(message, innerException)
    {
        ActiveResource = activeResource;
        ConnectionCleanupStarted = connectionCleanupStarted;
    }

    public string ActiveResource { get; }
    public bool ConnectionCleanupStarted { get; }
}
