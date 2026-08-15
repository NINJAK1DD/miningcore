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

    public static bool IsAllowedMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    public async Task Invoke(HttpContext context)
    {
        if(Program.IsMetricsRequest(context.Request.Path) &&
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
