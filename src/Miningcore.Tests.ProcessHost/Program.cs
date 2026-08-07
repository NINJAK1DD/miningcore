using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    var adminCredential = MiningcoreProgram
        .GetAdminApiCredential();

    using var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureWebHostDefaults(builder => builder
            .ConfigureServices(services => services.AddCors())
            .UseKestrel(options => MiningcoreProgram.ConfigureApiListeners(options,
                address, ports))
            .Configure(app =>
            {
                MiningcoreProgram.ConfigureApiPipeline(app, ports,
                    api.AdminIpWhitelist, api.MetricsIpWhitelist,
                    adminCredential, false,
                    afterAccessControl: pipeline => pipeline.Run(context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status200OK;
                        return context.Response.WriteAsync("ok\n");
                    }));
            }))
        .Build();

    await host.RunAsync();
    return 0;
}
