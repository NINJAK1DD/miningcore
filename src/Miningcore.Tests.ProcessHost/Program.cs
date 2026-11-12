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
            // This copy supplies only Location; no code in it executes, so it
            // deliberately needs neither dependencies nor a copied .deps.json.
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

if(args.Length == 1 && args[0] == "wallet-control-probe")
{
    await using var lease = new Miningcore.Payments.PostgresPayoutManagerLease(
        new Miningcore.Persistence.Postgres.PgConnectionFactory(
            Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES")));
    if(!await lease.TryAcquireAsync(CancellationToken.None))
        return lease.RequiresRecovery ? 78 : 1;
    if(!await lease.TryBeginWalletPayoutAsync(CancellationToken.None, new Miningcore.Payments.WalletPayoutBatch("lab", new string('a', 64))))
    {
        Console.WriteLine("paused");
        return 0;
    }
    Console.WriteLine("admitted");
    if(await Console.In.ReadLineAsync() != "complete")
    {
        lease.MarkFinancialOutcomeUncertain();
        return 78;
    }
    await lease.CompleteWalletPayoutAsync(CancellationToken.None);
    return 0;
}

if(args.Length > 0 && args[0] == "payout-lease")
{
    if(args.Length != 3)
        return 64;
    using var payoutHost = Host.CreateDefaultBuilder().Build();
    await using var lease = new Miningcore.Payments.PostgresPayoutManagerLease(
        new Miningcore.Persistence.Postgres.PgConnectionFactory(args[1] == "-"
            ? Environment.GetEnvironmentVariable("MININGCORE_TEST_POSTGRES") : args[1]));
    if(!await lease.TryAcquireAsync(CancellationToken.None))
        return lease.RequiresRecovery ? 78 : 1;
    await File.WriteAllTextAsync(args[2], "acquired");
    await payoutHost.RunAsync();
    await lease.DisposeAsync();
    await File.WriteAllTextAsync(args[2] + ".released", "released");
    return 0;
}

if(args.Length == 3 && args[0] == "exit-once")
{
    var first = !File.Exists(args[1]);
    await File.AppendAllTextAsync(args[1], "started\n");
    return first ? int.Parse(args[2]) : 0;
}

if(args.Length == 1 && args[0] == "payout-lock-keys")
{
    Console.WriteLine(JsonConvert.SerializeObject(new
    {
        Miningcore.Payments.PostgresPayoutManagerLease.LockNamespace,
        Miningcore.Payments.PostgresPayoutManagerLease.LockKey,
    }));
    return 0;
}

// Synthetic, disposable restore-drill fixture only; the shipped CLI always requires confirmations.
if(args.Length == 2 && args[0] == "payout-release-probe")
{
    var config = MiningcoreProgram.ReadConfig(args[1]);
    var recovery = new Miningcore.Payments.PostgresPayoutOwnershipRecovery(
        new Miningcore.Persistence.Postgres.PgConnectionFactory(PostgresConnectionPolicy.Build(config.Persistence.Postgres).ConnectionString));
    try
    {
        var snapshot = await recovery.InspectAsync(CancellationToken.None);
        var receipt = await recovery.ReleaseAsync(snapshot, "isolated-restore-drill", CancellationToken.None);
        Console.WriteLine(receipt.EventId);
        return 0;
    }
    catch(Miningcore.Payments.PayoutRecoveryException error)
    {
        Console.Error.WriteLine(error.Message);
        return error.ExitCode;
    }
}

if(args.Length > 1 && args[0] == "payout-main")
    return await MiningcoreProgram.Main(args.Skip(1).ToArray());

if(args.Length is 3 or 4 && args[0] == "payout-timestamp-probe")
{
    var startup = args.Length == 4 && args[3] == "startup";
    var result = await MiningcoreProgram.Main(startup ? ["-c", args[1]] : ["--recover-payout-ownership", "-c", args[1]]);
    if(result != (startup ? 78 : 0)) return 1;
    var config = MiningcoreProgram.ReadConfig(args[1]);
    await using var con = new Npgsql.NpgsqlConnection(PostgresConnectionPolicy.Build(config.Persistence.Postgres).ConnectionString);
    await con.OpenAsync();
    await using var command = new Npgsql.NpgsqlCommand("SELECT @value::timestamp", con);
    command.Parameters.AddWithValue("value", NpgsqlTypes.NpgsqlDbType.Timestamp, DateTime.UtcNow);
    var legacy = bool.Parse(args[2]);
    try
    {
        await command.ExecuteScalarAsync();
        return legacy ? 0 : 1;
    }
    catch(ArgumentException) when(!legacy) { return 0; }
}

if(args.Length > 0 && string.Equals(args[0], "api",
       StringComparison.Ordinal))
    return await RunApiListenerAsync(args);

if(args.Length == 1 && args[0] == "metrics-registration")
    return await MetricsRegistrationProbe.RunAsync();

if(args.Length == 3 && args[0] == "rpc-tls")
    return await RpcTlsProbe.RunAsync(args[1], args[2]);

if(args.Length == 3 && args[0] == "beam-gpu")
    return await BeamGpuProbe.RunAsync(args[1], args[2]);

if(args.Length == 5 && args[0] == "beam-gpu")
    return await BeamGpuProbe.RunAsync(args[1], args[2], args[3], args[4]);

Console.Error.WriteLine(
    "usage: Miningcore.Tests.ProcessHost hold <recovery-file> <state-directory> <ready-file>\n" +
    "   or: Miningcore.Tests.ProcessHost api <config-file>\n" +
    "   or: Miningcore.Tests.ProcessHost metrics-registration");
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
