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
        var con = new NpgsqlConnection(connectionString);
        await con.OpenAsync(ct);
        return con;
    }
}
