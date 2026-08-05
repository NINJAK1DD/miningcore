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
    public void CredentialVerification_AcceptsOnlyExactToken()
    {
        var credential = AdminApiCredential.Create(ValidToken);

        Assert.True(credential.Verify(ValidToken));
        Assert.False(credential.Verify(new string('b', ValidToken.Length)));
        Assert.False(credential.Verify($"{ValidToken} "));
        Assert.False(credential.Verify(null));
    }

    [Theory]
    [InlineData("/api/admin", true)]
    [InlineData("/API/ADMIN/stats/gc", true)]
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
