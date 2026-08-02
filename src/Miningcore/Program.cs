using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using AspNetCoreRateLimit;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Autofac.Features.Metadata;
using AutoMapper;
using Dapper;
using FluentValidation;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Miningcore.Api;
using Miningcore.Api.Controllers;
using Miningcore.Api.Middlewares;
using Miningcore.Api.Responses;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Crypto.Hashing.Ethash.Etchash;
using Miningcore.Crypto.Hashing.Ethash.Ethash;
using Miningcore.Crypto.Hashing.Ethash.Ethashb3;
using Miningcore.Crypto.Hashing.Ethash.Ubqhash;
using Miningcore.Crypto.Hashing.Progpow.Firopow;
using Miningcore.Crypto.Hashing.Progpow.Kawpow;
using Miningcore.Crypto.Hashing.Progpow.Meowpow;
using Miningcore.Crypto.Hashing.Progpow.Merakipow;
using Miningcore.Crypto.Hashing.Progpow.Phihash;
using Miningcore.Crypto.Hashing.Progpow.ProgpowZ;
using Miningcore.Crypto.Hashing.Progpow.Sccpow;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Native;
using Miningcore.Notifications;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Dummy;
using Miningcore.Persistence.Postgres;
using Miningcore.Persistence.Postgres.Repositories;
using Miningcore.Persistence.Repositories;
using Miningcore.Util;
using NBitcoin.Zcash;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Schema.Generation;
using Newtonsoft.Json.Serialization;
using NLog;
using NLog.Conditions;
using NLog.Config;
using NLog.Extensions.Hosting;
using NLog.Extensions.Logging;
using NLog.Layouts;
using NLog.Targets;
using Prometheus;
using WebSocketManager;
using ILogger = NLog.ILogger;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using static Miningcore.Util.ActionUtils;

// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable PossibleNullReferenceException

[assembly: InternalsVisibleToAttribute("Miningcore.Tests")]

namespace Miningcore;

public class Program : ProcessStatusBackgroundService
{
    private const string ReleaseVersionMetadataKey = "MiningcoreReleaseVersion";
    private const string SourceCommitMetadataKey = "MiningcoreSourceCommit";
    internal const long LogArchiveAboveSize = 512L * 1024L * 1024L;
    internal const int MaxLogArchiveFiles = 4;

    internal static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(45);

    public static async Task<int> Main(string[] args)
    {
        IProcessStatus processStatus = null;

        return await RunStartupBoundaryAsync(async () =>
        {
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            var app = ParseCommandLine(args);

            if(versionOption.HasValue())
            {
                app.ShowVersion();
                return;
            }

            if(dumpConfigOption.HasValue())
            {
                DumpParsedConfig(clusterConfig);
                return;
            }

            if(generateSchemaOption.HasValue())
            {
                GenerateJsonConfigSchema();
                return;
            }

            if(verifyShareRecoveryStateOption.HasValue())
            {
                clusterConfig = configFileOption.HasValue()
                    ? ReadConfig(configFileOption.Value())
                    : new ClusterConfig();
                processStatus = new ProcessStatus();
                var verification = ShareRecoveryIncidentVerifier.Verify(
                    clusterConfig, Console.Out);

                if(!verification.IsSuccessful)
                    processStatus.MarkFailed(
                        ProcessExitCodes.UnreconciledShareDurabilityLoss);

                return;
            }

            if(acknowledgeShareRecoveryStateOption.HasValue())
            {
                clusterConfig = configFileOption.HasValue()
                    ? ReadConfig(configFileOption.Value())
                    : new ClusterConfig();
                processStatus = new ProcessStatus();

                try
                {
                    var state = new ShareRecoveryFatalState(clusterConfig,
                        processStatus);
                    if(!state.Acknowledge(Console.Out))
                        processStatus.MarkFailed(
                            ProcessExitCodes.UnreconciledShareDurabilityLoss);
                }
                catch(Exception ex) when(ex is IOException or
                    InvalidDataException or InvalidOperationException or
                    UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"ACKNOWLEDGEMENT REFUSED: {ex.Message}");
                    processStatus.MarkFailed(
                        ProcessExitCodes.UnreconciledShareDurabilityLoss);
                }

                return;
            }

            if(!configFileOption.HasValue())
            {
                app.ShowHelp();
                return;
            }

            Logo();

            isShareRecoveryMode = shareRecoveryOption.HasValue();
            clusterConfig = ReadConfig(configFileOption.Value());

            ValidateConfig();

            ConfigureLogging();
            LogRuntimeInfo();
            ValidateRuntimeEnvironment();

            var hostBuilder = new HostBuilder();

            hostBuilder
                .UseServiceProviderFactory(new AutofacServiceProviderFactory())
                .ConfigureContainer((Action<ContainerBuilder>) ConfigureAutofac)
                .UseNLog()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddNLog();
                    logging.SetMinimumLevel(LogLevel.Trace);
                })
                .ConfigureServices((ctx, services) =>
                {
                    ConfigureHostShutdown(services);
                    services.AddHttpClient();
                    services.AddMemoryCache();

                    ConfigureBackgroundServices(services);
                });

            if(ShouldConfigureApi(isShareRecoveryMode, clusterConfig.Api))
            {
                var address = clusterConfig.Api?.ListenAddress != null
                    ? (clusterConfig.Api.ListenAddress != "*" ? IPAddress.Parse(clusterConfig.Api.ListenAddress) : IPAddress.Any)
                    : IPAddress.Parse("127.0.0.1");

                var port = clusterConfig.Api?.Port ?? 4000;
                var enableApiRateLimiting = clusterConfig.Api?.RateLimiting?.Disabled != true;
                var apiTlsEnable = clusterConfig.Api?.Tls?.Enabled == true || !string.IsNullOrEmpty(clusterConfig.Api?.Tls?.TlsPfxFile);

                if(apiTlsEnable)
                {
                    if(!File.Exists(clusterConfig.Api.Tls.TlsPfxFile))
                        throw new PoolStartupException($"Certificate file {clusterConfig.Api.Tls.TlsPfxFile} does not exist!");
                }

                hostBuilder.ConfigureWebHost(builder =>
                {
                    builder.ConfigureServices(services =>
                    {
                        // rate limiting
                        if(enableApiRateLimiting)
                        {
                            services.Configure<IpRateLimitOptions>(ConfigureIpRateLimitOptions);
                            services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
                            services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
                            services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
                            services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
                        }

                        // Controllers
                        services.AddSingleton<PoolApiController, PoolApiController>();
                        services.AddSingleton<AdminApiController, AdminApiController>();

                        // MVC
                        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

                        services.AddMvc(options =>
                        {
                            options.EnableEndpointRouting = false;
                        })
                        .AddControllersAsServices()
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.WriteIndented = true;

                            if(!clusterConfig.Api.LegacyNullValueHandling)
                                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                        });

                        // NSwag
                        #if DEBUG
                        services.AddOpenApiDocument(settings =>
                        {
                            settings.DocumentProcessors.Insert(0, new NSwagDocumentProcessor());
                        });
                        #endif

                        services.AddResponseCompression();
                        services.AddCors();
                        services.AddWebSocketManager();
                    })
                    .UseKestrel(options =>
                    {
                        options.Listen(address, port, listenOptions =>
                        {
                            if(apiTlsEnable)
                                listenOptions.UseHttps(clusterConfig.Api.Tls.TlsPfxFile, clusterConfig.Api.Tls.TlsPfxPassword);
                        });
                    })
                    .Configure(app =>
                    {
                        if(enableApiRateLimiting)
                            app.UseIpRateLimiting();

                        app.UseMiddleware<ApiExceptionHandlingMiddleware>();

                        UseIpWhiteList(app, true, new[]
                        {
                            "/api/admin"
                        }, clusterConfig.Api?.AdminIpWhitelist);
                        UseIpWhiteList(app, true, new[]
                        {
                            "/metrics"
                        }, clusterConfig.Api?.MetricsIpWhitelist);

                        #if DEBUG
                        app.UseOpenApi();
                        #endif

                        app.UseResponseCompression();
                        app.UseCors(corsPolicyBuilder => corsPolicyBuilder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
                        app.UseWebSockets();
                        app.MapWebSocketManager("/notifications", app.ApplicationServices.GetService<WebSocketNotificationsRelay>());
                        app.UseMetricServer();

                        app.UseMiddleware<ApiRequestMetricsMiddleware>();

                        app.UseMvc();
                    });

                    logger.Info(() => $"Prometheus Metrics API listening on http{(apiTlsEnable ? "s" : "")}://{address}:{port}/metrics");
                    logger.Info(() => $"WebSocket Events streaming on ws{(apiTlsEnable ? "s" : "")}://{address}:{port}/notifications");
                });
            }

            // ConfigureWebHost registers GenericWebHostService in a later service callback. Add
            // the mining shutdown coordinator only after the optional web host so it is the final
            // hosted service and therefore receives the shared shutdown budget first.
            hostBuilder.ConfigureServices((_, services) =>
                ConfigureMiningShutdownCoordinator(services));

            host = hostBuilder.UseConsoleLifetime().Build();
            processStatus = host.Services.GetRequiredService<IProcessStatus>();

            await PreFlightChecks(host.Services);

            await host.RunAsync();
        }, ReportStartupFailureAsync, () =>
            processStatus != null && processStatus.ExitCode != 0
                ? processStatus.ExitCode
                : Environment.ExitCode);
    }

    private static async Task ReportStartupFailureAsync(Exception exception)
    {
        switch(exception)
        {
            case PoolStartupException ex:
                if(!string.IsNullOrEmpty(ex.Message))
                    await Console.Error.WriteLineAsync(ex.Message);

                await Console.Error.WriteLineAsync("\nCluster cannot start. Good Bye!");
                break;

            case JsonException:
            case IOException:
                // The parser or console already reported these failures.
                break;

            case AggregateException ex:
                if(ex.InnerExceptions.FirstOrDefault() is not PoolStartupException)
                    Console.Error.WriteLine(ex);

                await Console.Error.WriteLineAsync("Cluster cannot start. Good Bye!");
                break;

            default:
                Console.Error.WriteLine(exception);
                await Console.Error.WriteLineAsync("Cluster cannot start. Good Bye!");
                break;
        }
    }

    private static void ConfigureBackgroundServices(IServiceCollection services)
    {
        // Recovery mode resolves ShareRecorder directly for a one-shot import. Starting the normal
        // hosted services here would unnecessarily run payment, statistics, relay and recorder
        // loops alongside that import before the host is stopped.
        if(!ShouldConfigureBackgroundServices(isShareRecoveryMode))
            return;

        services.AddHostedService<NotificationService>();
        services.AddHostedService<BtStreamReceiver>();

        // Share processing
        if(clusterConfig.ShareRelay == null)
        {
            ConfigureShareRecorderHostedService(services);
            services.AddHostedService<ShareReceiver>();
        }

        else
            services.AddHostedService<ShareRelay>();

        // API
        if(clusterConfig.Api == null || clusterConfig.Api.Enabled)
            services.AddHostedService<MetricsPublisher>();

        // Payment processing
        if(ShouldRunPaymentProcessor(clusterConfig))
            services.AddHostedService<PayoutManager>();
        else
            logger.Info("Payment processing is not enabled");

        if(clusterConfig.ShareRelay == null)
        {
            // Pool stats
            services.AddHostedService<StatsRecorder>();
        }
    }

    private static IHost host;
    private readonly IComponentContext container;
    private readonly IHostApplicationLifetime hal;
    private readonly Func<CancellationToken, Task> executeOverride;
    private static ILogger logger;
    private static CommandOption versionOption;
    private static CommandOption configFileOption;
    private static CommandOption dumpConfigOption;
    private static CommandOption shareRecoveryOption;
    private static CommandOption verifyShareRecoveryStateOption;
    private static CommandOption acknowledgeShareRecoveryStateOption;
    private static CommandOption generateSchemaOption;
    private static bool isShareRecoveryMode;
    private static ClusterConfig clusterConfig;
    private static readonly ConcurrentDictionary<string, IMiningPool> pools = new();
    private static readonly AdminGcStats gcStats = new();

    public Program(IComponentContext container, IHostApplicationLifetime hal,
        IProcessStatus processStatus) : base(processStatus)
    {
        this.container = container;
        this.hal = hal;
    }

    internal Program(IComponentContext container, IHostApplicationLifetime hal,
        IProcessStatus processStatus, Func<CancellationToken, Task> executeOverride) :
        this(container, hal, processStatus)
    {
        this.executeOverride = executeOverride;
    }

    private static void ConfigureAutofac(ContainerBuilder builder)
    {
        builder.RegisterAssemblyModules(typeof(AutofacModule).GetTypeInfo().Assembly);
        builder.RegisterInstance(clusterConfig);
        builder.RegisterInstance(pools);
        builder.RegisterInstance(gcStats);

        ConfigureAutoMapper(builder);

        ConfigurePersistence(builder);
    }

    internal static void ConfigureAutoMapper(ContainerBuilder builder)
    {
        builder.Register(ctx => AutoMapperFactory.CreateMapper(
                ctx.Resolve<ILoggerFactory>()))
            .As<IMapper>()
            .SingleInstance();
    }

    protected override async Task ExecuteCoreAsync(CancellationToken ct)
    {
        using var miningShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            ct, hal.ApplicationStopping);
        ct = miningShutdown.Token;

        if(executeOverride != null)
        {
            await executeOverride(ct);
            return;
        }

        if(isShareRecoveryMode)
        {
            await RunRecoveryModeAsync(
                () => RecoverSharesWithBestEffortTemplatesAsync(
                    () =>
                    {
                        // Recovery can commit block-only records and emits their block-found
                        // notification after the transaction succeeds. Coin metadata improves
                        // those notifications, but it must never prevent the journal import.
                        var recoveryCoinTemplates = LoadCoinTemplates();
                        AssignPoolTemplates(clusterConfig.Pools.Where(config => config.Enabled),
                            recoveryCoinTemplates);
                    },
                    () => RecoverSharesAsync(shareRecoveryOption.Value()),
                    ex => logger.Warn(ex, () =>
                        "Coin templates are unavailable during recovery; block notifications may be skipped")),
                hal.StopApplication);

            return;
        }

        if(clusterConfig.InstanceId.HasValue)
            logger.Info($"This is cluster node {clusterConfig.InstanceId.Value}{(!string.IsNullOrEmpty(clusterConfig.ClusterName) ? $" [{clusterConfig.ClusterName}]" : string.Empty)}");

        var coinTemplates = LoadCoinTemplates();
        logger.Info($"{coinTemplates.Keys.Count} coins loaded from '{string.Join(", ", clusterConfig.CoinTemplates)}'");

        var enabledPools = clusterConfig.Pools
            .Where(config => config.Enabled)
            .ToArray();

        await Guard(async () =>
        {
            AssignPoolTemplates(enabledPools, coinTemplates);
            await SupervisePoolLifetimesAsync(enabledPools.Select(config =>
                    new KeyValuePair<string, Func<CancellationToken, Task>>(
                        config.Id, poolCt => RunPool(config, poolCt))),
                ct, ProcessStatus, hal.StopApplication);
        }, ex =>
        {
            switch(ex)
            {
                case PoolStartupException pse:
                {
                    var _logger = pse.PoolId != null ? LogUtil.GetPoolScopedLogger(GetType(), pse.PoolId) : logger;
                    _logger.Error(() => $"{pse.Message}");

                    logger.Error(() => "Cluster cannot start. Good Bye!");

                    StopApplicationAsFailure(ProcessStatus, hal.StopApplication);
                    break;
                }

                default:
                    throw ex;
            }
        });
    }

    internal static bool ShouldConfigureBackgroundServices(bool recoveryMode) => !recoveryMode;

    internal static void StopApplicationAsFailure(IProcessStatus processStatus,
        Action stopApplication)
    {
        processStatus.MarkFailed();
        stopApplication();
    }

    internal static async Task SupervisePoolLifetimesAsync(
        IEnumerable<KeyValuePair<string, Func<CancellationToken, Task>>> poolLifetimes,
        CancellationToken ct, IProcessStatus processStatus, Action stopApplication)
    {
        ArgumentNullException.ThrowIfNull(poolLifetimes);
        ArgumentNullException.ThrowIfNull(processStatus);
        ArgumentNullException.ThrowIfNull(stopApplication);

        // Each task owns fail-fast signalling. Task.WhenAll is retained only to observe and
        // drain every sibling after host cancellation; it must not be the first observer of
        // an individual pool fault because it completes only after every supplied task ends.
        var tasks = poolLifetimes
            .Select(pool => RunPoolFailFastAsync(pool.Key, pool.Value, ct,
                processStatus, stopApplication))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private static async Task RunPoolFailFastAsync(string poolId,
        Func<CancellationToken, Task> runPool, CancellationToken ct,
        IProcessStatus processStatus, Action stopApplication)
    {
        ArgumentNullException.ThrowIfNull(runPool);

        try
        {
            await runPool(ct);
        }

        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // The host deliberately stopped this pool. Swallow its cooperative cancellation
            // so a normal cluster shutdown remains successful while Task.WhenAll drains peers.
            return;
        }

        catch
        {
            StopApplicationAsFailure(processStatus, stopApplication);
            throw;
        }

        if(!ct.IsCancellationRequested)
        {
            // A pool is a lifetime service. Returning while the host is still running leaves
            // the cluster only partially available and must be treated like a startup fault.
            StopApplicationAsFailure(processStatus, stopApplication);
            throw new PoolStartupException(
                $"Pool {poolId} stopped unexpectedly while the cluster was running",
                poolId);
        }
    }

    internal static void ConfigureShareRecorderHostedService(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ShareRecorder is registered as one Autofac component. Resolve that component here
        // instead of letting AddHostedService<T>() create another singleton with independent
        // recovery state and journal locking.
        services.AddHostedService(sp => sp.GetRequiredService<ShareRecorder>());
    }

    internal static void ConfigureMiningShutdownCoordinator(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<Program>();
        services.AddHostedService(sp => sp.GetRequiredService<Program>());
    }

    internal static void ConfigureHostShutdown(IServiceCollection services,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var timeout = shutdownTimeout ?? HostShutdownTimeout;
        if(timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout),
                "Host shutdown timeout must be positive");

        services.TryAddSingleton<IProcessStatus, ProcessStatus>();
        services.TryAddSingleton<IMiningFailStopCoordinator,
            MiningFailStopCoordinator>();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = timeout);
    }

    internal static bool ShouldConfigureApi(bool recoveryMode, ApiConfig api) =>
        !recoveryMode && (api == null || api.Enabled);

    internal static async Task<int> RunStartupBoundaryAsync(Func<Task> run,
        Func<Exception, Task> reportFailure, Func<int> getExitCode = null)
    {
        try
        {
            await run();
            return (getExitCode ?? (() => Environment.ExitCode))();
        }

        catch(Exception ex)
        {
            try
            {
                await reportFailure(ex);
            }

            catch
            {
                // A closed stderr stream must not turn a known startup failure into success.
            }

            var exitCode = (getExitCode ?? (() => Environment.ExitCode))();
            return exitCode != 0 ? exitCode : ProcessExitCodes.GeneralFailure;
        }
    }

    internal static async Task RecoverSharesWithBestEffortTemplatesAsync(
        Action prepareTemplates, Func<Task> recoverShares,
        Action<Exception> warnTemplateFailure)
    {
        try
        {
            prepareTemplates();
        }

        catch(Exception ex)
        {
            warnTemplateFailure(ex);
        }

        await recoverShares();
    }

    internal static async Task RunRecoveryModeAsync(Func<Task> recover,
        Action stopApplication, Action<int> setExitCode = null)
    {
        try
        {
            await recover();
        }

        catch
        {
            (setExitCode ?? (code => Environment.ExitCode = code))(1);
            throw;
        }

        finally
        {
            // Returning from a BackgroundService does not stop the generic host by itself.
            stopApplication();
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        logger?.Info(() => "Stopping mining pools ...");
        await base.StopAsync(ct);
        logger?.Info(() => "Mining pools stopped");
    }

    internal static void AssignPoolTemplates(IEnumerable<PoolConfig> poolConfigs,
        IReadOnlyDictionary<string, CoinTemplate> coinTemplates)
    {
        foreach(var poolConfig in poolConfigs)
        {
            if(!coinTemplates.TryGetValue(poolConfig.Coin, out var template))
                throw new PoolStartupException(
                    $"Pool {poolConfig.Id} references undefined coin '{poolConfig.Coin}'",
                    poolConfig.Id);

            poolConfig.Template = template;
        }
    }

    private async Task RunPool(PoolConfig poolConfig, CancellationToken ct)
    {
        // resolve implementation
        var poolImpl = container.Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .First(x => x.Value.Metadata.SupportedFamilies.Contains(poolConfig.Template.Family)).Value;

        // configure
        var pool = poolImpl.Value;
        pool.Configure(poolConfig, clusterConfig);
        pools[poolConfig.Id] = pool;

        // go
        await pool.RunAsync(ct);
    }

    private Task RecoverSharesAsync(string recoveryFilename)
    {
        var shareRecorder = container.Resolve<ShareRecorder>();
        return shareRecorder.RecoverSharesAsync(recoveryFilename);
    }

    private static void LogRuntimeInfo()
    {
        logger.Info(() => $"Version {GetVersion()}");

        logger.Info(() => $"Runtime {RuntimeInformation.FrameworkDescription.Trim()} on {RuntimeInformation.OSDescription.Trim()} [{RuntimeInformation.ProcessArchitecture}]");
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var releaseVersion = GetAssemblyMetadata(assembly, ReleaseVersionMetadataKey);
        var releaseSha = GetAssemblyMetadata(assembly, SourceCommitMetadataKey);
        var gitVersionInformationType = assembly.GetType("GitVersionInformation");
        string fullSemVer = null;
        string sha = null;

        if(gitVersionInformationType != null)
        {
            fullSemVer = gitVersionInformationType.GetField("FullSemVer")?.GetValue(null)?.ToString();
            sha = gitVersionInformationType.GetField("Sha")?.GetValue(null)?.ToString();
        }

        return SelectVersion(releaseVersion, releaseSha, fullSemVer, sha);
    }

    private static string GetAssemblyMetadata(Assembly assembly, string key)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(x => x.Key == key)?.Value;
    }

    internal static string SelectVersion(string releaseVersion, string releaseSha, string fullSemVer, string sha)
    {
        if(!string.IsNullOrWhiteSpace(releaseVersion) || !string.IsNullOrWhiteSpace(releaseSha))
            return FormatVersion(releaseVersion, releaseSha);

        return FormatVersion(fullSemVer, sha);
    }

    internal static string FormatVersion(string fullSemVer, string sha)
    {
        if(string.IsNullOrWhiteSpace(fullSemVer) || string.IsNullOrWhiteSpace(sha))
            return "unknown";

        return $"{fullSemVer} [{sha}]";
    }

    private static void ValidateConfig()
    {
        if(!clusterConfig.Pools.Any(x => x.Enabled))
            throw new PoolStartupException("No pools are enabled.");

        // set some defaults
        foreach(var config in clusterConfig.Pools)
        {
            config.EnableInternalStratum ??= clusterConfig.ShareRelays == null || clusterConfig.ShareRelays.Length == 0;
        }

        try
        {
            clusterConfig.Validate();
            ValidateMergedMiningDeployment(clusterConfig);

            if(clusterConfig.Notifications?.Admin?.Enabled == true)
            {
                if(string.IsNullOrEmpty(clusterConfig.Notifications?.Email?.FromName))
                    throw new PoolStartupException($"Notifications are enabled but email sender name is not configured (notifications.email.fromName)");

                if(string.IsNullOrEmpty(clusterConfig.Notifications?.Email?.FromAddress))
                    throw new PoolStartupException($"Notifications are enabled but email sender address name is not configured (notifications.email.fromAddress)");

                if(string.IsNullOrEmpty(clusterConfig.Notifications?.Admin?.EmailAddress))
                    throw new PoolStartupException($"Admin notifications are enabled but recipient address is not configured (notifications.admin.emailAddress)");
            }

            if(string.IsNullOrEmpty(clusterConfig.Logging.LogFile))
            {
                // emit a newline before regular logging output starts
                Console.WriteLine();
            }
        }

        catch(ValidationException ex)
        {
            Console.Error.WriteLine($"Configuration is not valid:\n\n{string.Join("\n", ex.Errors.Select(x => "=> " + x.ErrorMessage))}");
            throw new PoolStartupException(string.Empty);
        }
    }

    private static void DumpParsedConfig(ClusterConfig config)
    {
        Console.WriteLine("\nCurrent configuration as parsed from config file:");

        Console.WriteLine(JsonConvert.SerializeObject(config, new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.Indented
        }));
    }

    private static void GenerateJsonConfigSchema()
    {
        var filename = generateSchemaOption.Value();

        var generator = new JSchemaGenerator
        {
            DefaultRequired = Required.Default,
            SchemaPropertyOrderHandling = SchemaPropertyOrderHandling.Alphabetical,
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            GenerationProviders =
            {
                new StringEnumGenerationProvider()
            }
        };

        var schema = generator.Generate(typeof(ClusterConfig));

        using(var stream = File.Create(filename))
        {
            using(var writer = new JsonTextWriter(new StreamWriter(stream, Encoding.UTF8)))
            {
                schema.WriteTo(writer);

                writer.Flush();
            }
        }
    }

    private static CommandLineApplication ParseCommandLine(string[] args)
    {
        var app = new CommandLineApplication
        {
            FullName = "Miningcore",
            ShortVersionGetter = GetVersion,
            LongVersionGetter = GetVersion
        };

        versionOption = app.Option("-v|--version", "Version Information", CommandOptionType.NoValue);
        configFileOption = app.Option("-c|--config <configfile>", "Configuration File", CommandOptionType.SingleValue);
        dumpConfigOption = app.Option("-dc|--dumpconfig", "Dump the configuration (useful for trouble-shooting typos in the config file)",CommandOptionType.NoValue);
        shareRecoveryOption = app.Option("-rs", "Import lost shares using existing recovery file", CommandOptionType.SingleValue);
        verifyShareRecoveryStateOption = app.Option("--verify-share-recovery-state",
            "Read-only verification of fatal share-recovery incidents and exact-share sidecars",
            CommandOptionType.NoValue);
        acknowledgeShareRecoveryStateOption = app.Option(
            "--acknowledge-share-recovery-state",
            "After database reconciliation, verify and durably acknowledge fatal share-recovery evidence",
            CommandOptionType.NoValue);
        generateSchemaOption = app.Option("-gcs|--generate-config-schema <outputfile>", "Generate JSON schema from configuration options", CommandOptionType.SingleValue);
        app.HelpOption("-? | -h | --help");

        app.Execute(args);

        return app;
    }

    private static ClusterConfig ReadConfig(string file)
    {
        try
        {
            Console.WriteLine($"Using configuration file '{file}'");

            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            });

            using(var reader = new StreamReader(file, Encoding.UTF8))
            {
                using(var jsonReader = new JsonTextReader(reader))
                {
                    using(var validatingReader = new JSchemaValidatingReader(jsonReader)
                    {
                        Schema =  LoadSchema()
                    })
                    {
                        return serializer.Deserialize<ClusterConfig>(validatingReader);
                    }
                }
            }
        }

        catch(JSchemaValidationException ex)
        {
            throw new PoolStartupException($"Configuration file error: {ex.Message}");
        }

        catch(JsonSerializationException ex)
        {
            throw new PoolStartupException($"Configuration file error: {ex.Message}");
        }

        catch(JsonException ex)
        {
            throw new PoolStartupException($"Configuration file error: {ex.Message}");
        }

        catch(IOException ex)
        {
            throw new PoolStartupException($"Configuration file error: {ex.Message}");
        }
    }

    private static JSchema LoadSchema()
    {
        var basePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        var path = Path.Combine(basePath, "config.schema.json");

        using(var reader = new JsonTextReader(new StreamReader(File.OpenRead(path))))
        {
            return JSchema.Load(reader);
        }
    }

    private static void ValidateRuntimeEnvironment()
    {
        // root check
        if(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.UserName == "root")
            logger.Warn(() => "Running as root is discouraged!");

        // require 64-bit on Windows
        if(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.ProcessArchitecture == Architecture.X86)
            throw new PoolStartupException("Miningcore requires 64-Bit Windows");
    }

    private static void Logo()
    {
        Console.WriteLine(@"
 ███╗   ███╗██╗███╗   ██╗██╗███╗   ██╗ ██████╗  ██████╗ ██████╗ ██████╗ ███████╗
 ████╗ ████║██║████╗  ██║██║████╗  ██║██╔════╝ ██╔════╝██╔═══██╗██╔══██╗██╔════╝
 ██╔████╔██║██║██╔██╗ ██║██║██╔██╗ ██║██║  ███╗██║     ██║   ██║██████╔╝█████╗
 ██║╚██╔╝██║██║██║╚██╗██║██║██║╚██╗██║██║   ██║██║     ██║   ██║██╔══██╗██╔══╝
 ██║ ╚═╝ ██║██║██║ ╚████║██║██║ ╚████║╚██████╔╝╚██████╗╚██████╔╝██║  ██║███████╗
");
        Console.WriteLine(" https://github.com/NINJAK1DD/miningcore\n");
        Console.WriteLine(" Upstream Miningcore donation addresses:\n");
        Console.WriteLine(" ETH   - 0xbC059e88A4dD11c2E882Fc6B83F8Ec12E4CCCFad");
        Console.WriteLine(" BTC   - 16xvkGfG9nrJSKKo5nGWphP8w4hr2ZzVuw");
        Console.WriteLine(" LTC   - LLs76baYT7iMqQhizxtBC96Cy48iX3Eh1p");
        Console.WriteLine(" DOGE  - DFuvDSFh4N3SiXGDnye2Vbc8kqvMHbyQE1");
        Console.WriteLine(" KAS   - kaspa:qpmf0wyu7c5z4l82ax9cfc5ughwk2f9lgu8uckkqrrpjqkxuk7yrga5nntvgn");
        Console.WriteLine(" CCX   - ccx7S4B3gBeH1SGWCfqZp3NM7Vavg7H3S8ovJn8fU4bwC4vU7ChWfHtbNzifhrpbJ74bMDxj4KZFTcznTfsucCEg1Kgv7zbNgs");
        Console.WriteLine(" FIRO  - a5AsoTSkfPHQ3SUmR6binG1XW7oQQoFNU1");
        Console.WriteLine(" ERGO  - 9gYyuZzaSw3TiCtUkSRuS3XVDUv41EFs3dtNCFGqiEwHqpb7gkF");
        Console.WriteLine(" WART  - 7795fc0fe93e7e4e232a212f00bdc8885c580a5666d39a0d");
        Console.WriteLine(" XMR   - 483zaHtMRfM7rw1dXgebhWaRR8QLgAF6w4BomAV319FVVHfdbYTLVuBRc4pQgRAnRpfy6CXvvwngK4Lo3mRKE29RRx3Jb5c");
        Console.WriteLine(" XEL   - xel:ajnsfv065qusndt0hfsngecrnf5690drmqmc0uq0etlx8zjlcyzqq2slgvt");
        Console.WriteLine(" CTXC  - 0xbb60200d5151a4a0f9a75014e04cf61a0a9f0daf");
        Console.WriteLine(" ZANO  - ZxDKT1aqiEXPA5cDADtYEfMR1oXsRd68bby4nzUvVmnjHzzrfvjwhNdQ9yiWNeGutzg9LZdwsbP2FGB1gNpZXiYY1fCfpw33c");
        Console.WriteLine(" SCASH - scash1qe6dhv8kncz08jtqukyps4l2n83z2umewanlmas");
        Console.WriteLine();
    }

    private static void ConfigureLogging()
    {
        var config = clusterConfig.Logging;
        var loggingConfig = new LoggingConfiguration();

        if(config != null)
        {
            // parse level
            var level = !string.IsNullOrEmpty(config.Level)
            ? NLog.LogLevel.FromString(config.Level)
            : NLog.LogLevel.Info;

            var layout = "[${longdate}] [${level:format=FirstCharacter:uppercase=true}] [${logger:shortName=true}] ${message} ${exception:format=ToString,StackTrace}";

            var nullTarget = new NullTarget("null");

            loggingConfig.AddTarget(nullTarget);

            // Suppress some log spam
            loggingConfig.AddRule(level, NLog.LogLevel.Info, nullTarget, "Microsoft.AspNetCore.Mvc.Internal.*", true);
            loggingConfig.AddRule(level, NLog.LogLevel.Info, nullTarget, "Microsoft.AspNetCore.Mvc.Infrastructure.*", true);
            loggingConfig.AddRule(level, NLog.LogLevel.Warn, nullTarget, "System.Net.Http.HttpClient.*", true);
            loggingConfig.AddRule(level, NLog.LogLevel.Fatal, nullTarget, "Microsoft.Extensions.Hosting.Internal.*", true);

            // Api Log
            if(!string.IsNullOrEmpty(config.ApiLogFile) && !isShareRecoveryMode)
            {
                var target = CreateFileTarget("api-file", GetLogPath(config, config.ApiLogFile), layout);

                loggingConfig.AddTarget(target);
                loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target, "Microsoft.AspNetCore.*", true);
            }

            if(config.EnableConsoleLog || isShareRecoveryMode)
            {
                if(config.EnableConsoleColors)
                {
                    var target = new ColoredConsoleTarget("console")
                    {
                        Layout = layout
                    };

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Trace"),
                    ConsoleOutputColor.DarkMagenta, ConsoleOutputColor.NoChange));

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Debug"),
                    ConsoleOutputColor.Gray, ConsoleOutputColor.NoChange));

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Info"),
                    ConsoleOutputColor.White, ConsoleOutputColor.NoChange));

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Warn"),
                    ConsoleOutputColor.Yellow, ConsoleOutputColor.NoChange));

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Error"),
                    ConsoleOutputColor.Red, ConsoleOutputColor.NoChange));

                    target.RowHighlightingRules.Add(new ConsoleRowHighlightingRule(
                    ConditionParser.ParseExpression("level == LogLevel.Fatal"),
                    ConsoleOutputColor.DarkRed, ConsoleOutputColor.White));

                    loggingConfig.AddTarget(target);
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
                }

                else
                {
                    var target = new ConsoleTarget("console")
                    {
                        Layout = layout
                    };

                    loggingConfig.AddTarget(target);
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
                }
            }

            if(!string.IsNullOrEmpty(config.LogFile) && !isShareRecoveryMode)
            {
                var target = CreateFileTarget("main-file", GetLogPath(config, config.LogFile), layout);

                loggingConfig.AddTarget(target);
                loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
            }

            if(config.PerPoolLogFile && !isShareRecoveryMode)
            {
                foreach(var poolConfig in clusterConfig.Pools)
                {
                    var target = CreateFileTarget($"pool-{poolConfig.Id}-file",
                        GetLogPath(config, poolConfig.Id + ".log"), layout);

                    loggingConfig.AddTarget(target);
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target, poolConfig.Id);
                }
            }
        }

        LogManager.Configuration = loggingConfig;

        logger = LogManager.GetLogger("Core");
    }

    internal static FileTarget CreateFileTarget(string targetName, Layout fileName, string layout)
    {
        return new FileTarget(targetName)
        {
            FileName = fileName,
            FileNameKind = FilePathKind.Unknown,
            Layout = layout,
            ArchiveAboveSize = LogArchiveAboveSize,
            MaxArchiveFiles = MaxLogArchiveFiles,
            ArchiveOldFileOnStartup = false
        };
    }

    private static Layout GetLogPath(ClusterLoggingConfig config, string name)
    {
        if(string.IsNullOrEmpty(config.LogBaseDirectory))
            return name;

        return Path.Combine(config.LogBaseDirectory, name);
    }

    private static async Task PreFlightChecks(IServiceProvider services)
    {
        if(UsesLocalShareRecoveryPath(isShareRecoveryMode, clusterConfig))
            services.GetRequiredService<IShareRecoveryPathOwnership>().Acquire();

        if(ShouldValidateShareRecoveryState(isShareRecoveryMode, clusterConfig))
            services.GetRequiredService<IShareRecoveryFatalState>()
                .EnsureStartupAllowed();

        await ConfigurePostgresCompatibilityOptions(services);

        await EnsureSharePartitionsAsync(isShareRecoveryMode, clusterConfig,
            services.GetService<IConnectionFactory>(),
            services.GetService<IShareRepository>(), CancellationToken.None);

        await EnsureShareRecoverySchemaAsync(isShareRecoveryMode, clusterConfig,
            services.GetService<IConnectionFactory>(),
            services.GetService<IShareRepository>(), CancellationToken.None);

        if(RequiresMergedMiningPersistence(clusterConfig))
        {
            await EnsureMergedMiningSchemaAsync(clusterConfig,
                services.GetService<IConnectionFactory>(),
                services.GetService<IBlockRepository>(), CancellationToken.None);
        }

        ZcashNetworks.Instance.EnsureRegistered();

        var messageBus = services.GetService<IMessageBus>();
        var rmsm = services.GetService<RecyclableMemoryStreamManager>();

        // Configure RecyclableMemoryStream
        var rmsmOptions = rmsm.Settings;
        rmsmOptions.MaximumSmallPoolFreeBytes = clusterConfig.Memory?.RmsmMaximumFreeSmallPoolBytes ?? 0x100000;   // 1 MB
        rmsmOptions.MaximumLargePoolFreeBytes = clusterConfig.Memory?.RmsmMaximumFreeLargePoolBytes ?? 0x800000;   // 8 MB
        rmsm = new RecyclableMemoryStreamManager(rmsmOptions);

        // Configure Equihash
        EquihashSolver.messageBus = messageBus;
        EquihashSolver.MaxThreads = clusterConfig.EquihashMaxThreads ?? 1;

        // Configure Ethhash
        Miningcore.Crypto.Hashing.Ethash.Ethash.Cache.messageBus = messageBus;

        // Configure Etchash
        Miningcore.Crypto.Hashing.Ethash.Etchash.Cache.messageBus = messageBus;
        
        // Configure Ethashb3
        Miningcore.Crypto.Hashing.Ethash.Ethashb3.Cache.messageBus = messageBus;

        // Configure Ubqhash
        Miningcore.Crypto.Hashing.Ethash.Ubqhash.Cache.messageBus = messageBus;

        // Configure Verthash
        Verthash.messageBus = messageBus;

        // Configure Cryptonight
        Cryptonight.messageBus = messageBus;
        Cryptonight.InitContexts(GetDefaultConcurrency(clusterConfig.CryptonightMaxThreads));

        // Configure RandomX
        RandomX.messageBus = messageBus;

        // Configure RandomARQ
        RandomARQ.messageBus = messageBus;

        // Configure Panthera
        Panthera.messageBus = messageBus;

        // Configure RandomXSCash
        RandomXSCash.messageBus = messageBus;

        // Configure NexaPow
        Miningcore.Crypto.Hashing.Algorithms.NexaPow.messageBus = messageBus;

        // Configure AstroBWTv3
        AstroBWTv3.messageBus = messageBus;

        // Configure BeamHash
        BeamHash.messageBus = messageBus;

        // Configure CortexCuckooCycle
        CortexCuckooCycle.messageBus = messageBus;

        // Configure Firopow
        Miningcore.Crypto.Hashing.Progpow.Firopow.Cache.messageBus = messageBus;
        
        // Configure Kawpow
        Miningcore.Crypto.Hashing.Progpow.Kawpow.Cache.messageBus = messageBus;

        // Configure Meowpow
        Miningcore.Crypto.Hashing.Progpow.Meowpow.Cache.messageBus = messageBus;

        // Configure Merakipow
        Miningcore.Crypto.Hashing.Progpow.Merakipow.Cache.messageBus = messageBus;

        // Configure Phihash
        Miningcore.Crypto.Hashing.Progpow.Phihash.Cache.messageBus = messageBus;

        // Configure ProgpowZ
        Miningcore.Crypto.Hashing.Progpow.ProgpowZ.Cache.messageBus = messageBus;

        // Configure SccPow
        Miningcore.Crypto.Hashing.Progpow.Sccpow.Cache.messageBus = messageBus;
    }

    internal static bool ShouldValidateShareRecoveryState(bool recoveryMode,
        ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return !recoveryMode;
    }

    internal static bool UsesLocalShareRecoveryPath(bool recoveryMode,
        ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return recoveryMode || config.ShareRelay == null ||
            RequiresMergedMiningPersistence(config);
    }

    internal static async Task EnsureSharePartitionsAsync(bool recoveryMode,
        ClusterConfig config, IConnectionFactory cf, IShareRepository shareRepo,
        CancellationToken ct)
    {
        // A relay sender publishes ordinary shares instead of recording them locally. It may
        // still have PostgreSQL solely to own payout processing, so do not require local share
        // partitions unless this is an explicit recovery import.
        var writesSharesLocally = recoveryMode || config?.ShareRelay == null;

        if(config?.Persistence?.Postgres == null || !writesSharesLocally)
            return;

        if(cf == null || shareRepo == null)
            throw new PoolStartupException(
                "PostgreSQL share persistence is configured but its repository services are unavailable.");

        var poolIds = config.Pools?
            .Where(x => x.Enabled)
            .Select(x => x.Id)
            .ToArray() ?? Array.Empty<string>();

        var missing = await cf.Run(con =>
            shareRepo.GetMissingSharePartitionsAsync(con, poolIds, ct)) ??
            Array.Empty<string>();

        if(missing.Length == 0)
            return;

        var formattedPoolIds = string.Join(", ", missing
            .Select(x => JsonConvert.SerializeObject(x)));
        var failureBoundary = recoveryMode
            ? "The recovery journal has not been imported."
            : "Startup stopped before the share recorder or Stratum opened.";

        throw new PoolStartupException(
            $"The partitioned PostgreSQL shares table has no partition for enabled pool ID(s): " +
            $"{formattedPoolIds}. Create one LIST partition per pool before starting Miningcore " +
            "or importing a recovery journal. See 'Advanced share-table partitioning' in " +
            $"docs/database.md. {failureBoundary}");
    }

    internal static async Task EnsureShareRecoverySchemaAsync(bool recoveryMode,
        ClusterConfig config, IConnectionFactory cf, IShareRepository shareRepo,
        CancellationToken ct)
    {
        if(!recoveryMode)
            return;

        if(config?.Persistence?.Postgres == null || cf == null || shareRepo == null)
            throw new PoolStartupException(
                "Share recovery requires PostgreSQL persistence and the share_recovery_imports schema. " +
                "Configure PostgreSQL and apply add_payout_manager_ownership.sql before rerunning -rs.");

        using var con = await cf.OpenConnectionAsync();

        if(!await shareRepo.HasRecoveryImportSchemaAsync(con, ct))
            throw new PoolStartupException(
                "Share recovery schema is missing or malformed. Apply " +
                "src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql " +
                "before rerunning -rs; the recovery journal has not been imported.");
    }

    private static async Task ConfigurePostgresCompatibilityOptions(IServiceProvider services)
    {
        if(clusterConfig.Persistence?.Postgres == null)
            return;

        var cf = services.GetService<IConnectionFactory>();

        bool enableLegacyTimestampBehavior = false;

        if(!clusterConfig.Persistence.Postgres.EnableLegacyTimestamps.HasValue)
        {
            // check if 'shares.created' is legacy timestamp (without timezone)
            var columnType = await GetPostgresColumnType(cf, "shares", "created");

            if(columnType != null)
                enableLegacyTimestampBehavior = columnType.ToLower().Contains("without time zone");
            else
                logger.Warn(() => "Unable to auto-detect Npgsql Legacy Timestamp Behavior. Please set 'EnableLegacyTimestamps' in your Miningcore Database configuration to'true' or 'false' to bypass auto-detection in case of problems");
        }

        else
            enableLegacyTimestampBehavior = clusterConfig.Persistence.Postgres.EnableLegacyTimestamps.Value;

        if(enableLegacyTimestampBehavior)
        {
            logger.Info(()=> "Enabling Npgsql Legacy Timestamp Behavior");

            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }
    }

    internal static bool RequiresMergedMiningPersistence(ClusterConfig config)
    {
        var mergedMiningEnabled = config?.Pools?.Any(pool =>
            pool.Enabled && MergedMiningConfigLoader.GetNormalizedConfig(pool)?.Enabled == true) == true;

        // Every merged-mining submission node persists block-only results synchronously before
        // returning to the miner. Ordinary shares may still use a database-free relay topology.
        return mergedMiningEnabled;
    }

    internal static bool ShouldRunPaymentProcessor(ClusterConfig config)
    {
        var paymentEnabled = config?.PaymentProcessing?.Enabled == true &&
            config.Pools?.Any(x => x.Enabled && x.PaymentProcessing?.Enabled == true) == true;

        if(!paymentEnabled)
            return false;

        return config.ShareRelay == null || config.Persistence?.Postgres != null;
    }

    internal static void ValidateMergedMiningDeployment(ClusterConfig config)
    {
        var enabledMergedMining = config?.Pools?
            .Where(pool => pool.Enabled)
            .Select(MergedMiningConfigLoader.GetNormalizedConfig)
            .Where(x => x?.Enabled == true)
            .ToArray() ?? Array.Empty<MergedMiningConfig>();

        if(!RequiresMergedMiningPersistence(config))
            return;

        if(config.Persistence?.Postgres == null)
            throw new PoolStartupException(
                "Litecoin-Dogecoin merged mining requires PostgreSQL on every submitting node so accepted and uncertain block candidates are persisted synchronously. Database-free share-relay senders remain supported for non-merged pools.");

        if(config.ShareRelay == null && config.PaymentProcessing?.Enabled != true)
            throw new PoolStartupException(
                "Litecoin-Dogecoin merged mining requires cluster-level payment processing on direct and share-relay receiver/recorder nodes so accepted and uncertain blocks are reconciled.");
    }

    internal static async Task EnsureMergedMiningSchemaAsync(ClusterConfig config,
        IConnectionFactory cf, IBlockRepository blockRepo, CancellationToken ct)
    {
        if(!RequiresMergedMiningPersistence(config))
            return;

        var schemaReady = await cf.Run(con =>
            blockRepo.HasMergedMiningBlockIndexesAsync(con, ct));

        if(!schemaReady)
            throw new PoolStartupException(
                "Merged mining requires the AuxPoW block idempotency migration. Apply add_auxpow_block_idempotency.sql before enabling Litecoin-Dogecoin merged mining.");
    }

    private static async Task<string> GetPostgresColumnType(IConnectionFactory cf, string table, string column)
    {
        const string query = "SELECT data_type FROM information_schema.columns WHERE table_name = @table AND column_name = @column";

        return await cf.Run(async con => await con.ExecuteScalarAsync<string>(query, new { table, column }));
    }

    private static void ConfigurePersistence(ContainerBuilder builder)
    {
        if(clusterConfig.Persistence == null &&
           clusterConfig.PaymentProcessing?.Enabled == true &&
           clusterConfig.ShareRelay == null)
            throw new PoolStartupException("Persistence is not configured!");

        if(clusterConfig.Persistence?.Postgres != null)
            ConfigurePostgres(clusterConfig.Persistence.Postgres, builder);
        else
            ConfigureDummyPersistence(builder);
    }

    private static void ConfigurePostgres(PostgresConfig pgConfig, ContainerBuilder builder)
    {
        // validate config
        if(string.IsNullOrEmpty(pgConfig.Host))
            throw new PoolStartupException("Postgres configuration: invalid or missing 'host'");

        if(pgConfig.Port == 0)
            throw new PoolStartupException("Postgres configuration: invalid or missing 'port'");

        if(string.IsNullOrEmpty(pgConfig.Database))
            throw new PoolStartupException("Postgres configuration: invalid or missing 'database'");

        if(string.IsNullOrEmpty(pgConfig.User))
            throw new PoolStartupException("Postgres configuration: invalid or missing 'user'");

        // build connection string
        var connectionString = new StringBuilder($"Server={pgConfig.Host};Port={pgConfig.Port};Database={pgConfig.Database};User Id={pgConfig.User};Password={pgConfig.Password};");

        if(pgConfig.Tls)
        {
            connectionString.Append("SSL Mode=Require;");

            if(pgConfig.TlsNoValidate)
                connectionString.Append("Trust Server Certificate=true;");

            if(!string.IsNullOrEmpty(pgConfig.TlsCert?.Trim()))
                connectionString.Append($"SSL Certificate={pgConfig.TlsCert.Trim()};");

            if(!string.IsNullOrEmpty(pgConfig.TlsKey?.Trim()))
                connectionString.Append($"SSL Key={pgConfig.TlsKey.Trim()};");

            if(!string.IsNullOrEmpty(pgConfig.TlsPassword))
                connectionString.Append($"SSL Password={pgConfig.TlsPassword};");
        }

        connectionString.Append($"CommandTimeout={pgConfig.CommandTimeout ?? 300};");

        logger.Debug(()=> $"Using postgres connection string: {connectionString}");

        // register connection factory
        builder.RegisterInstance(new PgConnectionFactory(connectionString.ToString()))
            .AsImplementedInterfaces();

        // register repositories
        builder.RegisterAssemblyTypes(Assembly.GetExecutingAssembly())
            .Where(t =>
                t?.Namespace?.StartsWith(typeof(ShareRepository).Namespace) == true)
            .AsImplementedInterfaces()
            .SingleInstance();
    }

    private static void ConfigureDummyPersistence(ContainerBuilder builder)
    {
        // register connection factory
        builder.RegisterInstance(new DummyConnectionFactory(string.Empty))
            .AsImplementedInterfaces();

        // register repositories
        builder.RegisterAssemblyTypes(Assembly.GetExecutingAssembly())
            .Where(t => t?.Namespace?.StartsWith(typeof(ShareRepository).Namespace) == true)
            .AsImplementedInterfaces()
            .SingleInstance();
    }

    private Dictionary<string, CoinTemplate> LoadCoinTemplates()
    {
        var basePath = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        var defaultTemplates = Path.Combine(basePath, "coins.json");

        // make sure default templates are loaded first
        clusterConfig.CoinTemplates = new[]
        {
            defaultTemplates
        }
        .Concat(clusterConfig.CoinTemplates != null ? clusterConfig.CoinTemplates.Where(x => x != defaultTemplates) : Array.Empty<string>())
        .ToArray();

        return CoinTemplateLoader.Load(container, clusterConfig.CoinTemplates);
    }

    private static void UseIpWhiteList(IApplicationBuilder app, bool defaultToLoopback, string[] locations, string[] whitelist)
    {
        var ipList = whitelist?.Select(IPAddress.Parse).ToList();
        if(defaultToLoopback && (ipList == null || ipList.Count == 0))
            ipList = new List<IPAddress>(new[]
            {
                IPAddress.Loopback, IPAddress.IPv6Loopback, IPUtils.IPv4LoopBackOnIPv6
            });

        if(ipList.Count > 0)
        {
            // always allow access by localhost
            if(!ipList.Any(x => x.Equals(IPAddress.Loopback)))
                ipList.Add(IPAddress.Loopback);
            if(!ipList.Any(x => x.Equals(IPAddress.IPv6Loopback)))
                ipList.Add(IPAddress.IPv6Loopback);
            if(!ipList.Any(x => x.Equals(IPUtils.IPv4LoopBackOnIPv6)))
                ipList.Add(IPUtils.IPv4LoopBackOnIPv6);

            logger.Info(() => $"API Access to {string.Join(",", locations)} restricted to {string.Join(",", ipList.Select(x => x.ToString()))}");

            app.UseMiddleware<IPAccessWhitelistMiddleware>(locations, ipList.ToArray(), clusterConfig.Logging.GPDRCompliant);
        }
    }

    private static void ConfigureIpRateLimitOptions(IpRateLimitOptions options)
    {
        options.EnableEndpointRateLimiting = false;

        // exclude admin api and metrics from throtteling
        options.EndpointWhitelist = new List<string>
        {
            "*:/api/admin",
            "get:/metrics",
            "*:/notifications",
        };

        options.IpWhitelist = clusterConfig.Api?.RateLimiting?.IpWhitelist?.ToList();

        // default to whitelist localhost if whitelist absent
        if(options.IpWhitelist == null || options.IpWhitelist.Count == 0)
        {
            options.IpWhitelist = new List<string>
            {
                IPAddress.Loopback.ToString(),
                IPAddress.IPv6Loopback.ToString(),
                IPUtils.IPv4LoopBackOnIPv6.ToString()
            };
        }

        // limits
        var rules = clusterConfig.Api?.RateLimiting?.Rules?.ToList();

        if(rules == null || rules.Count == 0)
        {
            rules = new List<RateLimitRule>
            {
                new()
                {
                    Endpoint = "*",
                    Period = "1s",
                    Limit = 5,
                }
            };
        }

        options.GeneralRules = rules;

        logger.Info(() => $"API access limited to {(string.Join(", ", rules.Select(x => $"{x.Limit} requests per {x.Period}")))}, except from {string.Join(", ", options.IpWhitelist)}");
    }

    private static int GetDefaultConcurrency(int? value)
    {
        value = value switch
        {
            null => 1,
            -1 => Environment.ProcessorCount,
            _ => value
        };

        return value.Value;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if(logger != null)
        {
            logger.Error(e.ExceptionObject);
            LogManager.Flush(TimeSpan.Zero);
        }

        Console.Error.WriteLine("** AppDomain unhandled exception: {0}", e.ExceptionObject);
    }
}
