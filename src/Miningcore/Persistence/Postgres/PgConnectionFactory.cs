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
        catch(Exception)
        {
            // Intentionally ignored: cleanup must never replace cancellation or leak
            // a second provider exception. The original open outcome remains authoritative.
        }
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
    LocalFileAccess,
    SecurityMaterial,
    DatabaseNotFound,
    ConnectionLimit,
    ServerUnavailable,
}

internal sealed class PostgresConnectionException(PostgresConnectFailure category, bool isTransient) : NpgsqlException(
    $"PostgreSQL connection failed ({category}). " +
    (category == PostgresConnectFailure.Other
        ? "If the SSL mode requires TLS, check that the server supports TLS; other connection or authentication failures can also cause this result. "
        : string.Empty) +
    "Check the PostgreSQL connection troubleshooting guide; connection details omitted.")
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
                PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName } => PostgresConnectFailure.DatabaseNotFound,
                PostgresException { SqlState: PostgresErrorCodes.TooManyConnections } => PostgresConnectFailure.ConnectionLimit,
                PostgresException { SqlState: PostgresErrorCodes.CannotConnectNow } => PostgresConnectFailure.ServerUnavailable,
                AuthenticationException => PostgresConnectFailure.TlsHandshake,
                FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => PostgresConnectFailure.LocalFileAccess,
                CryptographicException => PostgresConnectFailure.SecurityMaterial,
                TimeoutException or SocketException { SocketErrorCode: SocketError.TimedOut } => PostgresConnectFailure.Timeout,
                SocketException => PostgresConnectFailure.Network,
                // An IOException alone can originate from a passfile, TLS material or a
                // transport stream. Only a structured inner cause establishes provenance.
                ArgumentException => PostgresConnectFailure.Configuration,
                _ => PostgresConnectFailure.Other,
            };
            // Prefer definite file-access failures even inside handshake wrappers. Generic
            // cryptographic causes remain below TLS handshake errors because they also
            // occur during certificate verification, not just material loading.
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
        PostgresConnectFailure.LocalFileAccess => 7,
        PostgresConnectFailure.TlsHandshake => 6,
        PostgresConnectFailure.Authentication or PostgresConnectFailure.DatabaseNotFound or
            PostgresConnectFailure.ConnectionLimit or PostgresConnectFailure.ServerUnavailable => 5,
        PostgresConnectFailure.SecurityMaterial => 4,
        PostgresConnectFailure.Timeout => 3,
        PostgresConnectFailure.Network => 2,
        PostgresConnectFailure.Configuration => 1,
        _ => 0,
    };
}
