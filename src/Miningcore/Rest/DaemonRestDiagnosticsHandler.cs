using System.Collections.Concurrent;
using System.Net;
using Miningcore.Diagnostics;
using Miningcore.Rpc;
using Microsoft.Extensions.Logging;

namespace Miningcore.Rest;

/// <summary>Bounded, URL-safe diagnostics for daemon REST transport outages.</summary>
public sealed class DaemonRestDiagnosticsHandler(ILogger<DaemonRestDiagnosticsHandler> logger) : DelegatingHandler
{
    internal static readonly HttpRequestOptionsKey<string> PoolIdOption = new("daemon-rest-pool-id");
    internal static readonly HttpRequestOptionsKey<int?> EndpointIndexOption = new("daemon-rest-endpoint-index");
    private readonly ConcurrentDictionary<(string Authority, string PoolId, int? EndpointIndex), byte>
        transportOutages = new();
    private readonly ConcurrentDictionary<(string Authority, string PoolId, int? EndpointIndex), byte>
        redirectOutages = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Authority is an internal outage key only. It must never reach a log.
        var authority = request.RequestUri?.GetComponents(UriComponents.SchemeAndServer,
            UriFormat.SafeUnescaped) ?? "unknown";
        request.Options.TryGetValue(PoolIdOption, out var poolId);
        request.Options.TryGetValue(EndpointIndexOption, out var endpointIndex);
        var outage = (authority, poolId, endpointIndex);
        try
        {
            var response = await base.SendAsync(request, ct);
            // Receiving any HTTP response ends a TLS/version-negotiation outage.
            transportOutages.TryRemove(outage, out _);

            if((int)response.StatusCode is >= 300 and <= 399)
            {
                if(redirectOutages.TryAdd(outage, 0))
                    RpcDiagnostics.WriteRest(logger, "redirect-refused", poolId, endpointIndex,
                        (int)response.StatusCode);
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("Daemon REST redirect was refused", null, status);
            }

            redirectOutages.TryRemove(outage, out _);
            return response;
        }
        catch(HttpRequestException ex) when(DiagnosticFailure.Category(ex) == "tls-handshake" ||
            request.Version.Major >= 2 && ex.HttpRequestError == HttpRequestError.VersionNegotiationError)
        {
            var category = DiagnosticFailure.Category(ex) == "tls-handshake"
                ? "tls-handshake" : "http2-negotiation";
            if(transportOutages.TryAdd(outage, 0))
                RpcDiagnostics.WriteRest(logger, category, poolId, endpointIndex);
            throw;
        }
    }
}
