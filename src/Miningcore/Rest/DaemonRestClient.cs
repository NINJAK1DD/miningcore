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

    public static IHttpClientBuilder AddDaemonRestClient(this IServiceCollection services) =>
        services.AddHttpClient(ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
            });

    public static SimpleRestClient Create(IHttpClientFactory factory, DaemonEndpointConfig endpoint)
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
        var client = factory.CreateClient(ClientName);
        if(endpoint.Http2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        return new SimpleRestClient(client, baseUrl);
    }
}
