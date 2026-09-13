using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

public class PostgresConnectionFailureTests
{
    private const string Secret = "SECRET password;host;certificate-path\r\nforged log";

    public static IEnumerable<object[]> Causes()
    {
        yield return new object[] { new ArgumentException(Secret), PostgresConnectFailure.Configuration };
        yield return new object[] { new SocketException((int) SocketError.ConnectionRefused), PostgresConnectFailure.Network };
        yield return new object[] { new IOException(Secret), PostgresConnectFailure.Network };
        yield return new object[] { new TimeoutException(Secret), PostgresConnectFailure.Timeout };
        yield return new object[] { new SocketException((int) SocketError.TimedOut), PostgresConnectFailure.Timeout };
        yield return new object[] { new FileNotFoundException(Secret, Secret), PostgresConnectFailure.TlsFileAccess };
        yield return new object[] { new DirectoryNotFoundException(Secret), PostgresConnectFailure.TlsFileAccess };
        yield return new object[] { new UnauthorizedAccessException(Secret), PostgresConnectFailure.TlsFileAccess };
        yield return new object[] { new CryptographicException(Secret), PostgresConnectFailure.TlsFileAccess };
        yield return new object[] { new AuthenticationException(Secret, new CryptographicException(Secret)), PostgresConnectFailure.TlsHandshake };
        yield return new object[] { new PostgresException(Secret, "FATAL", "FATAL", "28P01"), PostgresConnectFailure.Authentication };
        yield return new object[] { new PostgresException(Secret, "FATAL", "FATAL", "28000"), PostgresConnectFailure.Authentication };
        yield return new object[] { new PostgresException(Secret, "FATAL", "FATAL", Secret), PostgresConnectFailure.Other };
        yield return new object[] { new Exception("hostname mismatch expired certificate " + Secret), PostgresConnectFailure.Other };
        yield return new object[] { new AggregateException(new IOException(Secret), new TimeoutException(Secret)), PostgresConnectFailure.Timeout };
    }

    [Theory]
    [MemberData(nameof(Causes))]
    public async Task FailedOpenCategorizesWithoutRetainingUntrustedData(Exception cause, object expected)
    {
        var provider = new NpgsqlException(Secret, cause);
        var connection = new FaultConnection(provider, new Exception(Secret));
        var error = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
            PgConnectionFactory.OpenConnectionAsync(() => connection, CancellationToken.None));
        Assert.Equal((PostgresConnectFailure) expected, error.Category);
        Assert.Equal(provider.IsTransient, error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.Empty(error.Data);
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.DoesNotContain("forged", error.ToString());
        Assert.Contains($"({expected})", error.Message);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public async Task FailingDisposalCannotReplaceCancellationOrItsToken()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = new OperationCanceledException("cancelled", cancelled.Token);
        var connection = new FaultConnection(cancellation, new Exception(Secret));
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PgConnectionFactory.OpenConnectionAsync(() => connection, cancelled.Token));
        Assert.Same(cancellation, error);
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.Equal(1, connection.DisposeCalls);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task ConstructorFailureIsCategorizedWithoutAttemptingDisposal()
    {
        var error = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
            PgConnectionFactory.OpenConnectionAsync(() => throw new ArgumentException(Secret), CancellationToken.None));
        Assert.Equal(PostgresConnectFailure.Configuration, error.Category);
        Assert.False(error.IsTransient);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    [Fact]
    public async Task SuccessfulOpenTransfersDisposalOwnershipToCaller()
    {
        var connection = new FaultConnection(null, null);
        var result = await PgConnectionFactory.OpenConnectionAsync(() => connection, CancellationToken.None);
        Assert.Same(connection, result);
        Assert.Equal(0, connection.DisposeCalls);
        await connection.DisposeAsync();
        Assert.Equal(1, connection.DisposeCalls);
    }

    private sealed class FaultConnection(Exception openError, Exception disposeError) : DbConnection
    {
        public int DisposeCalls { get; private set; }
        public override string ConnectionString { get; set; }
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Closed;
        public override Task OpenAsync(CancellationToken cancellationToken) =>
            openError == null ? Task.CompletedTask : Task.FromException(openError);
        public override ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return disposeError == null ? ValueTask.CompletedTask : ValueTask.FromException(disposeError);
        }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => throw new NotSupportedException();
        public override void Open() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
