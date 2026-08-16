using Microsoft.AspNetCore.Http;
using NLog;
using System.Net;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Prometheus;

namespace Miningcore.Api.Middlewares;

public class IPAccessWhitelistMiddleware
{
    public IPAccessWhitelistMiddleware(RequestDelegate next, string[] locations,
        IPAddress[] whitelist, bool gpdrCompliantLogging,
        TimeProvider timeProvider = null)
    {
        this.whitelist = whitelist;
        this.next = next;
        this.locations = locations;
        this.gpdrCompliantLogging = gpdrCompliantLogging;
        rejectionLogLimiter = new MonotonicLogLimiter(
            TimeSpan.FromMinutes(1), timeProvider);
        rejectionCounter = RejectionCounter.WithLabels(
            ProtectedRouteClassifier.ClassifyWhitelistLocations(locations));
    }

    private readonly RequestDelegate next;
    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IPAddress[] whitelist;
    private readonly string[] locations;
    private readonly bool gpdrCompliantLogging;
    private readonly MonotonicLogLimiter rejectionLogLimiter;
    private readonly ICounter rejectionCounter;
    private static readonly Counter RejectionCounter = Metrics.CreateCounter(
        "miningcore_api_ip_whitelist_rejections_total",
        "Requests rejected by an API source-IP whitelist",
        new CounterConfiguration
        {
            // Fixed values only: admin, metrics or other. Never add a source IP,
            // request path or any other attacker-controlled label here.
            LabelNames = new[] { "route_family" },
        });

    public async Task Invoke(HttpContext context)
    {
        if(locations.Any(location => context.Request.Path.StartsWithSegments(
               new PathString(location), StringComparison.OrdinalIgnoreCase)))
        {
            var remoteAddress = context.Connection.RemoteIpAddress;
            var authorized = remoteAddress != null &&
                whitelist.Any(address => address.IsEqual(remoteAddress));
            if(!authorized)
            {
                // Preserve an alertable aggregate for every rejection even when its
                // informational log entry is suppressed.
                rejectionCounter.Inc();

                // Consume the informational budget independently of the active NLog
                // level so summaries remain coherent if logging is reconfigured. NLog
                // evaluates the message delegate lazily when Info is disabled.
                if(rejectionLogLimiter.TryAcquire(out var suppressed))
                    logger.Info(() => FormatRejection(context, suppressed));
                // Formatting is lazy: normal Info-level operation does no IP
                // censoring, message or delegate allocation for suppressed requests.
                // Debug may be enabled deliberately when per-request forensics matter.
                else if(logger.IsDebugEnabled)
                    logger.Debug(() => FormatRejection(context, 0));

                context.Response.StatusCode = (int) HttpStatusCode.Forbidden;
                await context.Response.WriteAsync("You are not in my access list. Good Bye.\n");
                return;
            }
        }

        await next.Invoke(context);
    }

    private string FormatRejection(HttpContext context, long suppressed)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        var remoteDisplay = remoteAddress != null
            ? remoteAddress.CensorOrReturn(gpdrCompliantLogging).ToString()
            : "unknown";
        var result =
            $"Unauthorized request attempt to {context.Request.Path.Value} from {remoteDisplay}";

        if(suppressed > 0)
            result +=
                $"; {suppressed} other rejection(s), possibly from other sources, were suppressed since the previous informational entry";

        return result;
    }
}
