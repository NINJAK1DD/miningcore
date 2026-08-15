using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Miningcore.Extensions;
using NLog;
using Prometheus;

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
            HashToken(token));
    }

    public bool Verify(string candidate)
    {
        if(Status != AdminApiCredentialStatus.Configured || !IsValidToken(candidate))
            return false;

        // Format validation is allowed to short-circuit because token length and alphabet
        // are public protocol requirements. Only the secret value comparison must be fixed-time.
        var candidateHash = HashToken(candidate);
        return CryptographicOperations.FixedTimeEquals(tokenHash, candidateHash);
    }

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Convert.FromHexString(token));

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

internal sealed class AdminApiCredentialProvider
{
    public AdminApiCredentialProvider()
    {
        credential = new Lazy<AdminApiCredential>(CreateFromEnvironment);
    }

    // Repeated in-process host construction must reuse the original credential
    // instead of silently downgrading to Missing after its environment copy is cleared.
    private readonly Lazy<AdminApiCredential> credential;

    public AdminApiCredential Get()
    {
        try
        {
            return credential.Value;
        }
        finally
        {
            // Remove every managed-process copy, including a replacement supplied
            // after the immutable process credential was initialized. The service
            // manager or container runtime may still retain its configured metadata.
            Environment.SetEnvironmentVariable(
                AdminApiAuthenticationMiddleware.TokenEnvironmentVariable, null);
        }
    }

    private static AdminApiCredential CreateFromEnvironment() =>
        AdminApiCredential.Create(Environment.GetEnvironmentVariable(
            AdminApiAuthenticationMiddleware.TokenEnvironmentVariable));
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
    private static readonly AdminApiAuthenticationLogLimiter RejectionLogLimiter =
        new(TimeSpan.FromMinutes(1));
    private readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private static readonly Counter AuthenticationCounter = Metrics.CreateCounter(
        "miningcore_admin_api_authentication_total",
        "Administrative API authentication outcomes",
        new CounterConfiguration
        {
            LabelNames = new[] { "outcome" },
        });

    public const string TokenEnvironmentVariable = "MININGCORE_ADMIN_API_TOKEN";
    public const string AdminRoutePrefix =
        ProtectedRouteClassifier.AdminRoutePrefix;

    public static bool IsAdminRequest(PathString path) =>
        ProtectedRouteClassifier.IsAdminRequest(path);

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
            AuthenticationCounter.WithLabels("unavailable").Inc();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/plain; charset=utf-8";
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
            if(RejectionLogLimiter.TryAcquire(out var suppressed))
                logger.Info(() => FormatRejection(context, suppressed));
            else
                logger.Debug(() => FormatRejection(context, 0));

            AuthenticationCounter.WithLabels("rejected").Inc();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.WWWAuthenticate = "Bearer realm=\"Miningcore administrative API\"";
            await context.Response.WriteAsync("Administrative API authentication required.\n");
            return;
        }

        AuthenticationCounter.WithLabels("accepted").Inc();
        await next(context);
    }

    private string FormatRejection(HttpContext context, long suppressed)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        var remoteDisplay = remoteAddress != null
            ? remoteAddress.CensorOrReturn(gpdrCompliantLogging).ToString()
            : "unknown";
        var result =
            $"Rejected administrative bearer authentication to {context.Request.Path.Value} from {remoteDisplay}";

        if(suppressed > 0)
            result +=
                $"; {suppressed} additional rejection(s) occurred after the previous informational entry and were suppressed";

        return result;
    }
}

internal sealed class AdminApiAuthenticationLogLimiter
{
    public AdminApiAuthenticationLogLimiter(TimeSpan interval,
        TimeProvider timeProvider = null)
    {
        if(interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        this.interval = interval;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly object gate = new();
    private readonly TimeSpan interval;
    private readonly TimeProvider timeProvider;
    private long previousInformationalTimestamp;
    private long suppressed;
    private bool hasInformationalEntry;

    public bool TryAcquire(out long suppressedSinceLastEntry)
    {
        lock(gate)
        {
            var now = timeProvider.GetTimestamp();

            if(!hasInformationalEntry ||
                timeProvider.GetElapsedTime(previousInformationalTimestamp,
                    now) >= interval)
            {
                suppressedSinceLastEntry = suppressed;
                suppressed = 0;
                previousInformationalTimestamp = now;
                hasInformationalEntry = true;
                return true;
            }

            suppressed++;
            suppressedSinceLastEntry = 0;
            return false;
        }
    }
}
