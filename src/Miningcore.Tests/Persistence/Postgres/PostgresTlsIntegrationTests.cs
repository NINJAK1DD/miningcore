using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Persistence.Postgres;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Miningcore.Tests.Persistence.Postgres;

public sealed class PostgresTlsFactAttribute : FactAttribute
{
    public PostgresTlsFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = "Set MININGCORE_TEST_POSTGRES_BIN to the PostgreSQL bin directory to run isolated real TLS tests";
    }
}

public sealed class PostgresTlsTheoryAttribute : TheoryAttribute
{
    public PostgresTlsTheoryAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = "Set MININGCORE_TEST_POSTGRES_BIN to run isolated real TLS tests";
    }
}

public sealed class PostgresTlsUnixFactAttribute : FactAttribute
{
    public PostgresTlsUnixFactAttribute()
    {
        if(OperatingSystem.IsWindows())
            Skip = "Requires Unix file permissions and an unprivileged PostgreSQL test account";
        else if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN")))
            Skip = "Set MININGCORE_TEST_POSTGRES_BIN to run isolated real TLS tests";
    }
}

[Collection(PostgresPolicyCollection.Name)]
public class PostgresTlsIntegrationTests : IClassFixture<PostgresTlsIntegrationTests.TlsServer>
{
    private readonly TlsServer server;
    private readonly ITestOutputHelper output;
    public PostgresTlsIntegrationTests(TlsServer server, ITestOutputHelper output)
    {
        this.server = server;
        this.output = output;
    }

    [PostgresTlsTheory]
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

    [PostgresTlsTheory]
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

    [PostgresTlsFact]
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

    [PostgresTlsFact]
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

    [PostgresTlsFact]
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

    [PostgresTlsTheory]
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

    [PostgresTlsFact]
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

    [PostgresTlsFact]
    public async Task OccupiedPortRetriesWithoutTouchingTheOtherListener()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint) occupied.LocalEndpoint).Port;
        await using var isolated = new TlsServer();
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

    public sealed class TlsServer : IAsyncDisposable, IAsyncLifetime
    {
        private readonly string bin = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "miningcore-pg-tls-" + Guid.NewGuid().ToString("N"));
        private string Data => Path.Combine(directory, "data");
        private string Log => Path.Combine(directory, "postgres.log");
        public string Root => Path.Combine(directory, "root;quoted=ca.crt");
        public string OtherRoot => Path.Combine(directory, "other.crt");
        public string InvalidRoot => Path.Combine(directory, "invalid.crt");
        public int Port { get; private set; }
        private bool started;
        private string activeCertificate;

        public Task InitializeAsync() => string.IsNullOrEmpty(bin) ? Task.CompletedTask : Initialize();
        Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

        public async Task Initialize(int? firstPort = null)
        {
            Directory.CreateDirectory(directory);
            await Run("initdb", "-D", Data, "-U", "miningcore", "--auth=trust", "--encoding=UTF8", "--no-locale");
            using var rootKey = RSA.Create(2048);
            using var ca = CreateCa(rootKey, "Miningcore ephemeral TLS test CA");
            await File.WriteAllTextAsync(Root, ca.ExportCertificatePem());
            using var otherKey = RSA.Create(2048);
            using var otherCa = CreateCa(otherKey, "Miningcore untrusted ephemeral CA");
            await File.WriteAllTextAsync(OtherRoot, otherCa.ExportCertificatePem());
            await File.WriteAllTextAsync(InvalidRoot, "not a certificate");
            CreateServerCertificate(ca, "valid", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            CreateServerCertificate(ca, "expired", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
            await Start("valid", firstPort);
        }

        private static X509Certificate2 CreateCa(RSA key, string name)
        {
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
        }

        private void CreateServerCertificate(X509Certificate2 ca, string name, DateTimeOffset from, DateTimeOffset to)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost"); // Deliberately no IP SAN: exercises VerifyCA vs VerifyFull.
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var cert = request.Create(ca, from, to, RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(Path.Combine(Data, name + ".crt"), cert.ExportCertificatePem());
            var path = Path.Combine(Data, name + ".key");
            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            if(!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static int AvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint) listener.LocalEndpoint).Port;
        }

        private static bool PortIsOccupied(int port)
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            try { probe.Start(); return false; }
            catch(SocketException error) when(error.SocketErrorCode == SocketError.AddressAlreadyInUse) { return true; }
        }

        private async Task Start(string certificate, int? firstPort = null)
        {
            for(var attempt = 0; attempt < 3; attempt++)
            {
                // Select as late as possible, and retry only an occupied TCP port after
                // PostgreSQL has exited. Never stop or alter the process owning that port.
                Port = attempt == 0 && firstPort.HasValue ? firstPort.Value : AvailablePort();
                // Debian/Ubuntu system Unix sockets are not writable by the test account.
                await File.WriteAllTextAsync(Path.Combine(Data, "postgresql.conf"),
                    $"listen_addresses='127.0.0.1'\nport={Port}\nunix_socket_directories=''\n" +
                    (certificate == "off" ? "ssl=off\n" :
                        $"ssl=on\nssl_cert_file='{certificate}.crt'\nssl_key_file='{certificate}.key'\n"));
                try
                {
                    await Run("pg_ctl", "-D", Data, "-l", Log, "-w", "start");
                    started = true;
                    activeCertificate = certificate;
                    return;
                }
                catch(Xunit.Sdk.XunitException) when(attempt < 2 &&
                    !File.Exists(Path.Combine(Data, "postmaster.pid")) && PortIsOccupied(Port)) { }
            }
        }

        public Task UseCertificate(string certificate) =>
            activeCertificate == certificate ? Task.CompletedTask : Restart(certificate);

        public async Task PasswordAuthentication(bool required)
        {
            await File.WriteAllTextAsync(Path.Combine(Data, "pg_hba.conf"),
                "host all all 127.0.0.1/32 " + (required ? "scram-sha-256" : "trust") + "\n");
            await Restart("valid");
        }

        public async Task Restart(string certificate)
        {
            await Run("pg_ctl", "-D", Data, "-m", "fast", "-w", "stop");
            started = false;
            await Start(certificate, Port);
        }

        public async Task<string> Run(string executable, params string[] arguments)
        {
            // A Windows postgres child can retain pg_ctl's redirected pipe handles after
            // pg_ctl exits. Do not await EOF on those pipes while the server is running.
            var capture = executable != "pg_ctl";
            var start = new ProcessStartInfo(Path.Combine(bin, executable + (OperatingSystem.IsWindows() ? ".exe" : "")))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = capture, RedirectStandardError = capture };
            foreach(var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            var stdout = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var stderr = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            var result = await stdout + await stderr;
            if(process.ExitCode != 0 && executable == "pg_ctl" && File.Exists(Log))
                result += await File.ReadAllTextAsync(Log);
            Assert.True(process.ExitCode == 0, executable + " failed: " + result);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if(started)
                await Run("pg_ctl", "-D", Data, "-m", "fast", "-w", "stop");
            // This generated, absolute temporary directory is the only deletion target.
            if(Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
