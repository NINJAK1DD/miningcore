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
    public const string CacheControlValue = "no-store";
    public const string ContentTypeOptionsHeaderName = "X-Content-Type-Options";
    public const string ContentTypeOptionsHeaderValue = "nosniff";

    public static bool IsProtectedRequest(PathString path) =>
        ProtectedRouteClassifier.IsProtectedRequest(path);

    public async Task Invoke(HttpContext context)
    {
        if(IsProtectedRequest(context.Request.Path))
        {
            // Apply the complete protected-response policy eagerly so downstream
            // code can observe it before response start. OnStarting callbacks run
            // in reverse order; registering this in the first middleware makes its
            // callback run after downstream callbacks and restore every header if a
            // later component attempts to weaken the policy before commit.
            ApplyHeaders(context.Response);
            context.Response.OnStarting(static state =>
            {
                var response = (HttpResponse) state;
                ApplyHeaders(response);
                return Task.CompletedTask;
            }, context.Response);
        }

        await next(context);
    }

    private static void ApplyHeaders(HttpResponse response)
    {
        response.Headers[HeaderName] = HeaderValue;
        response.Headers.CacheControl = CacheControlValue;
        response.Headers[ContentTypeOptionsHeaderName] =
            ContentTypeOptionsHeaderValue;
    }
}
