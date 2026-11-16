using Dapper;
using Miningcore.Persistence.Postgres;
using Newtonsoft.Json;

internal static class PostgresGssProbe
{
    // Native Kerberos credential state must be selected before process startup.
    // Only the disposable lab supplies this connection; never print its contents.
    public static async Task<int> RunAsync()
    {
        var factory = new PgConnectionFactory(Environment.GetEnvironmentVariable("MININGCORE_TEST_GSS_CONNECTION"));
        try
        {
            using var connection = await factory.OpenConnectionAsync();
            var transport = await connection.QuerySingleAsync("""
                SELECT g.gss_authenticated AS authenticated, g.encrypted, s.ssl AS tls
                FROM pg_stat_gssapi g JOIN pg_stat_ssl s ON s.pid=g.pid WHERE g.pid=pg_backend_pid()
                """);
            Console.WriteLine(JsonConvert.SerializeObject(transport));
            return 0;
        }
        catch(PostgresConnectionException error)
        {
            Console.WriteLine("rejected:" + error.Category);
            return 2;
        }
    }
}
