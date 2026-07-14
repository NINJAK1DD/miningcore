using System;
using System.Data;
using System.Threading.Tasks;
using Miningcore.Extensions;
using Miningcore.Persistence;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Extensions;

public class ConnectionFactoryExtensionsTests
{
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
}
