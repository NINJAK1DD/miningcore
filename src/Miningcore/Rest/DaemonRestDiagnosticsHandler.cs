using System.Collections.Concurrent;
using System.Net;
using Miningcore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Miningcore.Rest;

/// <summary>Bounded, URL-safe diagnostics for daemon REST transport outages.</summary>
public sealed class DaemonRestDiagnosticsHandler(ILogger<DaemonRestDiagnosticsHandler> logger) : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, byte> transportOutages = new();
    private readonly ConcurrentDictionary<string, byte> redirectOutages = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Do not log the path or query: some daemon requests contain wallet data.
        // UriPartial.Authority includes userinfo. SchemeAndServer excludes it.
        var authority = request.RequestUri?.GetComponents(UriComponents.SchemeAndServer,
            UriFormat.SafeUnescaped) ?? "unknown";
        try
        {
            var response = await base.SendAsync(request, ct);
            // Receiving any HTTP response ends a TLS/version-negotiation outage.
            transportOutages.TryRemove(authority, out _);

            if((int)response.StatusCode is >= 300 and <= 399)
            {
                if(redirectOutages.TryAdd(authority, 0))
                    logger.LogWarning("Daemon REST redirect refused for {Authority}; status {StatusCode}",
                        authority, (int)response.StatusCode);
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("Daemon REST redirect was refused", null, status);
            }

            redirectOutages.TryRemove(authority, out _);
            return response;
        }
        catch(HttpRequestException ex) when(DiagnosticFailure.Category(ex) == "tls-handshake" ||
            request.Version.Major >= 2 && ex.HttpRequestError == HttpRequestError.VersionNegotiationError)
        {
            var category = DiagnosticFailure.Category(ex) == "tls-handshake"
                ? "tls-handshake" : "http2-negotiation";
            if(transportOutages.TryAdd(authority, 0))
                logger.LogWarning("Daemon REST transport failure for {Authority}: {Category}", authority, category);
            throw;
        }
    }
}
