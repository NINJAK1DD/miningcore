using Microsoft.AspNetCore.Http;

namespace Miningcore.Api.Middlewares;

public sealed class ProtectedRouteResourcePolicyMiddleware
{
    public ProtectedRouteResourcePolicyMiddleware(RequestDelegate next)
    {
        this.next = next;
    }

    private readonly RequestDelegate next;

    public const string HeaderName = "Cross-Origin-Resource-Policy";
    public const string HeaderValue = "same-origin";

    public static bool IsProtectedRequest(PathString path) =>
        AdminApiAuthenticationMiddleware.IsAdminRequest(path) ||
        Program.IsMetricsRequest(path);

    public async Task Invoke(HttpContext context)
    {
        if(IsProtectedRequest(context.Request.Path))
        {
            // Apply this before every other API middleware so terminal listener,
            // whitelist, authentication and exception responses receive the same
            // browser resource policy. OnStarting callbacks run in reverse order;
            // registering this in the first middleware makes its callback run after
            // downstream callbacks and restore the boundary immediately before the
            // headers are committed.
            context.Response.Headers[HeaderName] = HeaderValue;
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse) state;
                response.Headers[HeaderName] = HeaderValue;
                return Task.CompletedTask;
            }, context.Response);
        }

        await next(context);
    }
}
