using Microsoft.AspNetCore.Http;
using NLog;
using System.Net;
using Miningcore.Configuration;
using Miningcore.Extensions;

namespace Miningcore.Api.Middlewares;

public class IPAccessWhitelistMiddleware
{
    public IPAccessWhitelistMiddleware(RequestDelegate next, string[] locations, IPAddress[] whitelist, bool gpdrCompliantLogging)
        : this(next, locations, whitelist, gpdrCompliantLogging,
            TimeProvider.System)
    {
    }

    public IPAccessWhitelistMiddleware(RequestDelegate next, string[] locations,
        IPAddress[] whitelist, bool gpdrCompliantLogging,
        TimeProvider timeProvider)
    {
        this.whitelist = whitelist;
        this.next = next;
        this.locations = locations;
        this.gpdrCompliantLogging = gpdrCompliantLogging;
        rejectionLogLimiter = new MonotonicLogLimiter(
            TimeSpan.FromMinutes(1), timeProvider);
    }

    private readonly RequestDelegate next;
    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private readonly IPAddress[] whitelist;
    private readonly string[] locations;
    private readonly bool gpdrCompliantLogging;
    private readonly MonotonicLogLimiter rejectionLogLimiter;

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
                if(rejectionLogLimiter.TryAcquire(out var suppressed))
                    logger.Info(() => FormatRejection(context, suppressed));
                else if(logger.IsDebugEnabled)
                    // Formatting is lazy: normal Info-level operation does no IP
                    // censoring, message or delegate allocation for suppressed
                    // requests. Debug may be enabled deliberately when per-request
                    // forensics matter.
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
