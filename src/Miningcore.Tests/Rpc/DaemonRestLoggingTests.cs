using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Rest;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests.Rpc;

[Collection(RpcDiagnosticCollection.Name)]
public class DaemonRestLoggingTests
{
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
}
