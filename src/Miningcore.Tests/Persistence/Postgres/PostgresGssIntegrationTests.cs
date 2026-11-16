using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Persistence.Postgres;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Newtonsoft.Json.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Persistence.Postgres;

// Kerberos uses process-wide configuration/cache variables. Share the serialized
// policy collection, and give every test its own disposable PostgreSQL instance.
[Collection(PostgresPolicyCollection.Name)]
public sealed class PostgresGssIntegrationTests(ITestOutputHelper output)
{
    private static string RealmRoot => Environment.GetEnvironmentVariable("MININGCORE_TEST_GSS_ROOT");

    private static async Task<IsolatedPostgresServer> Server(bool gssAuthentication = true, bool wrongKeytab = false)
    {
        var server = new IsolatedPostgresServer();
        try
        {
            await server.Initialize();
            if(gssAuthentication)
                await server.GssAuthentication(Path.Combine(RealmRoot, wrongKeytab ? "denied.keytab" : "server.keytab"),
                    Environment.GetEnvironmentVariable("MININGCORE_TEST_GSS_REALM"));
            return server;
        }
        catch { await server.DisposeAsync(); throw; }
    }

    private static NpgsqlConnectionStringBuilder Connection(IsolatedPostgresServer server,
        PostgresSslMode? mode, string root = null, string host = "localhost", bool? legacyTls = null)
    {
        var config = PostgresConnectionPolicyTests.Config();
        config.Database = "postgres";
        config.Host = host;
        config.Port = server.Port;
        config.SslMode = mode;
        config.Tls = legacyTls;
        config.TlsRootCert = root;
        var builder = PostgresConnectionPolicy.Build(config, null);
        builder.Pooling = false; // Every assertion observes an actual new handshake.
        builder.Timeout = 5;
        return builder;
    }

    private async Task AssertTransport(NpgsqlConnectionStringBuilder builder, bool encrypted, bool tls,
        bool authenticated = true, bool dedicated = false)
    {
        var factory = new PgConnectionFactory(builder.ConnectionString);
        using var connection = dedicated ? await factory.OpenDedicatedConnectionAsync(CancellationToken.None)
            : await factory.OpenConnectionAsync();
        var actual = await connection.QuerySingleAsync<Transport>("""
            SELECT g.gss_authenticated AS authenticated, g.encrypted, g.principal, s.ssl AS tls
            FROM pg_stat_gssapi g JOIN pg_stat_ssl s ON s.pid=g.pid WHERE g.pid=pg_backend_pid()
            """);
        Assert.Equal(encrypted, actual.Encrypted);
        Assert.Equal(tls, actual.Tls);
        Assert.Equal(authenticated, actual.Authenticated);
        if(authenticated)
            Assert.Equal("miningcore@MININGCORE.TEST", actual.Principal);
        // Exercise real protocol data (including a payload larger than a GSS frame).
        var payload = new string('x', 100000);
        Assert.Equal(payload, await connection.ExecuteScalarAsync<string>("SELECT @payload::text", new { payload }));
        output.WriteLine($"Server-observed GSS authenticated={actual.Authenticated}, encrypted={actual.Encrypted}, TLS={actual.Tls}");
    }

    [GssPostgresTheory]
    [InlineData(null, true, false)]
    [InlineData(PostgresSslMode.Disable, true, false)]
    [InlineData(PostgresSslMode.Allow, true, false)]
    [InlineData(PostgresSslMode.Prefer, true, false)]
    [InlineData(PostgresSslMode.Require, false, true)]
    [InlineData(PostgresSslMode.VerifyCA, false, true)]
    [InlineData(PostgresSslMode.VerifyFull, false, true)]
    public async Task LiveKerberos_TransportMatchesPolicyAndAuthenticatesPrincipal(PostgresSslMode? mode, bool encrypted, bool tls)
    {
        await using var server = await Server();
        var root = mode is PostgresSslMode.VerifyCA or PostgresSslMode.VerifyFull ? server.Root : null;
        await AssertTransport(Connection(server, mode, root), encrypted, tls);
    }

    [GssPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveKerberos_LegacyTlsAndDedicatedGuardPreserveTransport(bool tls)
    {
        await using var server = await Server();
        var builder = Connection(server, null, legacyTls: tls);
        await AssertTransport(builder, !tls, tls);
        builder.Pooling = true; // Guard must still open a dedicated physical connection.
        await AssertTransport(builder, !tls, tls, dedicated: true);
    }

    [GssPostgresTheory]
    [InlineData(PostgresSslMode.Require, "off", false)]
    [InlineData(PostgresSslMode.VerifyCA, "off", false)]
    [InlineData(PostgresSslMode.VerifyFull, "off", false)]
    [InlineData(PostgresSslMode.VerifyCA, "untrusted", false)]
    [InlineData(PostgresSslMode.VerifyFull, "untrusted", false)]
    [InlineData(PostgresSslMode.VerifyFull, "valid", true)]
    [InlineData(PostgresSslMode.VerifyCA, "expired", false)]
    [InlineData(PostgresSslMode.VerifyFull, "expired", false)]
    public async Task LiveKerberos_AvailableGssCannotBypassMandatoryTls(PostgresSslMode mode, string certificate, bool wrongHost)
    {
        await using var server = await Server();
        await server.UseCertificate(certificate == "untrusted" ? "valid" : certificate);
        var host = wrongHost ? "127.0.0.1" : "localhost";
        // Establish that GSS encryption is actually available on this exact endpoint.
        await AssertTransport(Connection(server, PostgresSslMode.Prefer, host: host), true, false);
        var root = mode == PostgresSslMode.Require ? null : certificate == "untrusted" ? server.OtherRoot : server.Root;
        var factory = new PgConnectionFactory(Connection(server, mode, root, host).ConnectionString);
        var failure = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
        Assert.Equal(certificate == "off" ? PostgresConnectFailure.Other : PostgresConnectFailure.TlsHandshake, failure.Category);
    }

    [GssPostgresTheory]
    [InlineData("missing.ccache", PostgresSslMode.Prefer)]
    [InlineData("missing.ccache", PostgresSslMode.VerifyFull)]
    [InlineData("denied.ccache", PostgresSslMode.Prefer)]
    [InlineData("denied.ccache", PostgresSslMode.VerifyFull)]
    public async Task LiveKerberos_MissingOrUnmappedCredentialsCannotAuthenticate(string cache, PostgresSslMode mode)
    {
        await using var server = await Server();
        var builder = Connection(server, mode, mode == PostgresSslMode.VerifyFull ? server.Root : null);
        var valid = await FreshClient(builder, "client.ccache");
        Assert.Equal(0, valid.Code);
        var transport = JObject.Parse(valid.Output);
        Assert.True(transport["authenticated"].Value<bool>());
        Assert.Equal(mode == PostgresSslMode.Prefer, transport["encrypted"].Value<bool>());
        Assert.Equal(mode == PostgresSslMode.VerifyFull, transport["tls"].Value<bool>());
        var rejected = await FreshClient(builder, cache);
        Assert.Equal(2, rejected.Code);
        Assert.StartsWith("rejected:", rejected.Output);
        await AssertTransport(Connection(server, PostgresSslMode.Prefer), true, false);
    }

    [GssPostgresTheory]
    [InlineData(PostgresSslMode.Prefer)]
    [InlineData(PostgresSslMode.VerifyFull)]
    public async Task LiveKerberos_WrongServerKeytabCannotAuthenticate(PostgresSslMode mode)
    {
        await using var server = await Server(wrongKeytab: true);
        var factory = new PgConnectionFactory(Connection(server, mode,
            mode == PostgresSslMode.VerifyFull ? server.Root : null).ConnectionString);
        await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
    }

    [GssPostgresTheory]
    [InlineData("valid", true)]
    [InlineData("off", false)]
    public async Task LiveKerberos_OptionalPolicyFallsBackOnlyWhenServerAllowsIt(string certificate, bool tls)
    {
        await using var server = await Server(gssAuthentication: false); // Loopback trust is deliberate here.
        await server.UseCertificate(certificate);
        var result = await FreshClient(Connection(server, PostgresSslMode.Prefer), "missing.ccache");
        Assert.Equal(0, result.Code);
        var transport = JObject.Parse(result.Output);
        Assert.False(transport["authenticated"].Value<bool>());
        Assert.False(transport["encrypted"].Value<bool>());
        Assert.Equal(tls, transport["tls"].Value<bool>());
    }

    [GssPostgresFact]
    public async Task LiveKerberos_RegressionControlDemonstratesForcedDisableLosesEncryption()
    {
        await using var server = await Server();
        await server.UseCertificate("off");
        var builder = Connection(server, null);
        await AssertTransport(builder, true, false);
        // Negative control reproduces the former unconditional-disable behavior.
        // Authentication alone does not encrypt application data.
        builder.GssEncryptionMode = GssEncryptionMode.Disable;
        await AssertTransport(builder, false, false);
    }

    private static async Task<(int Code, string Output)> FreshClient(NpgsqlConnectionStringBuilder builder, string cache)
    {
        // Changing managed Environment after native GSS initialization does not
        // reliably replace acquired credentials. Start each identity in a fresh OS process.
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"));
        start.ArgumentList.Add("postgres-gss-probe");
        start.Environment["KRB5CCNAME"] = "FILE:" + Path.Combine(RealmRoot, cache);
        start.Environment["MININGCORE_TEST_GSS_CONNECTION"] = builder.ConnectionString;
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode is 0 or 2, "Unexpected GSS probe exit: " + await stderr);
        return (process.ExitCode, (await stdout).Trim());
    }

    private sealed class Transport
    {
        public bool Authenticated { get; set; }
        public bool Encrypted { get; set; }
        public bool Tls { get; set; }
        public string Principal { get; set; }
    }
}

public sealed class GssPostgresTheoryAttribute : TheoryAttribute
{
    internal static bool Available => OperatingSystem.IsLinux() &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_GSS_ROOT")) &&
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN"));
    internal const string Reason = "Requires the documented disposable MIT Kerberos and PostgreSQL lab";

    public GssPostgresTheoryAttribute()
    {
        if(!Available)
            Skip = Reason;
    }
}

public sealed class GssPostgresFactAttribute : FactAttribute
{
    public GssPostgresFactAttribute()
    {
        if(!GssPostgresTheoryAttribute.Available)
            Skip = GssPostgresTheoryAttribute.Reason;
    }
}
