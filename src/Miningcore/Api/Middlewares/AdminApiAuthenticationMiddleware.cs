using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Miningcore.Extensions;
using NLog;

namespace Miningcore.Api.Middlewares;

public enum AdminApiCredentialStatus
{
    Missing,
    Invalid,
    Configured,
}

public sealed class AdminApiCredential
{
    private AdminApiCredential(AdminApiCredentialStatus status, byte[] tokenHash = null)
    {
        Status = status;
        this.tokenHash = tokenHash;
    }

    private readonly byte[] tokenHash;

    public const int RequiredTokenCharacters = 64;

    public AdminApiCredentialStatus Status { get; }

    public static AdminApiCredential Create(string token)
    {
        if(string.IsNullOrEmpty(token))
            return new AdminApiCredential(AdminApiCredentialStatus.Missing);

        if(!IsValidToken(token))
            return new AdminApiCredential(AdminApiCredentialStatus.Invalid);

        return new AdminApiCredential(AdminApiCredentialStatus.Configured,
            SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    public bool Verify(string candidate)
    {
        if(Status != AdminApiCredentialStatus.Configured || !IsValidToken(candidate))
            return false;

        var candidateHash = SHA256.HashData(Encoding.ASCII.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(tokenHash, candidateHash);
    }

    private static bool IsValidToken(string token)
    {
        if(token?.Length != RequiredTokenCharacters)
            return false;

        foreach(var c in token)
        {
            if(c is not (>= '0' and <= '9') and
               not (>= 'a' and <= 'f') and
               not (>= 'A' and <= 'F'))
                return false;
        }

        return true;
    }
}

public sealed class AdminApiAuthenticationMiddleware
{
    public AdminApiAuthenticationMiddleware(RequestDelegate next,
        AdminApiCredential credential, bool gpdrCompliantLogging)
    {
        this.next = next;
        this.credential = credential;
        this.gpdrCompliantLogging = gpdrCompliantLogging;
    }

    private readonly RequestDelegate next;
    private readonly AdminApiCredential credential;
    private readonly bool gpdrCompliantLogging;
    private readonly ILogger logger = LogManager.GetCurrentClassLogger();

    public const string TokenEnvironmentVariable = "MININGCORE_ADMIN_API_TOKEN";

    public static bool IsAdminRequest(PathString path) => path.StartsWithSegments(
        new PathString("/api/admin"), StringComparison.OrdinalIgnoreCase);

    public async Task Invoke(HttpContext context)
    {
        if(!IsAdminRequest(context.Request.Path))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";

        if(credential.Status != AdminApiCredentialStatus.Configured)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("Administrative API authentication is unavailable.\n");
            return;
        }

        var authorizationValues = context.Request.Headers.Authorization;
        var authorized = authorizationValues.Count == 1 &&
            AuthenticationHeaderValue.TryParse(authorizationValues[0], out var authorization) &&
            string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            credential.Verify(authorization.Parameter);

        if(!authorized)
        {
            var remoteAddress = context.Connection.RemoteIpAddress;
            var remoteDisplay = remoteAddress != null
                ? remoteAddress.CensorOrReturn(gpdrCompliantLogging).ToString()
                : "unknown";
            logger.Info(() => $"Unauthenticated administrative API request to {context.Request.Path.Value} from {remoteDisplay}");

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Miningcore administrative API\"";
            await context.Response.WriteAsync("Administrative API authentication required.\n");
            return;
        }

        await next(context);
    }
}
