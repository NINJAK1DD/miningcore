using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Primitives;
using Miningcore.Api.Controllers;
using Miningcore.Api.Middlewares;
using Xunit;

namespace Miningcore.Tests;

public class AdminApiSecurityTests
{
    private static readonly object EnvironmentGate = new();

    [Theory]
    [InlineData(null, AdminApiCredentialStatus.Missing)]
    [InlineData("", AdminApiCredentialStatus.Missing)]
    [InlineData("short", AdminApiCredentialStatus.Invalid)]
    [InlineData("0123456789abcdef0123456789abcde ", AdminApiCredentialStatus.Invalid)]
    [InlineData(ValidToken, AdminApiCredentialStatus.Configured)]
    public void CredentialValidation_FailsClosed(string token,
        AdminApiCredentialStatus expected)
    {
        Assert.Equal(expected, AdminApiCredential.Create(token).Status);
    }

    [Fact]
    public void CredentialValidation_RequiresExactly64HexadecimalCharacters()
    {
        var credential = AdminApiCredential.Create(ValidToken);
        var uppercaseToken = ValidToken.ToUpperInvariant();
        var uppercaseCredential = AdminApiCredential.Create(uppercaseToken);

        Assert.Equal(AdminApiCredentialStatus.Configured, credential.Status);
        Assert.True(credential.Verify(ValidToken));
        Assert.Equal(AdminApiCredentialStatus.Configured,
            uppercaseCredential.Status);
        Assert.True(uppercaseCredential.Verify(uppercaseToken));
        Assert.True(credential.Verify(uppercaseToken));
        Assert.True(uppercaseCredential.Verify(ValidToken));
        Assert.Equal(AdminApiCredentialStatus.Invalid,
            AdminApiCredential.Create(new string('a',
                AdminApiCredential.RequiredTokenCharacters - 1)).Status);
        Assert.Equal(AdminApiCredentialStatus.Invalid,
            AdminApiCredential.Create(new string('a',
                AdminApiCredential.RequiredTokenCharacters + 1)).Status);
    }

    [Fact]
    public void CredentialValidation_RejectsCharactersOutsideHexadecimalAlphabet()
    {
        var invalidTokens = new[]
        {
            $"{ValidToken[..^1]}テ",
            $"{ValidToken[..^1]}\"",
            $"{ValidToken[..^1]}'",
            $"{ValidToken[..^1]}$",
            $"{ValidToken[..^1]};",
            $"{ValidToken[..^1]}\u001f",
            $"{ValidToken[..^1]}_",
        };
        var credential = AdminApiCredential.Create(ValidToken);

        foreach(var token in invalidTokens)
        {
            Assert.Equal(AdminApiCredentialStatus.Invalid,
                AdminApiCredential.Create(token).Status);
            Assert.False(credential.Verify(token));
        }
    }

    [Fact]
    public void CredentialVerification_AcceptsOnlyExactToken()
    {
        var credential = AdminApiCredential.Create(ValidToken);

        Assert.True(credential.Verify(ValidToken));
        Assert.False(credential.Verify(new string('b', ValidToken.Length)));
        Assert.False(credential.Verify($"{ValidToken} "));
        Assert.False(credential.Verify(null));
    }

    [Fact]
    public void EnvironmentCredential_IsDigestedAndRemovedFromManagedProcess()
    {
        lock(EnvironmentGate)
        {
            var variable = AdminApiAuthenticationMiddleware
                .TokenEnvironmentVariable;
            var original = Environment.GetEnvironmentVariable(variable);

            try
            {
                Environment.SetEnvironmentVariable(variable, ValidToken);

                var credential = Program
                    .ReadAdminApiCredentialFromEnvironment();

                Assert.Equal(AdminApiCredentialStatus.Configured,
                    credential.Status);
                Assert.True(credential.Verify(ValidToken));
                Assert.Null(Environment.GetEnvironmentVariable(variable));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, original);
            }
        }
    }

    [Fact]
    public void AuthenticationRejectionLogLimiter_UsesMonotonicElapsedTimeAndSummarizesEntries()
    {
        var timeProvider = new ManualTimeProvider();
        var limiter = new AdminApiAuthenticationLogLimiter(
            TimeSpan.FromMinutes(1), timeProvider);

        Assert.True(limiter.TryAcquire(out var firstSuppressed));
        Assert.Equal(0L, firstSuppressed);
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.False(limiter.TryAcquire(out _));
        timeProvider.MoveWallClock(TimeSpan.FromDays(-1));
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(29));
        Assert.False(limiter.TryAcquire(out _));
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(out var summarized));
        Assert.Equal(2L, summarized);
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        Assert.False(limiter.TryAcquire(out _));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 8, 7, 12, 0, 0,
            TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void AdvanceMonotonic(TimeSpan elapsed) =>
            timestamp += elapsed.Ticks;

        public void MoveWallClock(TimeSpan change) =>
            utcNow = utcNow.Add(change);
    }

    [Theory]
    [InlineData("/api/admin", true)]
    [InlineData("/api/admin/", true)]
    [InlineData("/API/ADMIN/stats/gc", true)]
    [InlineData("//api/admin/status", false)]
    [InlineData("/api//admin", false)]
    // PathString canonicalizes percent-encoded unreserved characters before matching.
    [InlineData("/api/%61dmin", true)]
    [InlineData("/api/administer", false)]
    [InlineData("/api/administrator", false)]
    [InlineData("/api/pools", false)]
    public void AdministrativePathMatching_IsCaseInsensitiveAndSegmentBounded(
        string path, bool expected)
    {
        Assert.Equal(expected,
            AdminApiAuthenticationMiddleware.IsAdminRequest(path));
    }

    [Fact]
    public async Task NonAdministrativeRoute_BypassesMissingCredential()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(null, () => nextCalled = true);
        var context = CreateContext("/api/pools");

        await middleware.Invoke(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic Zm9vOmJhcg==")]
    [InlineData("Bearer wrong-token")]
    public async Task AdministrativeRoute_RejectsMissingOrInvalidAuthorization(
        string authorization)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ValidToken, () => nextCalled = true);
        var context = CreateContext("/api/admin/stats/gc");
        if(authorization != null)
            context.Request.Headers.Authorization = authorization;

        await middleware.Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("Bearer realm=\"Miningcore administrative API\"",
            context.Response.Headers.WWWAuthenticate);
        Assert.Equal("text/plain; charset=utf-8",
            context.Response.ContentType);
    }

    [Fact]
    public async Task AdministrativeRoute_RejectsMultipleAuthorizationHeaders()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ValidToken, () => nextCalled = true);
        var context = CreateContext("/api/admin/stats/gc");
        context.Request.Headers.Authorization = new StringValues(new[]
        {
            $"Bearer {ValidToken}",
            $"Bearer {ValidToken}",
        });

        await middleware.Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            context.Response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task AdministrativeRoute_FailsClosedWhenCredentialUnavailable(
        string token)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(token, () => nextCalled = true);
        var context = CreateContext("/api/admin/stats/gc");
        context.Request.Headers.Authorization = $"Bearer {ValidToken}";

        await middleware.Invoke(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable,
            context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("text/plain; charset=utf-8",
            context.Response.ContentType);
    }

    [Fact]
    public async Task AdministrativeRoute_AcceptsValidBearerToken()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(ValidToken, () => nextCalled = true);
        var context = CreateContext("/Api/Admin/stats/gc");
        context.Request.Headers.Authorization = $"bearer {ValidToken}";

        await middleware.Invoke(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(nameof(AdminApiController.SetLoggingLevel), "PUT")]
    [InlineData(nameof(AdminApiController.EnablePoolsPaymentProcessing), "PUT")]
    [InlineData(nameof(AdminApiController.DisablePoolsPaymentProcessing), "PUT")]
    [InlineData(nameof(AdminApiController.EnablePoolPaymentProcessing), "PUT")]
    [InlineData(nameof(AdminApiController.DisablePoolPaymentProcessing), "PUT")]
    [InlineData(nameof(AdminApiController.SetMinerSettingsAsync), "PUT")]
    [InlineData(nameof(AdminApiController.ForceGc), "POST")]
    public void AdministrativeMutations_UseSafeHttpVerbs(string methodName,
        string expectedVerb)
    {
        var method = typeof(AdminApiController).GetMethod(methodName);
        var attribute = Assert.Single(method.GetCustomAttributes(false)
            .OfType<HttpMethodAttribute>());

        Assert.Equal(new[] { expectedVerb }, attribute.HttpMethods);
        Assert.DoesNotContain("GET", attribute.HttpMethods);
    }

    [Fact]
    public void PublicMinerSettingsRoute_IsReadOnly()
    {
        var settingsActions = typeof(PoolApiController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes(false)
                .OfType<HttpMethodAttribute>())
            .Where(attribute => string.Equals(attribute.Template,
                "{poolId}/miners/{address}/settings",
                StringComparison.Ordinal))
            .ToArray();

        var action = Assert.Single(settingsActions);
        Assert.Equal(new[] { "GET" }, action.HttpMethods);
    }

    [Theory]
    [InlineData("api/pools", true)]
    [InlineData("/api/health-check", true)]
    [InlineData("api/admin/stats/gc", false)]
    [InlineData("/API/ADMIN/payment/processing/disable", false)]
    public void PublicHelp_OmitsAdministrativeRoutes(string template,
        bool expected)
    {
        Assert.Equal(expected, PoolApiController.IsPublicHelpRoute(template));
    }

    [Fact]
    public void MinerAddressNormalization_CanonicalizesEthereumAddresses()
    {
        var pool = new Configuration.PoolConfig
        {
            Template = new Configuration.EthereumCoinTemplate
            {
                Family = Configuration.CoinFamily.Ethereum,
            },
        };

        Assert.Equal("0xabcdef0123456789",
            ApiControllerBase.NormalizeMinerAddress(pool,
                "0xAbCdEf0123456789"));
    }

    private static AdminApiAuthenticationMiddleware CreateMiddleware(
        string token, Action next)
    {
        return new AdminApiAuthenticationMiddleware(_ =>
        {
            next();
            return Task.CompletedTask;
        }, AdminApiCredential.Create(token), false);
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private const string ValidToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
