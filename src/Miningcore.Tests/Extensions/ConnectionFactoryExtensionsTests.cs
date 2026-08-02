using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Extensions;
using Miningcore.Persistence;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Extensions;

public class ConnectionFactoryExtensionsTests
{
    [Fact]
    public async Task RunTx_CancellationBoundsLegacyConnectionOpen()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var neverOpened = new TaskCompletionSource<IDbConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        factory.OpenConnectionAsync().Returns(neverOpened.Task);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask, ct: timeout.Token));
    }

    [Fact]
    public async Task RunTx_CancellationBoundsTransactionBegin()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var transaction = new ControlledDbTransaction();
        var connection = new ControlledDbConnection(transaction)
        {
            BeginAsync = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return transaction;
            },
        };
        factory.OpenConnectionAsync().Returns(connection);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask, ct: timeout.Token));
    }

    [Fact]
    public async Task RunTx_CancellationInsideCommitIsOutcomeUncertain()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var transaction = new ControlledDbTransaction
        {
            CommitAsyncAction = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
        };
        var connection = new ControlledDbConnection(transaction);
        factory.OpenConnectionAsync().Returns(connection);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<
            TransactionCommitOutcomeUncertainException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask, ct: timeout.Token,
                classifyCommitOutcome: true));

        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Equal(0, transaction.RollbackCalls);
    }

    [Fact]
    public async Task RunTx_TransactionDisposeAfterCommitPreservesCommittedOutcome()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var transaction = new ControlledDbTransaction
        {
            DisposeFailure = new IOException("transaction dispose failed"),
        };
        var connection = new ControlledDbConnection(transaction);
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommittedCleanupException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true));

        Assert.Same(transaction.DisposeFailure, error.InnerException);
        Assert.Equal(1, transaction.CommitCalls);
        Assert.Equal(0, transaction.RollbackCalls);
        Assert.Equal(1, transaction.DisposeCalls);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public async Task RunTx_ConnectionDisposeAfterCommitPreservesCommittedOutcome()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var transaction = new ControlledDbTransaction();
        var connection = new ControlledDbConnection(transaction)
        {
            DisposeFailure = new IOException("connection dispose failed"),
        };
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommittedCleanupException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true));

        Assert.Same(connection.DisposeFailure, error.InnerException);
        Assert.Equal(1, transaction.CommitCalls);
        Assert.Equal(0, transaction.RollbackCalls);
        Assert.Equal(1, transaction.DisposeCalls);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public async Task RunTx_UncertainCommitRetainsTransactionDisposeAsSecondaryFailure()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var commitFailure = new IOException("commit transport failed");
        var transaction = new ControlledDbTransaction
        {
            CommitAsyncAction = _ => Task.FromException(commitFailure),
            DisposeFailure = new IOException("transaction dispose failed"),
        };
        var connection = new ControlledDbConnection(transaction);
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommitOutcomeUncertainException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true));

        Assert.Same(commitFailure, error.InnerException);
        Assert.Same(transaction.DisposeFailure,
            error.Data[ConnectionFactoryExtensions.CleanupExceptionDataKey]);
        Assert.Equal(0, transaction.RollbackCalls);
    }

    [Fact]
    public async Task RunTx_UncertainCommitRetainsConnectionDisposeAsSecondaryFailure()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var commitFailure = new IOException("commit transport failed");
        var transaction = new ControlledDbTransaction
        {
            CommitAsyncAction = _ => Task.FromException(commitFailure),
        };
        var connection = new ControlledDbConnection(transaction)
        {
            DisposeFailure = new IOException("connection dispose failed"),
        };
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommitOutcomeUncertainException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true));

        Assert.Same(commitFailure, error.InnerException);
        Assert.Same(connection.DisposeFailure,
            error.Data[ConnectionFactoryExtensions.CleanupExceptionDataKey]);
        Assert.Equal(0, transaction.RollbackCalls);
    }

    [Fact]
    public async Task RunTx_TransactionCleanupTimeoutPreservesCommittedOutcome()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var neverDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new ControlledDbTransaction
        {
            DisposeAsyncAction = () => new ValueTask(neverDisposed.Task),
        };
        var connection = new ControlledDbConnection(transaction);
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommittedCleanupException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true,
                resourceCleanupTimeout: TimeSpan.FromMilliseconds(50)));

        var timeout = Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Contains("database transaction", timeout.Message);
        Assert.Equal(1, transaction.CommitCalls);
        Assert.Equal(0, transaction.RollbackCalls);
        Assert.Equal(1, transaction.DisposeCalls);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public async Task RunTx_ConnectionCleanupTimeoutRemainsSecondaryToUncertainCommit()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var commitFailure = new IOException("commit transport failed");
        var neverDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new ControlledDbTransaction
        {
            CommitAsyncAction = _ => Task.FromException(commitFailure),
        };
        var connection = new ControlledDbConnection(transaction)
        {
            DisposeAsyncAction = () => new ValueTask(neverDisposed.Task),
        };
        factory.OpenConnectionAsync().Returns(connection);

        var error = await Assert.ThrowsAsync<
            TransactionCommitOutcomeUncertainException>(() =>
            factory.RunTx((_, _) => Task.CompletedTask,
                classifyCommitOutcome: true,
                resourceCleanupTimeout: TimeSpan.FromMilliseconds(50)));

        Assert.Same(commitFailure, error.InnerException);
        var timeout = Assert.IsType<TimeoutException>(
            error.Data[ConnectionFactoryExtensions.CleanupExceptionDataKey]);
        Assert.Contains("database connection", timeout.Message);
        Assert.Equal(0, transaction.RollbackCalls);
    }

    [Fact]
    public async Task RunTx_PreservesOriginalExceptionWhenRollbackFails()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var original = new TimeoutException("database command timed out");

        factory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        transaction.When(x => x.Rollback())
            .Do(_ => throw new ObjectDisposedException("NpgsqlTransaction"));

        var actual = await Assert.ThrowsAsync<TimeoutException>(() =>
            factory.RunTx((_, _) => Task.FromException(original)));

        Assert.Same(original, actual);
        Assert.IsType<ObjectDisposedException>(
            actual.Data[ConnectionFactoryExtensions.RollbackExceptionDataKey]);
        transaction.Received(1).Rollback();
    }

    [Fact]
    public async Task RunTxResult_PreservesOriginalExceptionWhenRollbackFails()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IDbConnection>();
        var transaction = Substitute.For<IDbTransaction>();
        var original = new TimeoutException("database command timed out");

        factory.OpenConnectionAsync().Returns(connection);
        connection.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(transaction);
        transaction.When(x => x.Rollback())
            .Do(_ => throw new ObjectDisposedException("NpgsqlTransaction"));

        var actual = await Assert.ThrowsAsync<TimeoutException>(() =>
            factory.RunTx<int>((_, _) => Task.FromException<int>(original)));

        Assert.Same(original, actual);
        Assert.IsType<ObjectDisposedException>(
            actual.Data[ConnectionFactoryExtensions.RollbackExceptionDataKey]);
        transaction.Received(1).Rollback();
    }

    private sealed class ControlledDbConnection : DbConnection
    {
        public ControlledDbConnection(DbTransaction transaction)
        {
            this.transaction = transaction;
        }

        private readonly DbTransaction transaction;
        public Func<CancellationToken, ValueTask<DbTransaction>> BeginAsync { get; set; }
        public Func<ValueTask> DisposeAsyncAction { get; set; }
        public Exception DisposeFailure { get; set; }
        public int DisposeCalls { get; private set; }

        public override string ConnectionString { get; set; }
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbCommand CreateDbCommand() =>
            throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel) => transaction;
        protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
            IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
            BeginAsync?.Invoke(cancellationToken) ??
            new ValueTask<DbTransaction>(transaction);

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;

            if(DisposeAsyncAction != null)
                return DisposeAsyncAction();

            return DisposeFailure != null
                ? ValueTask.FromException(DisposeFailure)
                : ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledDbTransaction : DbTransaction
    {
        public Func<CancellationToken, Task> CommitAsyncAction { get; set; }
        public Func<ValueTask> DisposeAsyncAction { get; set; }
        public Exception DisposeFailure { get; set; }
        public int CommitCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => null;
        public override void Commit() => CommitCalls++;
        public override Task CommitAsync(CancellationToken cancellationToken = default)
        {
            CommitCalls++;
            return CommitAsyncAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
        public override void Rollback() => RollbackCalls++;
        public override Task RollbackAsync(
            CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;

            if(DisposeAsyncAction != null)
                return DisposeAsyncAction();

            return DisposeFailure != null
                ? ValueTask.FromException(DisposeFailure)
                : ValueTask.CompletedTask;
        }
    }
}
