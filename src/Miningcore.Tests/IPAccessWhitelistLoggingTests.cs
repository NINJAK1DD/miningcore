using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Miningcore.Api;
using Miningcore.Api.Middlewares;
using Miningcore.Configuration;
using NLog;
using NLog.Config;
using NLog.Targets;
using Prometheus;
using Xunit;

namespace Miningcore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IPAccessWhitelistLoggingCollection
{
    // These tests temporarily replace NLog's process-wide configuration. Scope
    // properties isolate assertions from foreign records; keeping this collection
    // out of xunit's parallel collection phase also limits configuration overlap.
    public const string Name = "IP access-whitelist logging";
}

[Collection(IPAccessWhitelistLoggingCollection.Name)]
public class IPAccessWhitelistLoggingTests
{
    [Fact]
    public void MiddlewareActivation_UsesOptionalTimeProviderWithoutDiRegistration()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        RequestDelegate next = _ => Task.CompletedTask;

        Assert.Single(typeof(IPAccessWhitelistMiddleware).GetConstructors());
        Assert.Single(typeof(AdminApiAuthenticationMiddleware).GetConstructors());

        var exception = Record.Exception(() =>
        {
            _ = ActivatorUtilities.CreateInstance<
                IPAccessWhitelistMiddleware>(provider, next,
                new[] { Program.MetricsRoutePrefix },
                new[] { IPAddress.Loopback }, false);
            _ = ActivatorUtilities.CreateInstance<
                AdminApiAuthenticationMiddleware>(provider, next,
                AdminApiCredential.Create(ValidAdminToken), false);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void WhitelistMetricRouteFamilies_AreFixedAndNotPathDerived()
    {
        Assert.Equal(ProtectedRouteClassifier.AdminRouteFamily,
            ProtectedRouteClassifier.ClassifyWhitelistLocations(
                new[] { "/API/ADMIN/custom" }));
        Assert.Equal(ProtectedRouteClassifier.MetricsRouteFamily,
            ProtectedRouteClassifier.ClassifyWhitelistLocations(
                new[] { "/METRICS/custom" }));
        Assert.Equal(ProtectedRouteClassifier.OtherRouteFamily,
            ProtectedRouteClassifier.ClassifyWhitelistLocations(
                new[] { "/custom/attacker-controlled-path" }));
        Assert.Equal(ProtectedRouteClassifier.OtherRouteFamily,
            ProtectedRouteClassifier.ClassifyWhitelistLocations(
                new[] { "/api/admin", "/metrics" }));
    }

    [Fact]
    public async Task Rejections_AreBoundedAndSummarizedUsingMonotonicTime()
    {
        using var logs = new LogCapture();
        var timeProvider = new ManualTimeProvider();
        var nextCalls = 0;
        var middleware = new IPAccessWhitelistMiddleware(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            new[] { Program.MetricsRoutePrefix },
            new[] { IPAddress.Loopback }, false, timeProvider);

        await InvokeRejectedAsync(middleware, "/metrics",
            IPAddress.Parse("203.0.113.10"));
        await InvokeRejectedAsync(middleware, "/metrics",
            IPAddress.Parse("203.0.113.11"));
        timeProvider.MoveWallClock(TimeSpan.FromDays(-1));
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(59));
        await InvokeRejectedAsync(middleware, "/metrics",
            IPAddress.Parse("203.0.113.12"));
        timeProvider.AdvanceMonotonic(TimeSpan.FromSeconds(1));
        await InvokeRejectedAsync(middleware, "/metrics",
            IPAddress.Parse("203.0.113.13"));

        LogManager.Flush();
        var informational = logs.Messages("Info",
            "Unauthorized request attempt");
        var detailed = logs.Messages("Debug",
            "Unauthorized request attempt");

        Assert.Equal(0, nextCalls);
        Assert.Collection(informational,
            first =>
            {
                Assert.Contains("/metrics", first, StringComparison.Ordinal);
                Assert.Contains("203.0.113.10", first,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("other rejection(s)", first,
                    StringComparison.Ordinal);
            },
            summary =>
            {
                Assert.Contains("203.0.113.13", summary,
                    StringComparison.Ordinal);
                Assert.Contains("2 other rejection(s), possibly from other sources",
                    summary, StringComparison.Ordinal);
            });
        Assert.Equal(2, detailed.Length);
        Assert.Contains(detailed, message => message.Contains(
            "203.0.113.11", StringComparison.Ordinal));
        Assert.Contains(detailed, message => message.Contains(
            "203.0.113.12", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitingModes_BoundCanonicalMetricsAndAdminIndependently(
        bool enableRateLimiting)
    {
        using var logs = new LogCapture();
        var timeProvider = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        if(enableRateLimiting)
        {
            Program.AddApiRateLimiting(services, new ApiConfig
            {
                RateLimiting = new ApiRateLimitConfig(),
            });
        }
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var endpointCalls = 0;
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        Program.ConfigureApiPipeline(app, ports,
            new[] { "198.51.100.10" }, new[] { "198.51.100.10" },
            AdminApiCredential.Create(ValidAdminToken), false,
            afterAccessControl: pipeline => pipeline.Run(_ =>
            {
                endpointCalls++;
                return Task.CompletedTask;
            }),
            options: new Program.ApiPipelineOptions(
                EnableIpRateLimiting: enableRateLimiting,
                ProtectedRouteRejectionTimeProvider: timeProvider));
        var pipeline = app.Build();
        var metricsBefore = await ReadWhitelistRejectionCountAsync(
            ProtectedRouteClassifier.MetricsRouteFamily);
        var adminBefore = await ReadWhitelistRejectionCountAsync(
            ProtectedRouteClassifier.AdminRouteFamily);

        // Exact canonical scrapes intentionally bypass the public API limiter. The
        // whitelist's own fixed-size limiter must still bound a many-address flood.
        for(var index = 0; index < 256; index++)
        {
            var remote = IPAddress.Parse(
                $"203.0.{index / 254}.{index % 254 + 1}");
            var response = await InvokePipelineAsync(pipeline, ports.MetricsPort,
                HttpMethods.Get, "/metrics", remote, provider);
            Assert.Equal(StatusCodes.Status403Forbidden,
                response.Response.StatusCode);
        }

        var firstAdmin = await InvokePipelineAsync(pipeline, ports.AdminPort,
            HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.200"), provider);
        var suppressedAdmin = await InvokePipelineAsync(pipeline,
            ports.AdminPort, HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.201"), provider);
        Assert.Equal(StatusCodes.Status403Forbidden,
            firstAdmin.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            suppressedAdmin.Response.StatusCode);

        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        var metricsSummary = await InvokePipelineAsync(pipeline,
            ports.MetricsPort, HttpMethods.Get, "/metrics",
            IPAddress.Parse("203.0.113.210"), provider);
        var adminSummary = await InvokePipelineAsync(pipeline, ports.AdminPort,
            HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.211"), provider);

        Assert.Equal(StatusCodes.Status403Forbidden,
            metricsSummary.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            adminSummary.Response.StatusCode);
        Assert.Equal(0, endpointCalls);

        LogManager.Flush();
        var informational = logs.Messages("Info",
            "Unauthorized request attempt");
        var metricsEntries = informational.Where(message =>
            message.Contains("/metrics", StringComparison.Ordinal)).ToArray();
        var adminEntries = informational.Where(message =>
            message.Contains("/api/admin/stats/gc",
                StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, metricsEntries.Length);
        Assert.Contains("255 other rejection(s), possibly from other sources",
            metricsEntries[1],
            StringComparison.Ordinal);
        Assert.Equal(2, adminEntries.Length);
        Assert.Contains("1 other rejection(s), possibly from other sources",
            adminEntries[1],
            StringComparison.Ordinal);
        Assert.Equal(257d,
            await ReadWhitelistRejectionCountAsync(
                ProtectedRouteClassifier.MetricsRouteFamily) - metricsBefore);
        Assert.Equal(3d,
            await ReadWhitelistRejectionCountAsync(
                ProtectedRouteClassifier.AdminRouteFamily) - adminBefore);
    }

    [Fact]
    public async Task AuthenticationRejections_UsePerPipelineInjectedLimiter()
    {
        using var logs = new LogCapture();
        var timeProvider = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        using var provider = services.BuildServiceProvider();
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        RequestDelegate BuildPipeline()
        {
            var app = new ApplicationBuilder(provider);
            Program.ConfigureApiPipeline(app, ports, null, null,
                AdminApiCredential.Create(ValidAdminToken), false,
                options: new Program.ApiPipelineOptions(
                    ProtectedRouteRejectionTimeProvider: timeProvider),
                afterAccessControl: pipeline => pipeline.Run(_ =>
                    Task.CompletedTask));
            return app.Build();
        }

        var firstPipeline = BuildPipeline();
        var secondPipeline = BuildPipeline();

        var first = await InvokePipelineAsync(firstPipeline, ports.AdminPort,
            HttpMethods.Get, "/api/admin/status", IPAddress.Loopback,
            provider);
        var suppressed = await InvokePipelineAsync(firstPipeline,
            ports.AdminPort, HttpMethods.Get, "/api/admin/status",
            IPAddress.Loopback, provider);
        var independent = await InvokePipelineAsync(secondPipeline,
            ports.AdminPort, HttpMethods.Get, "/api/admin/status",
            IPAddress.IPv6Loopback, provider);
        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        var summary = await InvokePipelineAsync(firstPipeline, ports.AdminPort,
            HttpMethods.Get, "/api/admin/status", IPAddress.Loopback,
            provider);

        Assert.Equal(StatusCodes.Status401Unauthorized,
            first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            suppressed.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            independent.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            summary.Response.StatusCode);

        LogManager.Flush();
        var informational = logs.Messages("Info",
            "Rejected administrative bearer authentication");
        var detailed = logs.Messages("Debug",
            "Rejected administrative bearer authentication");

        Assert.Equal(3, informational.Length);
        Assert.Contains(informational, message => message.Contains(
            "from ::1", StringComparison.Ordinal));
        Assert.Contains(informational, message => message.Contains(
            "1 other rejection(s), possibly from other sources",
            StringComparison.Ordinal));
        Assert.Single(detailed);
    }

    [Fact]
    public async Task Rejections_PreserveGdprRenderingAndFailClosedWithoutRemoteAddress()
    {
        using var logs = new LogCapture();
        var timeProvider = new ManualTimeProvider();
        var whitelist = new[] { IPAddress.Loopback };
        var nextCalls = 0;
        RequestDelegate next = _ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        };
        var gdprMiddleware = new IPAccessWhitelistMiddleware(next,
            new[] { "/metrics" }, whitelist, true, timeProvider);
        var unknownMiddleware = new IPAccessWhitelistMiddleware(next,
            new[] { "/api/admin" }, whitelist, false, timeProvider);

        var gdprResponse = await InvokeRejectedAsync(gdprMiddleware,
            "/metrics", IPAddress.Parse("203.0.113.99"));
        var unknownResponse = await InvokeRejectedAsync(unknownMiddleware,
            "/api/admin/status", null);

        Assert.Equal(StatusCodes.Status403Forbidden,
            gdprResponse.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            unknownResponse.Response.StatusCode);
        Assert.Equal(0, nextCalls);

        LogManager.Flush();
        var informational = logs.Messages("Info",
            "Unauthorized request attempt");
        Assert.Contains(informational, message =>
            message.Contains("/metrics from 203.0.0.0",
                StringComparison.Ordinal));
        Assert.Contains(informational, message =>
            message.Contains("/api/admin/status from unknown",
                StringComparison.Ordinal));
        Assert.DoesNotContain(informational, message =>
            message.Contains("203.0.113.99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultWhitelist_AllowsLoopbackAndRejectsOtherClients()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        var endpointCalls = 0;
        var ports = new Program.ApiEndpointPorts(4000, 4000, 4000);

        Program.ConfigureApiPipeline(app, ports, null, null,
            AdminApiCredential.Create(ValidAdminToken), false,
            afterAccessControl: pipeline => pipeline.Run(_ =>
            {
                endpointCalls++;
                return Task.CompletedTask;
            }));
        var pipeline = app.Build();

        var loopbackMetrics = await InvokePipelineAsync(pipeline,
            ports.MetricsPort, HttpMethods.Get, "/metrics",
            IPAddress.Loopback, provider);
        var loopbackAdmin = await InvokePipelineAsync(pipeline,
            ports.AdminPort, HttpMethods.Get, "/api/admin/status",
            IPAddress.IPv6Loopback, provider,
            $"Bearer {ValidAdminToken}");
        var remoteMetrics = await InvokePipelineAsync(pipeline,
            ports.MetricsPort, HttpMethods.Get, "/metrics",
            IPAddress.Parse("203.0.113.10"), provider);

        Assert.Equal(StatusCodes.Status200OK,
            loopbackMetrics.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK,
            loopbackAdmin.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            remoteMetrics.Response.StatusCode);
        Assert.Equal(2, endpointCalls);
    }

    private static async Task<DefaultHttpContext> InvokeRejectedAsync(
        IPAccessWhitelistMiddleware middleware, string path,
        IPAddress remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Response.Body = new MemoryStream();

        await middleware.Invoke(context);
        return context;
    }

    private static async Task<DefaultHttpContext> InvokePipelineAsync(
        RequestDelegate pipeline, int localPort, string method, string path,
        IPAddress remoteAddress, IServiceProvider requestServices = null,
        string authorization = null)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = requestServices;
        context.Connection.LocalPort = localPort;
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if(authorization != null)
            context.Request.Headers.Authorization = authorization;

        await pipeline(context);
        return context;
    }

    private static async Task<double> ReadWhitelistRejectionCountAsync(
        string routeFamily)
    {
        await using var stream = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        var prefix =
            $"miningcore_api_ip_whitelist_rejections_total{{route_family=\"{routeFamily}\"}} ";
        var sample = text.Split('\n').SingleOrDefault(line =>
            line.StartsWith(prefix, StringComparison.Ordinal));

        return sample == null
            ? 0d
            : double.Parse(sample.AsSpan(prefix.Length),
                NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private sealed class LogCapture : IDisposable
    {
        public LogCapture()
        {
            captureId = Guid.NewGuid().ToString("N");
            previousConfiguration = LogManager.Configuration;
            Target = new MemoryTarget
            {
                Layout =
                    "${scopeproperty:item=WhitelistLogCaptureId}|${level}|${message}",
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRuleForAllLevels(Target);
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();
            scope = ScopeContext.PushProperty("WhitelistLogCaptureId",
                captureId);
        }

        private readonly string captureId;
        private readonly LoggingConfiguration previousConfiguration;
        private readonly IDisposable scope;
        public MemoryTarget Target { get; }

        public string[] Messages(string level, string text) =>
            Target.Logs.Where(message =>
                    message.StartsWith($"{captureId}|{level}|",
                        StringComparison.OrdinalIgnoreCase) &&
                    message.Contains(text, StringComparison.Ordinal))
                .ToArray();

        public void Dispose()
        {
            LogManager.Flush();
            scope.Dispose();
            LogManager.Configuration = previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }
    }

    private const string ValidAdminToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
