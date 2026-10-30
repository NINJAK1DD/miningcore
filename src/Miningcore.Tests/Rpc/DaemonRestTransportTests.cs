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
    public async Task CleartextAndRedirectPolicyApplyToEachDaemonFamily(string family)
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
        services.AddDaemonRestClient();
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
        await Assert.ThrowsAsync<HttpRequestException>(() => client.Get<Result>("status", CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref delivered));
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

    private sealed class Result
    {
        public string Value { get; set; }
    }
}
