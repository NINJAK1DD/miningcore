using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
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

[Collection(PostgresPolicyCollection.Name)]
public class PostgresTlsIntegrationTests
{
    private readonly ITestOutputHelper output;
    public PostgresTlsIntegrationTests(ITestOutputHelper output) => this.output = output;

    [PostgresTlsFact]
    public async Task RealServerEnforcesCertificatePolicyWithoutDowngrade()
    {
        await using var server = new TlsServer();
        await server.Initialize();
        output.WriteLine(await server.Run("postgres", "--version"));
        output.WriteLine("Npgsql " + typeof(NpgsqlConnection).Assembly.GetName().Version);

        foreach(var mode in new[] { PostgresSslMode.VerifyCA, PostgresSslMode.VerifyFull })
        {
            await Connect(server, mode, "localhost", server.Root, true);
            await Connect(server, mode, "localhost", server.OtherRoot, false);
        }
        await Connect(server, PostgresSslMode.VerifyCA, "127.0.0.1", server.Root, true);
        await Connect(server, PostgresSslMode.VerifyFull, "127.0.0.1", server.Root, false);
        // Require deliberately retains encryption without authenticating this server.
        await Connect(server, PostgresSslMode.Require, "127.0.0.1", null, true);

        var priorRoot = Environment.GetEnvironmentVariable("PGSSLROOTCERT");
        try
        {
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", server.Root);
            await Connect(server, PostgresSslMode.VerifyFull, "localhost", null, true);
            Environment.SetEnvironmentVariable("PGSSLROOTCERT", server.OtherRoot);
            await Connect(server, PostgresSslMode.VerifyFull, "localhost", null, false);
            await Connect(server, PostgresSslMode.VerifyFull, "localhost", server.Root, true);
        }
        finally { Environment.SetEnvironmentVariable("PGSSLROOTCERT", priorRoot); }

        // Explicit bad CA files cannot silently fall back to another trust source.
        var missing = server.Root + ".missing";
        var invalid = server.Root + ".invalid";
        await File.WriteAllTextAsync(invalid, "not a certificate");
        await Connect(server, PostgresSslMode.VerifyFull, "localhost", missing, false, certificateFileFailure: true);
        await Connect(server, PostgresSslMode.VerifyFull, "localhost", invalid, false, certificateFileFailure: true);

        var factoryConfig = PostgresConnectionPolicyTests.Config();
        factoryConfig.Database = "postgres";
        factoryConfig.Port = server.Port;
        factoryConfig.SslMode = PostgresSslMode.VerifyFull;
        factoryConfig.TlsRootCert = missing;
        var failedFactory = new PgConnectionFactory(PostgresConnectionPolicy.Build(factoryConfig).ConnectionString);
        var safeError = await Assert.ThrowsAsync<PostgresConnectionException>(() => failedFactory.OpenConnectionAsync());
        Assert.Null(safeError.InnerException);
        Assert.DoesNotContain(missing, safeError.ToString());
        Assert.DoesNotContain(server.Root, safeError.ToString());
        // Npgsql classifies the nested missing-file IOException as transient.
        // Redacting diagnostics must preserve that existing retry classification.
        Assert.True(safeError.IsTransient);
        using(var cancelled = new System.Threading.CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failedFactory.OpenConnectionAsync(cancelled.Token));
        }
        factoryConfig.TlsRootCert = server.Root;
        using(var opened = await new PgConnectionFactory(PostgresConnectionPolicy.Build(factoryConfig).ConnectionString)
                  .OpenConnectionAsync())
            Assert.Equal(System.Data.ConnectionState.Open, opened.State);

        await server.Restart("expired");
        foreach(var mode in new[] { PostgresSslMode.VerifyCA, PostgresSslMode.VerifyFull })
            await Connect(server, mode, "localhost", server.Root, false);
        await Connect(server, PostgresSslMode.Require, "localhost", null, true);

        await server.Restart("off");
        foreach(var mode in new[] { PostgresSslMode.VerifyCA, PostgresSslMode.VerifyFull, PostgresSslMode.Require })
            await Connect(server, mode, "localhost", mode == PostgresSslMode.Require ? null : server.Root,
                false, tlsUnavailable: true);
        await Connect(server, PostgresSslMode.Prefer, "localhost", null, true, tlsUnavailable: true);
        await Connect(server, PostgresSslMode.Disable, "localhost", null, true, tlsUnavailable: true);
    }

    private async Task Connect(TlsServer server, PostgresSslMode mode, string host, string root,
        bool succeeds, bool tlsUnavailable = false, bool certificateFileFailure = false)
    {
        var config = PostgresConnectionPolicyTests.Config();
        config.Database = "postgres";
        config.Host = host;
        config.Port = server.Port;
        config.SslMode = mode;
        config.TlsRootCert = root;
        var builder = PostgresConnectionPolicy.Build(config);
        builder.Pooling = false; // Every assertion must perform a fresh TLS handshake.
        builder.Timeout = 5;
        builder.SslNegotiation = SslNegotiation.Postgres;
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        if(succeeds)
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "select ssl from pg_stat_ssl where pid = pg_backend_pid()", connection);
            Assert.Equal(!tlsUnavailable, (bool) await command.ExecuteScalarAsync());
        }
        else
        {
            var error = await Record.ExceptionAsync(() => connection.OpenAsync());
            Assert.NotNull(error);
            if(tlsUnavailable)
            {
                Assert.IsType<NpgsqlException>(error);
                Assert.Contains("No SSL enabled", error.Message);
            }
            else if(certificateFileFailure)
                Assert.True(Contains<IOException>(error) || Contains<CryptographicException>(error),
                    "Expected failure loading the explicitly configured CA file");
            else
                Assert.True(Contains<AuthenticationException>(error),
                    "Expected a certificate authentication failure, not a connection or SQL failure");
        }
        output.WriteLine($"PASS {mode}, host={host}, TLS unavailable={tlsUnavailable}, connection accepted={succeeds}");
    }

    private static bool Contains<T>(Exception error) where T : Exception =>
        error is T || error.InnerException != null && Contains<T>(error.InnerException);

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly string bin = Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES_BIN");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "miningcore-pg-tls-" + Guid.NewGuid().ToString("N"));
        private string Data => Path.Combine(directory, "data");
        public string Root => Path.Combine(directory, "root;quoted=ca.crt");
        public string OtherRoot => Path.Combine(directory, "other.crt");
        public int Port { get; private set; }
        private bool started;

        public async Task Initialize()
        {
            Directory.CreateDirectory(directory);
            using(var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                Port = ((IPEndPoint) listener.LocalEndpoint).Port;
            }
            await Run("initdb", "-D", Data, "-U", "miningcore", "--auth=trust", "--encoding=UTF8", "--no-locale");
            using var rootKey = RSA.Create(2048);
            using var ca = CreateCa(rootKey, "Miningcore ephemeral TLS test CA");
            await File.WriteAllTextAsync(Root, ca.ExportCertificatePem());
            using var otherKey = RSA.Create(2048);
            using var otherCa = CreateCa(otherKey, "Miningcore untrusted ephemeral CA");
            await File.WriteAllTextAsync(OtherRoot, otherCa.ExportCertificatePem());
            CreateServerCertificate(ca, "valid", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            CreateServerCertificate(ca, "expired", DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
            await Start("valid");
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

        private async Task Start(string certificate)
        {
            await File.WriteAllTextAsync(Path.Combine(Data, "postgresql.conf"),
                $"listen_addresses='127.0.0.1'\nport={Port}\n" +
                (certificate == "off" ? "ssl=off\n" :
                    $"ssl=on\nssl_cert_file='{certificate}.crt'\nssl_key_file='{certificate}.key'\n"));
            await Run("pg_ctl", "-D", Data, "-l", Path.Combine(directory, "postgres.log"), "-w", "start");
            started = true;
        }

        public async Task Restart(string certificate)
        {
            await Run("pg_ctl", "-D", Data, "-m", "fast", "-w", "stop");
            started = false;
            await Start(certificate);
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
