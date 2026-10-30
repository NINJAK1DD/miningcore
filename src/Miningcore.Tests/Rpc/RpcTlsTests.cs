using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class RpcTlsTests
{
    private sealed class LinuxTrustTheoryAttribute : TheoryAttribute
    {
        public LinuxTrustTheoryAttribute()
        {
            if(!OperatingSystem.IsLinux())
                Skip = "Uses OpenSSL per-process CA files; never modifies machine/user trust stores";
        }
    }

    private sealed class NonLinuxTrustTheoryAttribute : TheoryAttribute
    {
        public NonLinuxTrustTheoryAttribute()
        {
            if(OperatingSystem.IsLinux())
                Skip = "The full isolated CA matrix runs on Linux";
        }
    }

    public static IEnumerable<object[]> Cases =>
        from transport in new[] { "https", "http2", "batch", "wss", "ergo", "alephium",
            "rest-beam", "rest-conceal", "rest-warthog", "rest-http2" }
        from certificate in new[] { "valid", "untrusted", "expired", "wrong-name", "dns-valid", "dns-ip-only" }
        select new object[] { transport, certificate };

    [LinuxTrustTheory]
    [MemberData(nameof(Cases))]
    public async Task ProductionTransportsVerifyCertificateBeforeSendingRpc(string transport, string certificate)
        => await VerifyCertificateBeforeSendingRpc(transport, certificate);

    [NonLinuxTrustTheory]
    [InlineData("https")]
    [InlineData("wss")]
    [InlineData("ergo")]
    [InlineData("alephium")]
    [InlineData("rest-beam")]
    [InlineData("rest-conceal")]
    [InlineData("rest-warthog")]
    public async Task PlatformRejectsUntrustedDaemonCertificate(string transport)
        => await VerifyCertificateBeforeSendingRpc(transport, "untrusted");

    private static async Task VerifyCertificateBeforeSendingRpc(string transport, string certificate)
    {
        var directory = Path.Combine(Path.GetTempPath(), "miningcore-rpc-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var caKey = RSA.Create(2048);
            using var ca = CreateCa(caKey);
            using var otherKey = RSA.Create(2048);
            using var otherCa = CreateCa(otherKey);
            using var serverKey = RSA.Create(2048);
            using var serverCert = CreateServer(ca, serverKey, certificate);
            var requests = 0;
            var handlerFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener =>
            {
                listener.Protocols = HttpProtocols.Http1AndHttp2;
                listener.UseHttps(serverCert);
            }));
            await using var app = builder.Build();
            app.UseWebSockets();
            app.Run(async context =>
            {
                try
                {
                    Interlocked.Increment(ref requests);
                    if(transport == "wss")
                    {
                        using var socket = await context.WebSockets.AcceptWebSocketAsync();
                        var buffer = new byte[4096];
                        await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("verified")),
                            WebSocketMessageType.Text, true, context.RequestAborted);
                        return;
                    }
                    if(transport.StartsWith("rest-", StringComparison.Ordinal))
                    {
                        Assert.Equal(transport switch
                        {
                            "rest-conceal" => "/getinfo",
                            "rest-warthog" => "/chain/info",
                            _ => "/status",
                        }, context.Request.Path.Value);
                        Assert.Equal("synthetic-secret", context.Request.Headers["X-Synthetic"].ToString());
                        if(transport == "rest-http2") Assert.Equal("HTTP/2", context.Request.Protocol);
                        await context.Response.WriteAsync("{\"value\":\"verified\"}");
                        return;
                    }
                    Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:synthetic-secret")),
                        context.Request.Headers.Authorization.ToString());
                    if(transport == "ergo")
                        Assert.Equal("synthetic-api-key", context.Request.Headers["api_key"].ToString());
                    if(transport == "alephium")
                        Assert.Equal("synthetic-api-key", context.Request.Headers["X-API-KEY"].ToString());
                    if(transport == "http2") Assert.Equal("HTTP/2", context.Request.Protocol);
                    if(transport is "ergo" or "alephium")
                    {
                        await context.Response.WriteAsync("{}");
                        return;
                    }
                    using var reader = new StreamReader(context.Request.Body);
                    var request = JToken.Parse(await reader.ReadToEndAsync());
                    static JObject Response(JToken item) => new() { ["id"] = item["id"], ["result"] = "verified" };
                    JToken response = request is JArray batch ? new JArray(batch.Select(Response)) : Response(request);
                    await context.Response.WriteAsync(response.ToString(Formatting.None));
                }
                catch(Exception ex)
                {
                    handlerFailure.TrySetResult(ex);
                    throw;
                }
            });
            await app.StartAsync();
            try
            {
                var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.Single());
                var endpoint = new DaemonEndpointConfig
                {
                    Host = certificate.StartsWith("dns-", StringComparison.Ordinal) ? "localhost" : address.Host,
                    Port = address.Port, Ssl = transport is not ("http2" or "rest-http2"),
                    Http2 = transport is "http2" or "rest-http2",
                    User = "fixture", Password = "synthetic-secret",
                    HttpPath = transport == "rest-conceal" ? "/json_rpc" : null,
                    Extra = new Dictionary<string, object> { ["apiKey"] = "synthetic-api-key" },
                };
                var config = Path.Combine(directory, "endpoint.json");
                var trust = Path.Combine(directory, "ca.pem");
                var emptyTrust = Path.Combine(directory, "empty-ca-dir");
                Directory.CreateDirectory(emptyTrust);
                await File.WriteAllTextAsync(config, JsonConvert.SerializeObject(endpoint));
                await File.WriteAllTextAsync(trust, (certificate == "untrusted" ? otherCa : ca).ExportCertificatePem());
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                foreach(var argument in new[]
                {
                    "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.runtimeconfig.json"),
                    "--depsfile", Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.deps.json"),
                    Path.Combine(AppContext.BaseDirectory, "Miningcore.Tests.ProcessHost.dll"), "rpc-tls", config, transport,
                }) start.ArgumentList.Add(argument);
                start.Environment["SSL_CERT_FILE"] = trust;
                start.Environment["SSL_CERT_DIR"] = emptyTrust;
                start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
                using var process = Process.Start(start);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally
                {
                    if(!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                var accepted = certificate is "valid" or "dns-valid";
                Assert.False(handlerFailure.Task.IsCompleted, handlerFailure.Task.IsCompleted ? handlerFailure.Task.Result.ToString() : null);
                Assert.True(process.ExitCode == (accepted ? 0 : 20), $"TLS probe exit {process.ExitCode}: {await stdout}\n{await stderr}");
                Assert.Equal(accepted ? "accepted" : "rejected", (await stdout).Trim());
                Assert.Equal(accepted ? 1 : 0, Volatile.Read(ref requests));
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stop.Token);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static X509Certificate2 CreateCa(RSA key)
    {
        var request = new CertificateRequest("CN=Miningcore ephemeral RPC CA " + Guid.NewGuid().ToString("N"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(10));
    }

    private static X509Certificate2 CreateServer(X509Certificate2 ca, RSA key, string kind)
    {
        // Keep the common name unrelated to the endpoint: some TLS stacks
        // still fall back to CN even when a certificate has only an IP SAN.
        var request = new CertificateRequest("CN=rpc-test.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        if(kind == "wrong-name") san.AddDnsName("wrong-name.invalid");
        else if(kind == "dns-valid") san.AddDnsName("localhost");
        else san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-3),
            DateTimeOffset.UtcNow.AddDays(kind == "expired" ? -2 : 2), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }
}
