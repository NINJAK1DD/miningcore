using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Miningcore.Api.Middlewares;
using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json;
using MiningcoreProgram = Miningcore.Program;

if(args.Length > 0 && string.Equals(args[0], "hold",
       StringComparison.Ordinal))
    return await HoldRecoveryOwnershipAsync(args);

if(args.Length > 0 && string.Equals(args[0], "api",
       StringComparison.Ordinal))
    return await RunApiListenerAsync(args);

Console.Error.WriteLine(
    "usage: Miningcore.Tests.ProcessHost hold <recovery-file> <state-directory> <ready-file>\n" +
    "   or: Miningcore.Tests.ProcessHost api <config-file>");
return 64;

static async Task<int> HoldRecoveryOwnershipAsync(string[] args)
{
    if(args.Length != 4)
        return 64;

    var config = new ClusterConfig
    {
        ShareRecoveryFile = args[1],
        ShareRecoveryStateDirectory = args[2],
    };
    using var ownership = new ShareRecoveryPathOwnership(config);
    ownership.Acquire();
    await File.WriteAllTextAsync(args[3], Environment.ProcessId.ToString());

    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    catch(OperationCanceledException)
    {
    }

    return 0;
}

static async Task<int> RunApiListenerAsync(string[] args)
{
    if(args.Length != 2)
        return 64;

    var config = JsonConvert.DeserializeObject<ClusterConfig>(
        await File.ReadAllTextAsync(args[1]));
    var api = MiningcoreProgram.NormalizeApiConfig(config);
    var address = MiningcoreProgram.ResolveListenAddress(api.ListenAddress);
    var ports = MiningcoreProgram.ResolveApiEndpointPorts(api);
    var adminWhitelist = BuildWhitelist(api.AdminIpWhitelist);
    var metricsWhitelist = BuildWhitelist(api.MetricsIpWhitelist);

    using var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureWebHostDefaults(builder => builder
            .UseKestrel(options => MiningcoreProgram.ConfigureApiListeners(options,
                address, ports))
            .Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    if(!MiningcoreProgram.IsApiRequestAllowed(
                           context.Connection.LocalPort,
                           context.Request.Path, ports))
                    {
                        context.Response.StatusCode =
                            StatusCodes.Status404NotFound;
                        return;
                    }

                    await next();
                });
                app.UseMiddleware<IPAccessWhitelistMiddleware>(
                    new[] { "/api/admin" }, adminWhitelist, false);
                app.UseMiddleware<IPAccessWhitelistMiddleware>(
                    new[] { "/metrics" }, metricsWhitelist, false);
                app.Run(context =>
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    return context.Response.WriteAsync("ok\n");
                });
            }))
        .Build();

    await host.RunAsync();
    return 0;
}

static IPAddress[] BuildWhitelist(string[] configured)
{
    var result = configured?.Select(IPAddress.Parse).ToList() ?? new();

    foreach(var loopback in new[]
            {
                IPAddress.Loopback,
                IPAddress.IPv6Loopback,
                IPAddress.Parse("::ffff:127.0.0.1"),
            })
    {
        if(!result.Contains(loopback))
            result.Add(loopback);
    }

    return result.ToArray();
}
