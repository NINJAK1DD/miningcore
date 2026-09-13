using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Npgsql;

namespace Miningcore.Persistence.Postgres;

public class PgConnectionFactory : IConnectionFactory, ICancellableConnectionFactory
{
    public PgConnectionFactory(string connectionString)
    {
        this.connectionString = connectionString;
    }

    private readonly string connectionString;

    public Task<IDbConnection> OpenConnectionAsync() =>
        OpenConnectionAsync(CancellationToken.None);

    public Task<IDbConnection> OpenConnectionAsync(CancellationToken ct) =>
        OpenConnectionAsync(() => new NpgsqlConnection(connectionString), ct);

    // Keep construction, opening and failed-open cleanup inside one sanitization boundary.
    // The internal seam permits deterministic fault injection, including failing disposal.
    internal static async Task<IDbConnection> OpenConnectionAsync(Func<DbConnection> create, CancellationToken ct)
    {
        DbConnection con = null;
        try
        {
            con = create();
            await con.OpenAsync(ct);
            return con;
        }
        catch(OperationCanceledException)
        {
            await DisposeAfterFailedOpen(con);
            throw;
        }
        catch(Exception error)
        {
            await DisposeAfterFailedOpen(con);
            // Opening can disclose passwords, server-supplied identity strings, or local
            // certificate/key paths through nested exceptions. Callers routinely log these.
            // Keep retry classification, but do not retain the untrusted exception graph.
            throw new PostgresConnectionException(PostgresConnectionException.Classify(error),
                error is NpgsqlException { IsTransient: true });
        }
    }

    private static async ValueTask DisposeAfterFailedOpen(DbConnection connection)
    {
        if(connection == null)
            return;
        try { await connection.DisposeAsync(); }
        // Cleanup must never replace cancellation or leak a second provider exception.
        catch(Exception) { }
    }
}

internal enum PostgresConnectFailure
{
    Other,
    Configuration,
    Network,
    Timeout,
    Authentication,
    TlsHandshake,
    TlsFileAccess,
}

internal sealed class PostgresConnectionException(PostgresConnectFailure category, bool isTransient) : NpgsqlException(
    $"PostgreSQL connection failed ({category}). Check the PostgreSQL connection troubleshooting guide; connection details omitted.")
{
    public PostgresConnectFailure Category { get; } = category;
    public override bool IsTransient => isTransient;

    internal static PostgresConnectFailure Classify(Exception error)
    {
        // Inspect only structured types/codes, never exception messages, paths or type names.
        // Npgsql does not expose typed hostname/expiry/chain distinctions; do not guess them.
        var pending = new Stack<Exception>();
        pending.Push(error);
        var result = PostgresConnectFailure.Other;
        for(var count = 0; pending.Count > 0 && count < 64; count++)
        {
            var current = pending.Pop();
            var category = current switch
            {
                PostgresException { SqlState: PostgresErrorCodes.InvalidPassword or
                    PostgresErrorCodes.InvalidAuthorizationSpecification } => PostgresConnectFailure.Authentication,
                AuthenticationException => PostgresConnectFailure.TlsHandshake,
                FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or
                    CryptographicException => PostgresConnectFailure.TlsFileAccess,
                TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut } => PostgresConnectFailure.Timeout,
                SocketException or IOException => PostgresConnectFailure.Network,
                ArgumentException => PostgresConnectFailure.Configuration,
                _ => PostgresConnectFailure.Other,
            };
            // Prefer specific causes over their generic transport wrappers. A handshake
            // failure takes precedence over platform-specific cryptographic inner errors.
            if(Priority(category) > Priority(result))
                result = category;
            if(current is AggregateException aggregate)
            {
                foreach(var inner in aggregate.InnerExceptions.Take(64 - count))
                    pending.Push(inner);
            }
            else if(current.InnerException != null)
                pending.Push(current.InnerException);
        }
        return result;
    }

    private static int Priority(PostgresConnectFailure category) => category switch
    {
        PostgresConnectFailure.TlsHandshake => 6,
        PostgresConnectFailure.Authentication => 5,
        PostgresConnectFailure.TlsFileAccess => 4,
        PostgresConnectFailure.Timeout => 3,
        PostgresConnectFailure.Network => 2,
        PostgresConnectFailure.Configuration => 1,
        _ => 0,
    };
}
