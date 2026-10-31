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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http;
using Miningcore.Configuration;
using Miningcore.Rest;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests.Rpc;

[Collection(RpcDiagnosticCollection.Name)]
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
    public void NullFactoryClientFailsWithPolicyError()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(DaemonRestClient.ClientName).Returns((HttpClient)null);
        var ex = Assert.Throws<InvalidOperationException>(() => DaemonRestClient.Create(factory,
            new DaemonEndpointConfig { Host = "localhost", Port = 8080 }));
        Assert.Equal("Daemon REST HTTP policy is not registered", ex.Message);
    }

    [Fact]
    public void NamedPolicyKeepsHandlerAcrossPayoutCycles()
    {
        var services = new ServiceCollection();
        services.AddDaemonRestClient();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(DaemonRestClient.ClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
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

    [Fact]
    public async Task DaemonDirectSendCannotOverrideRequiredHttp2()
    {
        var versions = new List<(Version Version, HttpVersionPolicy Policy)>();
        var services = new ServiceCollection();
        services.AddDaemonRestClient()
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingVersionHandler(versions));
        using var provider = services.BuildServiceProvider();
        var rest = DaemonRestClient.Create(provider.GetRequiredService<IHttpClientFactory>(),
            new DaemonEndpointConfig { Host = "example.invalid", Port = 443, Http2 = true });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/status")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        await rest.Send<object>(request, CancellationToken.None);
        using var responseRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/status")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        using var response = await rest.SendWithResponse<object>(responseRequest, CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.All(versions, version =>
            Assert.Equal((HttpVersion.Version20, HttpVersionPolicy.RequestVersionExact), version));
    }

    [Fact]
    public void RestDiagnosticRejectsUnreviewedFailureLabels()
    {
        using var logs = new RestLogCapture();
        RpcDiagnostics.WriteRest("synthetic-secret", "pool-1", 2);
        var record = JObject.Parse(Assert.Single(logs.Warnings)["RPC diagnostic ".Length..]);
        Assert.Equal("other", record.Value<string>("failure"));
        Assert.Equal(JTokenType.Null, record["method"].Type);
        Assert.DoesNotContain("synthetic-secret", logs.Warnings[0]);
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
        using var logs = new RestLogCapture();
        using var client = new HttpClient(new DaemonRestDiagnosticsHandler
        {
            InnerHandler = new DiagnosticSequenceHandler(),
        });
        const string url = "https://user:synthetic-credential@127.0.0.1:1234/wallet/synthetic-secret";
        async Task<HttpResponseMessage> SendTagged()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Options.Set(DaemonRestDiagnosticsHandler.PoolIdOption, "pool-1");
            request.Options.Set(DaemonRestDiagnosticsHandler.EndpointIndexOption, 2);
            return await client.SendAsync(request);
        }
        for(var i = 0; i < 4; i++)
            await Assert.ThrowsAsync<HttpRequestException>(SendTagged);
        using var recovered = await SendTagged();
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(SendTagged);

        Assert.Equal(4, logs.Warnings.Count);
        Assert.Equal(2, logs.Warnings.Count(x => x.Contains("tls-handshake", StringComparison.Ordinal)));
        Assert.Equal(2, logs.Warnings.Count(x => x.Contains("redirect-refused", StringComparison.Ordinal)));
        foreach(var warning in logs.Warnings)
        {
            Assert.StartsWith("RPC diagnostic ", warning);
            var record = JObject.Parse(warning["RPC diagnostic ".Length..]);
            Assert.Equal("Rest", record.Value<string>("transport"));
            Assert.Equal("Failure", record.Value<string>("stage"));
            Assert.Equal(JTokenType.Null, record["method"].Type);
            Assert.Equal("pool-1", record.Value<string>("poolId"));
            Assert.Equal(2, record.Value<int>("endpointIndex"));
            if(record.Value<string>("failure") == "redirect-refused")
                Assert.Equal(307, record.Value<int>("httpStatus"));
        }
        Assert.All(logs.Warnings, warning => Assert.DoesNotContain("synthetic-secret", warning));
        Assert.All(logs.Warnings, warning => Assert.DoesNotContain("synthetic-credential", warning));
        Assert.All(logs.Warnings, warning => Assert.DoesNotContain("127.0.0.1", warning));
        Assert.All(logs.Warnings, warning => Assert.DoesNotContain("1234", warning));
    }

    [Fact]
    public async Task Http2NegotiationWarningRearmsAfterResponse()
    {
        using var logs = new RestLogCapture();
        using var client = new HttpClient(new DaemonRestDiagnosticsHandler
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
            request.Options.Set(DaemonRestDiagnosticsHandler.PoolIdOption, "pool-1");
            request.Options.Set(DaemonRestDiagnosticsHandler.EndpointIndexOption, 2);
            if(i == 2)
            {
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            else
                await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        }
        Assert.Equal(2, logs.Warnings.Count);
        Assert.All(logs.Warnings, warning => Assert.Contains("http2-negotiation", warning));
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

    private sealed class RestLogCapture : IDisposable
    {
        private readonly LoggingConfiguration previous = NLog.LogManager.Configuration;
        private readonly MemoryTarget target = new() { Layout = "${message}" };

        public RestLogCapture()
        {
            var config = new LoggingConfiguration();
            config.AddRule(NLog.LogLevel.Warn, NLog.LogLevel.Fatal, target, "pool-1");
            NLog.LogManager.Configuration = config;
        }

        public IList<string> Warnings => target.Logs;

        public void Dispose()
        {
            NLog.LogManager.Configuration = previous;
            target.Dispose();
        }
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Result
    {
        public string Value { get; set; }
    }
}
