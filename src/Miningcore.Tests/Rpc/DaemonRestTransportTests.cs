using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Miningcore.Configuration;
using Miningcore.Rest;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class DaemonRestTransportTests
{
    [Theory]
    [InlineData("beam")]
    [InlineData("conceal")]
    [InlineData("warthog")]
    public async Task NamedClientRefusesRedirectsForEachRestPathShape(string family)
    {
        var replayed = 0;
        var target = WebApplication.CreateBuilder();
        target.Logging.ClearProviders();
        target.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var targetApp = target.Build();
        targetApp.Run(context =>
        {
            Interlocked.Increment(ref replayed);
            return context.Response.WriteAsync("{\"value\":\"replayed\"}");
        });
        await targetApp.StartAsync();

        var destination = Address(targetApp);
        var source = WebApplication.CreateBuilder();
        source.Logging.ClearProviders();
        source.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var sourceApp = source.Build();
        sourceApp.Run(async context =>
        {
            Assert.Equal("synthetic-secret", context.Request.Headers["X-Synthetic"].ToString());
            if(context.Request.Path == "/ok")
            {
                await context.Response.WriteAsync("{\"value\":\"verified\"}");
                return;
            }
            context.Response.StatusCode = int.Parse(context.Request.Path.Value!.Trim('/'));
            context.Response.Headers.Location = destination + "/capture";
        });
        await sourceApp.StartAsync();

        var address = new Uri(Address(sourceApp));
        var endpoint = new DaemonEndpointConfig
        {
            Host = address.Host, Port = address.Port,
            HttpPath = family == "conceal" ? "/json_rpc" : null,
        };
        var services = new ServiceCollection();
        Miningcore.Program.AddHttpClients(services);
        using var provider = services.BuildServiceProvider();
        var client = DaemonRestClient.Create(provider.GetRequiredService<IHttpClientFactory>(), endpoint);
        var headers = new Dictionary<string, string> { ["X-Synthetic"] = "synthetic-secret" };
        Assert.Equal("verified", (await client.Get<Result>("ok", CancellationToken.None, headers: headers)).Value);

        foreach(var status in new[] { 301, 302, 303, 307, 308 })
        {
            var get = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.Get<Result>($"{status}?wallet=synthetic-secret", CancellationToken.None, headers: headers));
            Assert.Equal((HttpStatusCode) status, get.StatusCode);
            var post = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.Post<Result>($"{status}", new { wallet = "synthetic-secret" }, CancellationToken.None,
                    headers: headers));
            Assert.Equal((HttpStatusCode) status, post.StatusCode);
        }
        Assert.Equal(0, Volatile.Read(ref replayed));
    }

    [Fact]
    public void InvalidDaemonAddressFailsBeforeAnyRequest()
    {
        var services = new ServiceCollection();
        services.AddDaemonRestClient();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        Assert.Throws<ArgumentException>(() => DaemonRestClient.Create(factory,
            new DaemonEndpointConfig { Host = "localhost", Port = 0, Ssl = true }));
        Assert.Throws<ArgumentException>(() => DaemonRestClient.Create(factory,
            new DaemonEndpointConfig { Host = "", Port = 8080 }));
        Assert.Throws<ArgumentException>(() => DaemonRestClient.Create(factory,
            new DaemonEndpointConfig { Host = "localhost", Port = 65536 }));
    }

    [Fact]
    public void MissingNamedPolicyFailsClosed()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => DaemonRestClient.Create(
            provider.GetRequiredService<IHttpClientFactory>(),
            new DaemonEndpointConfig { Host = "localhost", Port = 8080 }));
        Assert.Equal("Daemon REST HTTP policy is not registered", ex.Message);
    }

    [Fact]
    public async Task GeneratedRequestsUseExplicitVersionWithoutOverwritingCallerRequest()
    {
        var versions = new List<(Version Version, HttpVersionPolicy Policy)>();
        using var client = new HttpClient(new RecordingVersionHandler(versions));
        var rest = new SimpleRestClient(client, "https://example.invalid",
            HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact);

        await rest.Get<object>("status", CancellationToken.None);
        using var callerRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/status")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        await rest.Send<object>(callerRequest, CancellationToken.None);

        Assert.Equal((HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact), versions[0]);
        Assert.Equal((HttpVersion.Version11, HttpVersionPolicy.RequestVersionOrLower), versions[1]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HttpsRequestedAgainstHttpOnlyDaemonFailsWithoutFallback(bool ssl, bool http2)
    {
        var delivered = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(context =>
        {
            Interlocked.Increment(ref delivered);
            return context.Response.WriteAsync("{\"value\":\"unexpected\"}");
        });
        await app.StartAsync();

        var address = new Uri(Address(app));
        var services = new ServiceCollection();
        services.AddDaemonRestClient();
        using var provider = services.BuildServiceProvider();
        var client = DaemonRestClient.Create(provider.GetRequiredService<IHttpClientFactory>(),
            new DaemonEndpointConfig { Host = address.Host, Port = address.Port, Ssl = ssl, Http2 = http2 });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.Get<Result>("status", CancellationToken.None));
        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
        Assert.Equal(0, Volatile.Read(ref delivered));
    }

    [Fact]
    public async Task CookiesFromOneDaemonAreNotSentToAnotherPort()
    {
        var first = WebApplication.CreateBuilder();
        first.Logging.ClearProviders();
        first.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var firstApp = first.Build();
        firstApp.Run(context =>
        {
            context.Response.Headers.SetCookie = "daemon=synthetic-secret; Path=/";
            return context.Response.WriteAsync("{\"value\":\"first\"}");
        });
        await firstApp.StartAsync();

        string observedCookie = null;
        var second = WebApplication.CreateBuilder();
        second.Logging.ClearProviders();
        second.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var secondApp = second.Build();
        secondApp.Run(context =>
        {
            observedCookie = context.Request.Headers.Cookie.ToString();
            return context.Response.WriteAsync("{\"value\":\"second\"}");
        });
        await secondApp.StartAsync();

        var services = new ServiceCollection();
        Miningcore.Program.AddHttpClients(services);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        static DaemonEndpointConfig Endpoint(string address)
        {
            var uri = new Uri(address);
            return new DaemonEndpointConfig { Host = uri.Host, Port = uri.Port };
        }
        Assert.Equal("first", (await DaemonRestClient.Create(factory, Endpoint(Address(firstApp)))
            .Get<Result>("status", CancellationToken.None)).Value);
        Assert.Equal("second", (await DaemonRestClient.Create(factory, Endpoint(Address(secondApp)))
            .Get<Result>("status", CancellationToken.None)).Value);
        Assert.True(string.IsNullOrEmpty(observedCookie), observedCookie);
    }

    [Fact]
    public async Task DiagnosticsWarnOncePerOutageAndNeverIncludeWalletPath()
    {
        var logger = new WarningRecorder();
        using var client = new HttpClient(new DaemonRestDiagnosticsHandler(logger)
        {
            InnerHandler = new DiagnosticSequenceHandler(),
        });
        const string url = "https://user:synthetic-credential@127.0.0.1:1234/wallet/synthetic-secret";
        for(var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));
        using var recovered = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.Equal(4, logger.Warnings.Count);
        Assert.Equal(2, logger.Warnings.Count(x => x.Contains("tls-handshake", StringComparison.Ordinal)));
        Assert.Equal(2, logger.Warnings.Count(x => x.Contains("redirect refused", StringComparison.Ordinal)));
        Assert.All(logger.Warnings, warning => Assert.DoesNotContain("synthetic-secret", warning));
        Assert.All(logger.Warnings, warning => Assert.DoesNotContain("synthetic-credential", warning));
    }

    [Fact]
    public async Task Http2NegotiationWarningRearmsAfterResponse()
    {
        var logger = new WarningRecorder();
        using var client = new HttpClient(new DaemonRestDiagnosticsHandler(logger)
        {
            InnerHandler = new Http2SequenceHandler(),
        });
        for(var i = 0; i < 4; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:1234/status")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            if(i == 2)
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            else
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        }
        Assert.Equal(2, logger.Warnings.Count);
        Assert.All(logger.Warnings, warning => Assert.Contains("http2-negotiation", warning));
    }

    private sealed class DiagnosticSequenceHandler : HttpMessageHandler
    {
        private int calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref calls);
            if(call is 1 or 2 or 4)
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic-secret");
            return Task.FromResult(new HttpResponseMessage(call is 3 or 6
                ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK));
        }
    }

    private sealed class RecordingVersionHandler(List<(Version Version, HttpVersionPolicy Policy)> versions) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            versions.Add((request.Version, request.VersionPolicy));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class Http2SequenceHandler : HttpMessageHandler
    {
        private int calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if(Interlocked.Increment(ref calls) != 3)
                throw new HttpRequestException(HttpRequestError.VersionNegotiationError,
                    "synthetic-secret");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class WarningRecorder : ILogger<DaemonRestDiagnosticsHandler>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if(IsEnabled(logLevel)) Warnings.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Result
    {
        public string Value { get; set; }
    }
}
