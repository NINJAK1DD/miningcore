using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Miningcore.Api.Middlewares;
using NLog;
using NLog.Config;
using NLog.Targets;
using Xunit;

namespace Miningcore.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IPAccessWhitelistLoggingCollection
{
    public const string Name = "IP access-whitelist logging";
}

[Collection(IPAccessWhitelistLoggingCollection.Name)]
public class IPAccessWhitelistLoggingTests
{
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

        Assert.Equal(0, nextCalls);
        Assert.Collection(informational,
            first =>
            {
                Assert.Contains("/metrics", first, StringComparison.Ordinal);
                Assert.Contains("203.0.113.10", first,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("additional rejection", first,
                    StringComparison.Ordinal);
            },
            summary =>
            {
                Assert.Contains("203.0.113.13", summary,
                    StringComparison.Ordinal);
                Assert.Contains("2 additional rejection(s)", summary,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task DisabledRateLimiting_BoundsCanonicalMetricsAndAdminIndependently()
    {
        using var logs = new LogCapture();
        var timeProvider = new ManualTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCors();
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
                WhitelistRejectionTimeProvider: timeProvider));
        var pipeline = app.Build();

        // Exact canonical scrapes intentionally bypass the public API limiter. The
        // whitelist's own fixed-size limiter must still bound a many-address flood.
        for(var index = 0; index < 256; index++)
        {
            var remote = IPAddress.Parse(
                $"203.0.{index / 254}.{index % 254 + 1}");
            var response = await InvokePipelineAsync(pipeline, ports.MetricsPort,
                HttpMethods.Get, "/metrics", remote);
            Assert.Equal(StatusCodes.Status403Forbidden,
                response.Response.StatusCode);
        }

        var firstAdmin = await InvokePipelineAsync(pipeline, ports.AdminPort,
            HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.200"));
        var suppressedAdmin = await InvokePipelineAsync(pipeline,
            ports.AdminPort, HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.201"));
        Assert.Equal(StatusCodes.Status403Forbidden,
            firstAdmin.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden,
            suppressedAdmin.Response.StatusCode);

        timeProvider.AdvanceMonotonic(TimeSpan.FromMinutes(1));
        var metricsSummary = await InvokePipelineAsync(pipeline,
            ports.MetricsPort, HttpMethods.Get, "/metrics",
            IPAddress.Parse("203.0.113.210"));
        var adminSummary = await InvokePipelineAsync(pipeline, ports.AdminPort,
            HttpMethods.Post, "/api/admin/stats/gc",
            IPAddress.Parse("203.0.113.211"));

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
        Assert.Contains("255 additional rejection(s)", metricsEntries[1],
            StringComparison.Ordinal);
        Assert.Equal(2, adminEntries.Length);
        Assert.Contains("1 additional rejection(s)", adminEntries[1],
            StringComparison.Ordinal);
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
            IPAddress.Loopback);
        var loopbackAdmin = await InvokePipelineAsync(pipeline,
            ports.AdminPort, HttpMethods.Get, "/api/admin/status",
            IPAddress.IPv6Loopback, $"Bearer {ValidAdminToken}");
        var remoteMetrics = await InvokePipelineAsync(pipeline,
            ports.MetricsPort, HttpMethods.Get, "/metrics",
            IPAddress.Parse("203.0.113.10"));

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
        IPAddress remoteAddress, string authorization = null)
    {
        var context = new DefaultHttpContext();
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        private DateTimeOffset utcNow = new(2026, 8, 15, 12, 0, 0,
            TimeSpan.Zero);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void AdvanceMonotonic(TimeSpan elapsed) =>
            timestamp += elapsed.Ticks;

        public void MoveWallClock(TimeSpan change) =>
            utcNow = utcNow.Add(change);
    }

    private sealed class LogCapture : IDisposable
    {
        public LogCapture()
        {
            previousConfiguration = LogManager.Configuration;
            Target = new MemoryTarget
            {
                Layout = "${level}|${message}",
            };
            var configuration = new LoggingConfiguration();
            configuration.AddRuleForAllLevels(Target);
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();
        }

        private readonly LoggingConfiguration previousConfiguration;
        public MemoryTarget Target { get; }

        public string[] Messages(string level, string text) =>
            Target.Logs.Where(message =>
                    message.StartsWith($"{level}|",
                        StringComparison.OrdinalIgnoreCase) &&
                    message.Contains(text, StringComparison.Ordinal))
                .ToArray();

        public void Dispose()
        {
            LogManager.Flush();
            LogManager.Configuration = previousConfiguration;
            LogManager.ReconfigExistingLoggers();
        }
    }

    private const string ValidAdminToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
