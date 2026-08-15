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
                var remoteDisplay = remoteAddress != null
                    ? remoteAddress.CensorOrReturn(gpdrCompliantLogging).ToString()
                    : "unknown";
                if(rejectionLogLimiter.TryAcquire(out var suppressed))
                    logger.Info(() => FormatRejection(context, remoteDisplay,
                        suppressed));

                context.Response.StatusCode = (int) HttpStatusCode.Forbidden;
                await context.Response.WriteAsync("You are not in my access list. Good Bye.\n");
                return;
            }
        }

        await next.Invoke(context);
    }

    private static string FormatRejection(HttpContext context,
        string remoteDisplay, long suppressed)
    {
        var result =
            $"Unauthorized request attempt to {context.Request.Path.Value} from {remoteDisplay}";

        if(suppressed > 0)
            result +=
                $"; {suppressed} additional rejection(s) occurred after the previous informational entry and were suppressed";

        return result;
    }
}
