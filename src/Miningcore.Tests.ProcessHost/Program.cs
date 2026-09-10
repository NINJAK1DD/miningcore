using System.Net;
using System.Reflection;
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

if(args.Length > 1 && args[0] is "config-dump" or "config-dump-closed-output" or "config-dump-isolated-schema")
{
    var target = new NLog.Targets.FileTarget("captured-dump-logs")
    {
        FileName = args[1],
        Layout = "${level}|${message}|${exception:format=ToString}",
    };
    var logging = new NLog.Config.LoggingConfiguration();
    logging.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
    NLog.LogManager.Configuration = logging;
    try
    {
        if(args[0] == "config-dump-isolated-schema")
        {
            // Only this disposable child sees the synthetic installation. Keep
            // the real test output/schema untouched so parallel tests are safe.
            var entry = Path.Combine(Environment.CurrentDirectory,
                "installation-ISSUE144_SYNTHETIC_SECRET", "Miningcore.Tests.ProcessHost.dll");
            Assembly.SetEntryAssembly(Assembly.LoadFile(entry));
        }
        if(args[0] == "config-dump-closed-output")
            Console.SetOut(new ClosedDiagnosticOutput());
        return await MiningcoreProgram.Main(args.Skip(2).ToArray());
    }
    finally
    {
        NLog.LogManager.Shutdown();
    }
}

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
    var enableApiRateLimiting = api.RateLimiting?.Disabled != true;

    using var host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.ClearProviders())
        .ConfigureWebHostDefaults(builder => builder
            .ConfigureServices(services =>
            {
                services.AddCors();
                if(enableApiRateLimiting)
                    MiningcoreProgram.AddApiRateLimiting(services, api);
            })
            .UseKestrel(options => MiningcoreProgram.ConfigureApiListeners(options,
                address, ports))
            .Configure(app =>
            {
                MiningcoreProgram.ConfigureApiPipeline(app, ports,
                    api.AdminIpWhitelist, api.MetricsIpWhitelist,
                    adminCredential, false,
                    new MiningcoreProgram.ApiPipelineOptions(
                        EnableIpRateLimiting: enableApiRateLimiting),
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

sealed class ClosedDiagnosticOutput : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(char value) => throw new IOException("ISSUE144_SYNTHETIC_SECRET");
    public override void Write(string value) => throw new IOException("ISSUE144_SYNTHETIC_SECRET");
}
