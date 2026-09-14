using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Persistence.Postgres;
using Miningcore.Tests.Util.Postgres;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Persistence.Postgres;

[Collection(PostgresPolicyCollection.Name)]
public class PostgresTlsIntegrationTests
{
    private readonly IsolatedPostgresServer server;
    private readonly ITestOutputHelper output;
    public PostgresTlsIntegrationTests(IsolatedPostgresServer server, ITestOutputHelper output)
    {
        this.server = server;
        this.output = output;
    }

    [IsolatedPostgresTheory]
    [InlineData("valid", PostgresSslMode.VerifyCA, "localhost", "trusted", true)]
    [InlineData("valid", PostgresSslMode.VerifyFull, "localhost", "trusted", true)]
    [InlineData("valid", PostgresSslMode.VerifyCA, "localhost", "untrusted", false)]
    [InlineData("valid", PostgresSslMode.VerifyFull, "localhost", "untrusted", false)]
    [InlineData("valid", PostgresSslMode.VerifyCA, "127.0.0.1", "trusted", true)]
    [InlineData("valid", PostgresSslMode.VerifyFull, "127.0.0.1", "trusted", false)]
    [InlineData("valid", PostgresSslMode.Require, "127.0.0.1", null, true)]
    [InlineData("valid", PostgresSslMode.VerifyFull, "localhost", "missing", false)]
    [InlineData("valid", PostgresSslMode.VerifyFull, "localhost", "invalid", false)]
    [InlineData("expired", PostgresSslMode.VerifyCA, "localhost", "trusted", false)]
    [InlineData("expired", PostgresSslMode.VerifyFull, "localhost", "trusted", false)]
    [InlineData("expired", PostgresSslMode.Require, "localhost", null, true)]
    [InlineData("off", PostgresSslMode.VerifyCA, "localhost", "trusted", false)]
    [InlineData("off", PostgresSslMode.VerifyFull, "localhost", "trusted", false)]
    [InlineData("off", PostgresSslMode.Require, "localhost", null, false)]
    [InlineData("off", PostgresSslMode.Prefer, "localhost", null, true)]
    [InlineData("off", PostgresSslMode.Disable, "localhost", null, true)]
    public async Task RealServerEnforcesCertificatePolicyWithoutDowngrade(
        string certificate, PostgresSslMode mode, string host, string root, bool succeeds)
    {
        await server.UseCertificate(certificate);
        output.WriteLine(await server.Run("postgres", "--version"));
        var builder = Connection(mode, host, root switch
        {
            "trusted" => server.Root, "untrusted" => server.OtherRoot,
            "missing" => server.Root + ".missing", "invalid" => server.InvalidRoot, _ => null,
        });
        var factory = new PgConnectionFactory(builder.ConnectionString);
        if(succeeds)
            await AssertOpened(factory, certificate != "off");
        else
        {
            var error = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
            var expected = certificate == "off" ? PostgresConnectFailure.Other :
                root == "missing" ? PostgresConnectFailure.LocalFileAccess :
                root == "invalid" ? PostgresConnectFailure.SecurityMaterial :
                PostgresConnectFailure.TlsHandshake;
            Assert.Equal(expected, error.Category);
            if(certificate == "off")
                Assert.Contains("If the SSL mode requires TLS, check that the server supports TLS", error.Message);
            AssertSafe(error);
        }
    }

    [IsolatedPostgresTheory]
    [InlineData("absent")]
    [InlineData("environment")]
    [InlineData("explicit")]
    public async Task TrustSourceLifecycleMatchesDocumentedPhysicalOpenBehavior(string source)
    {
        await server.UseCertificate("valid");
        var priorRoot = Environment.GetEnvironmentVariable("PGSSLROOTCERT");
        try
        {
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", source == "environment" ? server.Root : null);
            var builder = Connection(PostgresSslMode.VerifyFull, "localhost", source == "explicit" ? server.Root : null);
            Assert.Equal(source == "absent" ? null : server.Root, builder.RootCertificate);
            var factory = new PgConnectionFactory(builder.ConnectionString);
            // Build the factory before changing the environment. Pooling=false forces
            // Npgsql to resolve any remaining fallback on each physical connection.
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", server.Root);
            await AssertOpened(factory);
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", server.OtherRoot);
            if(source == "absent")
            {
                var error = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
                Assert.Equal(PostgresConnectFailure.TlsHandshake, error.Category);
                AssertSafe(error);
            }
            else
                await AssertOpened(factory);
        }
        finally { Environment.SetEnvironmentVariable("PGSSLROOTCERT", priorRoot); }
    }

    [IsolatedPostgresFact]
    public async Task ConfiguredPathDoesNotFreezeCertificateFileContents()
    {
        await server.UseCertificate("valid");
        var rotatingRoot = server.Root + ".rotation";
        File.Copy(server.Root, rotatingRoot, true);
        var factory = new PgConnectionFactory(Connection(PostgresSslMode.VerifyFull, "localhost", rotatingRoot).ConnectionString);
        try
        {
            await AssertOpened(factory);
            File.Copy(server.OtherRoot, rotatingRoot, true);
            var error = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
            Assert.Equal(PostgresConnectFailure.TlsHandshake, error.Category);
            AssertSafe(error);
        }
        finally { File.Delete(rotatingRoot); }
    }

    [IsolatedPostgresFact]
    public async Task MissingCaPreservesRetryClassificationAndCancellation()
    {
        await server.UseCertificate("valid");
        var factory = new PgConnectionFactory(Connection(PostgresSslMode.VerifyFull, "localhost", server.Root + ".missing").ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresConnectionException>(() => factory.OpenConnectionAsync());
        Assert.Equal(PostgresConnectFailure.LocalFileAccess, error.Category);
        // Keep Npgsql's retry contract; the safe category identifies the configuration error.
        Assert.True(error.IsTransient);
        AssertSafe(error);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.OpenConnectionAsync(cancelled.Token));
    }

    [IsolatedPostgresFact]
    public async Task PasswordSourcesAuthenticateUsingScramAndExplicitValuesTakePrecedence()
    {
        const string password = "ephemeral-password";
        var passfile = server.Root + ".pgpass";
        await WithScramAuthentication(async () =>
        {
            await File.WriteAllTextAsync(passfile, $"localhost:{server.Port}:postgres:miningcore:{password}\n");
            if(!OperatingSystem.IsWindows())
                File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Environment.SetEnvironmentVariable("PGPASSWORD", "wrong-environment-password");
            Environment.SetEnvironmentVariable("PGPASSFILE", passfile);

            var explicitPassword = Connection(PostgresSslMode.VerifyFull, "localhost", server.Root);
            explicitPassword.Password = password;
            await AssertOpened(new PgConnectionFactory(explicitPassword.ConnectionString));

            // Missing password delegates to PGPASSWORD, then PGPASSFILE.
            var config = PostgresConnectionPolicyTests.Config();
            config.Port = server.Port;
            config.Database = "postgres";
            config.SslMode = PostgresSslMode.VerifyFull;
            config.TlsRootCert = server.Root;
            var omitted = PostgresConnectionPolicy.Build(config);
            omitted.Pooling = false;
            Assert.Null(omitted.Password);
            Environment.SetEnvironmentVariable("PGPASSWORD", password);
            await AssertOpened(new PgConnectionFactory(omitted.ConnectionString));
            Environment.SetEnvironmentVariable("PGPASSWORD", null);
            await AssertOpened(new PgConnectionFactory(omitted.ConnectionString));

            explicitPassword.Password = "SECRET-wrong-password";
            var wrong = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
                new PgConnectionFactory(explicitPassword.ConnectionString).OpenConnectionAsync());
            Assert.Equal(PostgresConnectFailure.Authentication, wrong.Category);
            AssertSafe(wrong);
            Assert.DoesNotContain("SECRET", wrong.ToString());

            // Npgsql's connection-string parser treats Password= as omitted, just as
            // the legacy builder did. Verify the actual factory boundary, not just a getter.
            explicitPassword.Password = "";
            await AssertOpened(new PgConnectionFactory(explicitPassword.ConnectionString));
        }, () => { File.Delete(passfile); return Task.CompletedTask; });
    }

    [IsolatedPostgresTheory]
    [InlineData("missing")]
    [InlineData("directory")]
    public async Task PassfileAccessFailuresAreSourceNeutralAndDoNotExposePaths(string failure)
    {
        var passfile = server.Root + ".SECRET-passfile";
        if(failure == "directory")
            Directory.CreateDirectory(passfile);
        await WithScramAuthentication(async () =>
        {
            // First prove that TLS and database authentication are healthy.
            var builder = Connection(PostgresSslMode.VerifyFull, "localhost", server.Root);
            builder.Password = "ephemeral-password";
            await AssertOpened(new PgConnectionFactory(builder.ConnectionString));
            builder.Password = null;
            Environment.SetEnvironmentVariable("PGPASSFILE", passfile);
            var error = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
                new PgConnectionFactory(builder.ConnectionString).OpenConnectionAsync());
            Assert.Equal(PostgresConnectFailure.LocalFileAccess, error.Category);
            AssertSafe(error);
            Assert.DoesNotContain(passfile, error.ToString());
        }, () =>
        {
            if(Directory.Exists(passfile))
                Directory.Delete(passfile); // Only this fixture-created, empty directory.
            return Task.CompletedTask;
        });
    }

    [PostgresTlsUnixFact]
    public async Task UnreadablePassfileReportsLocalAccessWithoutExposingItsPath()
    {
        var passfile = server.Root + ".SECRET-unreadable-passfile";
        await WithScramAuthentication(async () =>
        {
            if(OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("This test requires Unix file permissions");
            var builder = Connection(PostgresSslMode.VerifyFull, "localhost", server.Root);
            builder.Password = "ephemeral-password";
            await AssertOpened(new PgConnectionFactory(builder.ConnectionString));
            builder.Password = null;
            await File.WriteAllTextAsync(passfile, "localhost:*:postgres:miningcore:ephemeral-password\n");
            File.SetUnixFileMode(passfile, UnixFileMode.None);
            Environment.SetEnvironmentVariable("PGPASSFILE", passfile);
            var error = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
                new PgConnectionFactory(builder.ConnectionString).OpenConnectionAsync());
            Assert.Equal(PostgresConnectFailure.LocalFileAccess, error.Category);
            AssertSafe(error);
            Assert.DoesNotContain(passfile, error.ToString());
        }, () =>
        {
            if(File.Exists(passfile))
            {
                if(!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(passfile);
            }
            return Task.CompletedTask;
        });
    }

    [IsolatedPostgresFact]
    public async Task MissingDatabaseReportsItsCategoryWithoutExposingItsName()
    {
        await server.UseCertificate("valid");
        var builder = Connection(PostgresSslMode.VerifyFull, "localhost", server.Root);
        builder.Database = "SECRET-missing-database";
        var error = await Assert.ThrowsAsync<PostgresConnectionException>(() =>
            new PgConnectionFactory(builder.ConnectionString).OpenConnectionAsync());
        Assert.Equal(PostgresConnectFailure.DatabaseNotFound, error.Category);
        AssertSafe(error);
    }

    private async Task WithScramAuthentication(Func<Task> body, params Func<Task>[] cleanup)
    {
        await server.UseCertificate("valid");
        var priorPassword = Environment.GetEnvironmentVariable("PGPASSWORD");
        var priorPassfile = Environment.GetEnvironmentVariable("PGPASSFILE");
        await PostgresTestCleanup.RunAsync(async () =>
        {
            // The role and password belong only to this temporary cluster.
            using(var connection = await new PgConnectionFactory(Connection(PostgresSslMode.VerifyFull, "localhost", server.Root).ConnectionString).OpenConnectionAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SET password_encryption = 'scram-sha-256'; ALTER ROLE miningcore PASSWORD 'ephemeral-password'";
                command.ExecuteNonQuery();
            }
            await server.PasswordAuthentication(true);
            Environment.SetEnvironmentVariable("PGPASSWORD", null);
            Environment.SetEnvironmentVariable("PGPASSFILE", null);
            await body();
        }, [
            () => { Environment.SetEnvironmentVariable("PGPASSWORD", priorPassword); return Task.CompletedTask; },
            () => { Environment.SetEnvironmentVariable("PGPASSFILE", priorPassfile); return Task.CompletedTask; },
            .. cleanup,
            () => server.PasswordAuthentication(false),
        ]);
    }

    [IsolatedPostgresFact]
    public async Task OccupiedPortRetriesWithoutTouchingTheOtherListener()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint) occupied.LocalEndpoint).Port;
        await using var isolated = new IsolatedPostgresServer();
        await isolated.Initialize(port);
        Assert.NotEqual(port, isolated.Port);
        Assert.True(occupied.Server.IsBound);
    }

    private NpgsqlConnectionStringBuilder Connection(PostgresSslMode mode, string host, string root)
    {
        var config = PostgresConnectionPolicyTests.Config();
        config.Database = "postgres";
        config.Host = host;
        config.Port = server.Port;
        config.SslMode = mode;
        config.TlsRootCert = root;
        var builder = PostgresConnectionPolicy.Build(config);
        builder.Pooling = false; // Every assertion must perform a fresh handshake.
        builder.Timeout = 5;
        builder.SslNegotiation = SslNegotiation.Postgres;
        return builder;
    }

    private static async Task AssertOpened(PgConnectionFactory factory, bool tls = true)
    {
        using var opened = await factory.OpenConnectionAsync();
        using var command = opened.CreateCommand();
        command.CommandText = "select ssl from pg_stat_ssl where pid = pg_backend_pid()";
        Assert.Equal(tls, (bool) command.ExecuteScalar());
    }

    private void AssertSafe(PostgresConnectionException error)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(server.Root, error.ToString());
        Assert.DoesNotContain(server.OtherRoot, error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
    }

}
