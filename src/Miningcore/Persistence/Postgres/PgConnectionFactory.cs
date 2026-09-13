using System.Data;
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

    public async Task<IDbConnection> OpenConnectionAsync(CancellationToken ct)
    {
        NpgsqlConnection con = null;
        try
        {
            con = new NpgsqlConnection(connectionString);
            await con.OpenAsync(ct);
            return con;
        }
        catch(OperationCanceledException)
        {
            if(con != null)
                await con.DisposeAsync();
            throw;
        }
        catch(Exception error)
        {
            if(con != null)
                await con.DisposeAsync();
            // Opening can disclose passwords, server-supplied identity strings, or local
            // certificate/key paths through nested exceptions. Callers routinely log these.
            // Keep retry classification, but do not retain the untrusted exception graph.
            throw new PostgresConnectionException(error is NpgsqlException { IsTransient: true });
        }
    }
}

internal sealed class PostgresConnectionException(bool isTransient) : NpgsqlException(
    "PostgreSQL connection failed. Check connectivity, authentication, SSL mode and certificate configuration; connection details omitted.")
{
    public override bool IsTransient => isTransient;
}
