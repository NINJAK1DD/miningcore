using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Miningcore.Tests.Util;

// Shared isolated schema and one-slot pool for timeout and payout persistence tests.
internal sealed class PostgresPersistenceTestDatabase : IAsyncDisposable
{
    // Including the observer suffix, the ASCII identity stays within PostgreSQL's 63-byte limit.
    private readonly string schema = "miningcore_persist_" + Guid.NewGuid().ToString("N");
    // One generated identity deliberately serves as schema, search_path and application_name.
    public string SchemaAndApplicationName => schema;
    private NpgsqlConnectionStringBuilder settings;
    public NpgsqlConnection Observer { get; private set; }
    public PgConnectionFactory Factory { get; private set; }

    public static async Task<PostgresPersistenceTestDatabase> Create(IsolatedPostgresServer server, int timeout)
    {
        var db = new PostgresPersistenceTestDatabase();
        try
        {
            // TLS tests in the same collection may leave the shared server in an
            // intentional non-verifying mode. Select the required state explicitly.
            await server.UseCertificate("valid");
            await server.PasswordAuthentication(false);
            var config = new PostgresConfig
            {
                Host = "127.0.0.1", Port = server.Port, Database = "postgres", User = "miningcore",
                SslMode = PostgresSslMode.VerifyCA, TlsRootCert = server.Root, CommandTimeout = timeout,
            };
            db.settings = PostgresConnectionPolicy.Build(config, null, out var diagnostic);
            Assert.Equal(timeout, diagnostic.CommandTimeout);
            db.settings.SearchPath = db.schema;
            db.settings.ApplicationName = db.schema;
            db.settings.MaxPoolSize = 1;
            db.Factory = new PgConnectionFactory(db.settings.ConnectionString);
            var control = new NpgsqlConnectionStringBuilder(db.settings.ConnectionString)
            { Pooling = false, CommandTimeout = 10, ApplicationName = db.schema + "_observer" };
            db.Observer = new NpgsqlConnection(control.ConnectionString);
            await db.Observer.OpenAsync();
            await db.Observer.ExecuteAsync($"CREATE SCHEMA {db.schema}");
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Miningcore/Persistence/Postgres/Scripts/createdb.sql"));
            await db.Observer.ExecuteAsync(await File.ReadAllTextAsync(script));
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }

    public Task<int> Count(string table) => Observer.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table}");

    public async Task AssertUsable()
    {
        await WaitForTransactionEndAsync();
        // A one-slot pool must be usable after failure, without leaked transactions/locks.
        Assert.Equal(1, await Factory.Run(con => con.ExecuteScalarAsync<int>("SELECT 1")));
        Assert.Equal(0, await Observer.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name=@schema AND xact_start IS NOT NULL", new { schema }));
    }

    public async Task WaitForTransactionEndAsync()
    {
        // OnRetry is synchronous. ConfigureAwait(false) on every await here is
        // required to avoid deadlocking its caller under xUnit's sync context.
        // Move those callers to await if the production hook becomes asynchronous.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(await Observer.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name=@schema AND xact_start IS NOT NULL)",
            new { schema }, cancellationToken: deadline.Token)).ConfigureAwait(false))
            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if(Observer != null)
        {
            try
            {
                if(Observer.State == ConnectionState.Open)
                {
                    // Cleanup cannot mask a failed assertion with a DROP blocked by
                    // the deliberately sleeping test backend. Only this generated
                    // test identity on this private server can be terminated here.
                    await Observer.ExecuteAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name=@schema", new { schema });
                    await Observer.ExecuteAsync($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                }
            }
            finally { await Observer.DisposeAsync(); }
        }
        if(settings != null)
        {
            using var pool = new NpgsqlConnection(settings.ConnectionString);
            NpgsqlConnection.ClearPool(pool);
        }
    }
}
