using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Xunit;

namespace Miningcore.Tests.Persistence.Postgres;

// One loopback-only cluster per PostgreSQL policy collection. Tests restore their
// mutable server state and dispose their own connections before another test runs.
public sealed class IsolatedPostgresServer : IAsyncDisposable, IAsyncLifetime
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
