using System.Net;
using Miningcore.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Miningcore.Rest;

/// <summary>
/// Transport for daemon REST APIs. Keep redirect policy separate from general HTTP clients.
/// </summary>
public static class DaemonRestClient
{
    public const string ClientName = "daemon-rest";
    // A missing named registration otherwise silently creates the default,
    // redirect-following client. BaseAddress is replaced with the real endpoint
    // before any request can be sent.
    private static readonly Uri registrationMarker = new("http://daemon-rest-policy.invalid/");

    public static IHttpClientBuilder AddDaemonRestClient(this IServiceCollection services)
    {
        services.AddTransient<DaemonRestDiagnosticsHandler>();
        return services.AddHttpClient(ClientName, client => client.BaseAddress = registrationMarker)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<DaemonRestDiagnosticsHandler>()
            // Connection lifetime handles DNS/TLS renewal; keep outage state stable.
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
    }

    public static SimpleRestClient Create(IHttpClientFactory factory, DaemonEndpointConfig endpoint,
        string poolId = null, int? endpointIndex = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(endpoint);
        if(string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65535)
            throw new ArgumentException("Daemon REST endpoint requires a host and port in the range 1–65535", nameof(endpoint));

        var scheme = endpoint.Ssl || endpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        // REST paths are rooted at the daemon API. HttpPath is the JSON-RPC route
        // for some families (for example Conceal's /json_rpc), not a REST prefix.
        var baseUrl = new UriBuilder(scheme, endpoint.Host, endpoint.Port).Uri
            .GetLeftPart(UriPartial.Authority);
        var client = factory.CreateClient(ClientName) ??
            throw new InvalidOperationException("Daemon REST HTTP policy is not registered");
        if(client.BaseAddress != registrationMarker)
            throw new InvalidOperationException("Daemon REST HTTP policy is not registered");
        client.BaseAddress = new Uri(baseUrl + "/");
        return new DaemonPolicyRestClient(client, baseUrl, endpoint.Http2, poolId, endpointIndex);
    }

    // The daemon-specific path also protects direct Send/SendWithResponse calls.
    // General SimpleRestClient callers retain their caller-specified versions.
    private sealed class DaemonPolicyRestClient(HttpClient client, string baseUrl, bool http2,
        string poolId, int? endpointIndex) : SimpleRestClient(client, baseUrl,
            http2 ? HttpVersion.Version20 : null,
            http2 ? HttpVersionPolicy.RequestVersionExact : HttpVersionPolicy.RequestVersionOrLower)
    {
        protected override void PrepareRequest(HttpRequestMessage request,
            IEnumerable<KeyValuePair<string, string>> headers)
        {
            if(http2)
            {
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            request.Options.Set(DaemonRestDiagnosticsHandler.PoolIdOption, poolId);
            request.Options.Set(DaemonRestDiagnosticsHandler.EndpointIndexOption, endpointIndex);
            base.PrepareRequest(request, headers);
        }
    }
}
