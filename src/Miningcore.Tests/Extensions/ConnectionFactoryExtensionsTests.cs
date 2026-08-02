using System;
using System.Data;
using System.Data.Common;
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
    }

    private sealed class ControlledDbTransaction : DbTransaction
    {
        public Func<CancellationToken, Task> CommitAsyncAction { get; set; }
        public int RollbackCalls { get; private set; }
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => null;
        public override void Commit() { }
        public override Task CommitAsync(CancellationToken cancellationToken = default) =>
            CommitAsyncAction?.Invoke(cancellationToken) ?? Task.CompletedTask;
        public override void Rollback() => RollbackCalls++;
        public override Task RollbackAsync(
            CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.CompletedTask;
        }
    }
}
