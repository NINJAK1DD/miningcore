using Microsoft.AspNetCore.Http;

namespace Miningcore.Api.Middlewares;

public sealed class MetricsMethodPolicyMiddleware
{
    public MetricsMethodPolicyMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    private readonly RequestDelegate next;

    public const string AllowedMethods = "GET, HEAD";

    // Use ordinal comparisons rather than HttpMethods.IsGet/IsHead: RFC 9110
    // method tokens are case-sensitive, while those helpers are not.
    public static bool IsAllowedMethod(string method) =>
        string.Equals(method, HttpMethods.Get, StringComparison.Ordinal) ||
        string.Equals(method, HttpMethods.Head, StringComparison.Ordinal);

    public async Task Invoke(HttpContext context)
    {
        if(ProtectedRouteClassifier.IsMetricsRequest(context.Request.Path) &&
            !IsAllowedMethod(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = AllowedMethods;
            context.Response.ContentLength = 0;
            return;
        }

        await next(context);
    }
}
