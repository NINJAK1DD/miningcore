using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Miningcore.Api;
using Miningcore.Api.Controllers;
using Miningcore.Api.Extensions;
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
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin.Zcash;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
[assembly: InternalsVisibleToAttribute("Miningcore.Tests.ProcessHost")]

namespace Miningcore;

public class Program : ProcessStatusBackgroundService
{
    internal const int DefaultApiPort = ApiConfig.DefaultPort;
    internal const string MetricsRoutePrefix =
        ProtectedRouteClassifier.MetricsRoutePrefix;
    private const string ReleaseVersionMetadataKey = "MiningcoreReleaseVersion";
    private const string SourceCommitMetadataKey = "MiningcoreSourceCommit";
    internal const long LogArchiveAboveSize = 512L * 1024L * 1024L;
    internal const int MaxLogArchiveFiles = 4;

    internal static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(45);
    private static readonly (string Symbol, string Address)[] DonationAddresses =
    {
        ("BTC", "bc1q94x9ncw62g09c80yr38jkewyn6cre3h473g54j"),
        ("ETH", "0x4DE55672F0bBB88882A5a589b320eE40FfbdebF9"),
        ("DOGE", "DQKEyZ2sTzcCPeeqzP4xUiPHzwtCS9LUTt"),
        ("ZEC", "t1TbjCnoNdGWnwEt9QqCZvHuG3MsWf4Bj66"),
        ("XMR", "43iiCs5pjvqbzYDvGSPgwtTdR4E4s996cSBsCSTe5HHbSrzr4HBosKZch8t7Fpg34" +
            "DL9dNcN22T7H6JWEC23B9iDLAZqQsp"),
        ("BCH", "bitcoincash:qzyvaurh8vlj22jvyhpdce6ld4lt3zfc3svyt665de"),
        ("LTC", "ltc1qgnt28drw663gldx76zp3s28xl58wsp0ccv4vxg"),
        ("KAS", "kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu"),
        ("ETC", "0x331e6c8d7Caae3Dd1136EefF6c828dBDe5ae64F0"),
        ("FIRO", "aH1tURoFqY1quNraAtceE6YFPv3DLFo8zT"),
        ("XEL", "xel:gt8m2j4al22k8ecp99uducy84vnhn2nlx6ftxjgw2rfr0hg5n47sqkec7n4"),
        ("WART", "4701843e274a2a4dfbac59678cb693233274bf5fefcc4e46"),
    };
    private static readonly AdminApiCredentialProvider adminApiCredentialProvider =
        new();
    private static readonly HashSet<string> RecoveryConfigurationProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "coinTemplates",
            "logging",
            "persistence",
            "pools",
            "shareRecoveryFile",
            "shareRecoveryStateDirectory",
        };

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
                    ? ReadConfig(configFileOption.Value(), true)
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
                    ? ReadConfig(configFileOption.Value(), true)
                    : new ClusterConfig();
                processStatus = new ProcessStatus();

                try
                {
                    // Acknowledgement mutates durable recovery evidence. Participate in the same
                    // native process-lifetime boundary as recording and import so disabling .NET
                    // managed file locking cannot permit it to race a live owner.
                    using var recoveryPathOwnership =
                        new ShareRecoveryPathOwnership(clusterConfig);
                    recoveryPathOwnership.Acquire();
                    var state = new ShareRecoveryFatalState(clusterConfig,
                        processStatus, recoveryPathOwnership);
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
            clusterConfig = ReadAndValidateConfig(configFileOption.Value(),
                isShareRecoveryMode);
            var apiConfig = clusterConfig.Api;

            ConfigureLogging();
            LogSkippedStratumListenerValidation(clusterConfig);
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

            if(ShouldConfigureApi(isShareRecoveryMode, apiConfig))
            {
                var address = ResolveListenAddress(apiConfig.ListenAddress);

                var endpointPorts = ResolveApiEndpointPorts(apiConfig);
                var enableApiRateLimiting = apiConfig.RateLimiting?.Disabled != true;
                var apiTlsEnable = apiConfig.Tls?.Enabled == true ||
                    !string.IsNullOrEmpty(apiConfig.Tls?.TlsPfxFile);
                // The process constructs one API host and reads the credential once.
                // Token rotation therefore requires the documented service/container restart.
                var adminApiCredential = GetAdminApiCredential();
                var gpdrCompliantLogging = clusterConfig.Logging?.GPDRCompliant == true;

                if(apiTlsEnable)
                {
                    if(!File.Exists(apiConfig.Tls.TlsPfxFile))
                        throw new PoolStartupException($"Certificate file {apiConfig.Tls.TlsPfxFile} does not exist!");
                }

                hostBuilder.ConfigureWebHost(builder =>
                {
                    builder.ConfigureServices(services =>
                    {
                        // rate limiting
                        if(enableApiRateLimiting)
                            AddApiRateLimiting(services, apiConfig);

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
                            ConfigureApiJsonSerializerOptions(
                                options.JsonSerializerOptions,
                                apiConfig.LegacyNullValueHandling);
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
                        ConfigureApiListeners(options, address, endpointPorts,
                            listenOptions =>
                            {
                                if(apiTlsEnable)
                                    listenOptions.UseHttps(apiConfig.Tls.TlsPfxFile,
                                        apiConfig.Tls.TlsPfxPassword);
                            });
                    })
                    .Configure(app =>
                    {
                        ConfigureApiPipeline(app, endpointPorts,
                            apiConfig.AdminIpWhitelist,
                            apiConfig.MetricsIpWhitelist,
                            adminApiCredential, gpdrCompliantLogging,
                            new ApiPipelineOptions(
                                EnableIpRateLimiting: enableApiRateLimiting,
                                EnableExceptionHandling: true),
                            afterAccessControl: pipeline =>
                            {
                                #if DEBUG
                                pipeline.UseOpenApi();
                                #endif

                                pipeline.UseResponseCompression();
                                pipeline.UseWebSockets();
                                pipeline.MapWebSocketManager("/notifications",
                                    pipeline.ApplicationServices.GetService<WebSocketNotificationsRelay>());
                                pipeline.UseMetricServer(MetricsRoutePrefix);
                                pipeline.UseMiddleware<ApiRequestMetricsMiddleware>();
                                pipeline.UseMvc();
                            });
                    });

                    var httpScheme = $"http{(apiTlsEnable ? "s" : "")}";
                    var webSocketScheme = $"ws{(apiTlsEnable ? "s" : "")}";
                    var listenerHost = FormatListenerHost(address);
                    logger.Info(() => $"Public API listening on {httpScheme}://{listenerHost}:{endpointPorts.PublicPort}");
                    logger.Info(() => $"Administrative API listening on {httpScheme}://{listenerHost}:{endpointPorts.AdminPort}{AdminApiAuthenticationMiddleware.AdminRoutePrefix}");
                    logger.Info(() => $"Prometheus Metrics API listening on {httpScheme}://{listenerHost}:{endpointPorts.MetricsPort}{MetricsRoutePrefix}");
                    logger.Info(() => $"WebSocket Events streaming on {webSocketScheme}://{listenerHost}:{endpointPorts.PublicPort}/notifications");

                    switch(adminApiCredential.Status)
                    {
                        case AdminApiCredentialStatus.Configured:
                            logger.Info("Administrative API bearer authentication enabled");
                            if(!apiTlsEnable && !IPAddress.IsLoopback(address))
                                logger.Warn("Administrative API bearer authentication is using HTTP on a non-loopback listener; restrict the listener to a trusted network or enable TLS before sending the token");
                            break;
                        case AdminApiCredentialStatus.Invalid:
                            logger.Warn($"Administrative API disabled: " +
                                $"{AdminApiAuthenticationMiddleware.TokenEnvironmentVariable} must contain exactly " +
                                $"{AdminApiCredential.RequiredTokenCharacters} hexadecimal characters");
                            break;
                        default:
                            logger.Warn($"Administrative API disabled until {AdminApiAuthenticationMiddleware.TokenEnvironmentVariable} is configured");
                            break;
                    }

                    foreach(var warning in GetSharedProtectedRouteWarnings(
                                apiConfig))
                        logger.Warn(warning);
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

        ConfigureShareProcessingHostedServices(services, clusterConfig);

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
                        AssignRecoveryPoolTemplates(clusterConfig,
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
            AssignPoolTemplatesAndLogPaymentExtraOmissions(enabledPools,
                coinTemplates, PaymentProcessingExtraDiagnostics.
                    CreateLogger());
            // Configuration parsing runs before coin templates are loaded. Recheck the
            // template-dependent PPS family contract here, after production has assigned the
            // templates but before any Stratum listener is reserved or pool is started.
            // PpsTemplateFamily_IsCheckedAfterProductionAssignment pins this ordering.
            ValidatePpsDeployment(clusterConfig, requireAssignedTemplates: true);
            ValidateBitcoinDirectSoloDeployment(clusterConfig,
                requireAssignedTemplates: true);
            var listenerCoordinator = new StratumListenerReservationCoordinator(
                logger);
            using var listenerReservations = await listenerCoordinator.ReserveAllAsync(
                enabledPools, ct);

            if(listenerReservations.Count > 0)
            {
                logger.Info(() =>
                    $"Reserved {listenerReservations.Count} Stratum listener socket(s) before pool startup");
            }

            await SupervisePoolLifetimesAsync(enabledPools.Select(config =>
                    new KeyValuePair<string, Func<CancellationToken, Task>>(
                        config.Id, poolCt => RunPool(config,
                            listenerReservations, poolCt))),
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
        services.TryAddSingleton<ISharePersistenceQueueMetricsProvider>(sp =>
            sp.GetRequiredService<ShareRecorder>());
    }

    internal static void ConfigureShareProcessingHostedServices(
        IServiceCollection services, ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        if(config.ShareRelay == null)
        {
            ConfigureShareRecorderHostedService(services);
            services.AddHostedService<ShareReceiver>();
        }
        else
            services.AddHostedService<ShareRelay>();
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

    internal static ApiConfig NormalizeApiConfig(ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Api ??= new ApiConfig
        {
            Enabled = true,
        };

        return config.Api;
    }

    internal static ClusterConfig ReadAndValidateConfig(string file,
        bool recoveryMode)
    {
        var config = ReadConfig(file, recoveryMode);
        if(!recoveryMode)
            NormalizeApiConfig(config);
        ValidateConfig(config, recoveryMode);
        return config;
    }

    internal sealed class ApiEndpointPorts
    {
        public ApiEndpointPorts(int publicPort, int adminPort,
            int metricsPort)
        {
            PublicPort = publicPort;
            AdminPort = adminPort;
            MetricsPort = metricsPort;
            ListenerPorts = Array.AsReadOnly(new[]
            {
                publicPort,
                adminPort,
                metricsPort,
            }
            .Distinct()
            .ToArray());
        }

        public int PublicPort { get; }
        public int AdminPort { get; }
        public int MetricsPort { get; }
        public IReadOnlyList<int> ListenerPorts { get; }
    }

    internal sealed record ApiPipelineOptions(
        bool EnableIpRateLimiting = false,
        bool EnableExceptionHandling = false,
        TimeProvider ProtectedRouteRejectionTimeProvider = null,
        CollectorRegistry WhitelistRejectionMetricsRegistry = null);

    internal static ApiEndpointPorts ResolveApiEndpointPorts(ApiConfig api)
    {
        var publicPort = api?.Port ?? DefaultApiPort;
        return new ApiEndpointPorts(
            publicPort,
            api?.AdminPort ?? publicPort,
            api?.MetricsPort ?? publicPort);
    }

    internal static IPAddress ResolveListenAddress(string listenAddress)
    {
        if(!TryResolveListenAddress(listenAddress, out var address))
            throw new FormatException(
                $"Invalid IP listen address '{listenAddress}'");

        return address;
    }

    internal static bool TryResolveListenAddress(string listenAddress,
        out IPAddress address) =>
        ListenerAddressUtils.TryResolve(listenAddress, out address);

    internal static string FormatListenerHost(IPAddress address)
    {
        return ListenerAddressUtils.FormatHost(address);
    }

    internal static string[] GetSharedProtectedRouteWarnings(ApiConfig api)
    {
        ArgumentNullException.ThrowIfNull(api);

        var warnings = new List<string>();

        // Keep these at warning level even for loopback-only whitelists: a same-host reverse
        // proxy also appears as loopback and can make a shared protected route public.

        if(!api.AdminPort.HasValue)
            warnings.Add($"api.adminPort is omitted; {AdminApiAuthenticationMiddleware.AdminRoutePrefix} is served on the public listener. A public reverse proxy must deny this path unless forwarding it is intentional");

        if(!api.MetricsPort.HasValue)
            warnings.Add($"api.metricsPort is omitted; {MetricsRoutePrefix} is served on the public listener. A public reverse proxy must deny this path unless exposing metrics is intentional");

        return warnings.ToArray();
    }

    internal static void ConfigureApiListeners(KestrelServerOptions options,
        IPAddress address, ApiEndpointPorts ports,
        Action<ListenOptions> configureListener = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(ports);

        foreach(var port in ports.ListenerPorts)
        {
            options.Listen(address, port, listenOptions =>
                configureListener?.Invoke(listenOptions));
        }
    }

    internal static bool IsApiRequestAllowed(int localPort, PathString path,
        ApiEndpointPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        if(ProtectedRouteClassifier.IsAdminRequest(path))
            return localPort == ports.AdminPort;

        if(IsMetricsRequest(path))
            return localPort == ports.MetricsPort;

        return localPort == ports.PublicPort;
    }

    internal static bool IsMetricsRequest(PathString path) =>
        ProtectedRouteClassifier.IsMetricsRequest(path);

    internal static bool ShouldApplyPublicCors(PathString path) =>
        !ProtectedRouteClassifier.IsProtectedRequest(path);

    internal static void ConfigureApiPipeline(IApplicationBuilder app,
        ApiEndpointPorts ports, string[] adminIpWhitelist,
        string[] metricsIpWhitelist, AdminApiCredential adminCredential,
        bool gpdrCompliantLogging,
        ApiPipelineOptions options = null,
        Action<IApplicationBuilder> afterAccessControl = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(adminCredential);
        options ??= new ApiPipelineOptions();

        // Browser resource isolation belongs ahead of every terminal path so
        // protected success and rejection responses share the same policy.
        app.UseMiddleware<ProtectedRouteResourcePolicyMiddleware>();

        // Reject wrong-listener requests before rate limiting or protected endpoint
        // middleware. Preserve the generic 404 response without exposing endpoint
        // status, authentication state, or route-specific response content.
        app.Use(async (context, next) =>
        {
            if(!IsApiRequestAllowed(context.Connection.LocalPort,
                   context.Request.Path, ports))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });

        if(options.EnableIpRateLimiting)
        {
            // AspNetCoreRateLimit normalizes method tokens before evaluating its
            // endpoint whitelist. Decide the metrics exemption here from the raw
            // case-sensitive token so rejected lookalikes remain throttled.
            app.UseWhen(context => ShouldApplyIpRateLimiting(
                    context.Request.Path, context.Request.Method),
                rateLimited => rateLimited.UseIpRateLimiting());
        }

        if(options.EnableExceptionHandling)
            app.UseMiddleware<ApiExceptionHandlingMiddleware>();

        UseIpWhiteList(app, true,
            new[] { AdminApiAuthenticationMiddleware.AdminRoutePrefix },
            adminIpWhitelist, gpdrCompliantLogging,
            options.ProtectedRouteRejectionTimeProvider,
            options.WhitelistRejectionMetricsRegistry);
        UseIpWhiteList(app, true, new[] { MetricsRoutePrefix },
            metricsIpWhitelist, gpdrCompliantLogging,
            options.ProtectedRouteRejectionTimeProvider,
            options.WhitelistRejectionMetricsRegistry);
        app.UseMiddleware<AdminApiAuthenticationMiddleware>(adminCredential,
            gpdrCompliantLogging,
            options.ProtectedRouteRejectionTimeProvider ?? TimeProvider.System);

        // Preserve wrong-listener 404 and IP-whitelist 403 behavior by enforcing
        // the metrics method contract only after those access-control boundaries.
        app.UseMiddleware<MetricsMethodPolicyMiddleware>();

        // Public API clients retain the existing permissive policy. Administrative and
        // metrics routes deliberately receive no CORS headers: browsers must not carry
        // the operator token or gain cross-origin access to operational telemetry.
        app.UseWhen(context => ShouldApplyPublicCors(context.Request.Path),
            publicApi => publicApi.UseCors(corsPolicyBuilder =>
                corsPolicyBuilder.AllowAnyOrigin().AllowAnyMethod()
                    .AllowAnyHeader()));

        afterAccessControl?.Invoke(app);
    }

    internal static AdminApiCredential GetAdminApiCredential() =>
        adminApiCredentialProvider.Get();

    internal static int? FindApiListenerStratumPortConflict(
        ClusterConfig config, bool recoveryMode)
    {
        ArgumentNullException.ThrowIfNull(config);

        if(!ShouldConfigureApi(recoveryMode, config.Api))
            return null;

        var apiAddress = ResolveListenAddress(config.Api?.ListenAddress);
        var apiPorts = ResolveApiEndpointPorts(config.Api).ListenerPorts
            .ToHashSet();

        foreach(var pool in config.Pools?.Where(pool => pool.Enabled &&
                    pool.EnableInternalStratum == true && pool.Ports != null) ??
                Enumerable.Empty<PoolConfig>())
        {
            foreach(var (port, endpoint) in pool.Ports)
            {
                if(endpoint == null || !apiPorts.Contains(port) ||
                    !TryResolveListenAddress(endpoint.ListenAddress,
                        out var stratumAddress))
                    continue;

                if(ListenAddressesOverlap(apiAddress, stratumAddress))
                    return port;
            }
        }

        return null;
    }

    internal static bool ListenAddressesOverlap(IPAddress first,
        IPAddress second) =>
        ListenerAddressUtils.Overlaps(first, second);

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

    internal static void AssignPoolTemplatesAndLogPaymentExtraOmissions(
        IEnumerable<PoolConfig> poolConfigs,
        IReadOnlyDictionary<string, CoinTemplate> coinTemplates,
        ILogger diagnosticLogger)
    {
        ArgumentNullException.ThrowIfNull(poolConfigs);
        ArgumentNullException.ThrowIfNull(coinTemplates);
        ArgumentNullException.ThrowIfNull(diagnosticLogger);

        var pools = poolConfigs as PoolConfig[] ?? poolConfigs.ToArray();
        AssignPoolTemplates(pools, coinTemplates);
        PaymentProcessingExtraDiagnostics.Log(pools, diagnosticLogger);
    }

    internal static void AssignRecoveryPoolTemplates(ClusterConfig config,
        IReadOnlyDictionary<string, CoinTemplate> coinTemplates)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(coinTemplates);

        // Enabled state controls live mining, not journal attribution. Recovery may use an
        // all-disabled safety configuration, so enrich every configured pool when its template
        // remains available.
        var missing = new List<string>();
        foreach(var pool in config.Pools)
        {
            if(!string.IsNullOrEmpty(pool.Coin) &&
               coinTemplates.TryGetValue(pool.Coin, out var template))
            {
                pool.Template = template;
                continue;
            }

            missing.Add($"{pool.Id} ({pool.Coin ?? "<missing>"})");
        }

        if(missing.Count > 0)
            throw new PoolStartupException(
                $"Recovery coin templates are unavailable for pool(s): {string.Join(", ", missing)}");
    }

    private async Task RunPool(PoolConfig poolConfig,
        StratumListenerReservationSession listenerReservations,
        CancellationToken ct)
    {
        // resolve implementation
        var poolImpl = container.Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .First(x => x.Value.Metadata.SupportedFamilies.Contains(poolConfig.Template.Family)).Value;

        // configure
        var pool = poolImpl.Value;
        pool.Configure(poolConfig, clusterConfig);

        if(pool is PoolBase poolBase)
            poolBase.AttachStratumListenerReservations(listenerReservations);
        else if(poolConfig.EnableInternalStratum == true)
        {
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' does not support retained Stratum listener reservations",
                poolConfig.Id);
        }

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

    internal static void ValidateConfig(ClusterConfig config,
        bool recoveryMode)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            // Let FluentValidation report null collections or entries before live-startup code
            // dereferences them. JSON schema validation normally catches these first, but direct
            // callers and tests deserve the same configuration boundary instead of an NRE.
            if(config.Pools == null || config.Pools.Any(pool => pool == null))
                config.Validate(recoveryMode);

            if(!recoveryMode && !config.Pools.Any(pool => pool.Enabled))
                throw new PoolStartupException("No pools are enabled.");

            if(!recoveryMode)
            {
                // Apply live-only defaults before the complete validator examines listeners.
                foreach(var poolConfig in config.Pools)
                {
                    poolConfig.EnableInternalStratum ??=
                        config.ShareRelays == null || config.ShareRelays.Length == 0;
                }
            }

            config.Validate(recoveryMode);
            if(!recoveryMode)
            {
                ValidateMergedMiningDeployment(config);
                ValidatePpsDeployment(config);
                ValidateBitcoinDirectSoloDeployment(config);
            }

            var listenerConflict = FindApiListenerStratumPortConflict(
                config, recoveryMode);
            if(listenerConflict.HasValue)
                throw new PoolStartupException(
                    $"API listener port {listenerConflict.Value} is also assigned to an enabled Stratum endpoint");

            if(!recoveryMode &&
               config.Notifications?.Admin?.Enabled == true)
            {
                if(string.IsNullOrEmpty(config.Notifications?.Email?.FromName))
                    throw new PoolStartupException($"Notifications are enabled but email sender name is not configured (notifications.email.fromName)");

                if(string.IsNullOrEmpty(config.Notifications?.Email?.FromAddress))
                    throw new PoolStartupException($"Notifications are enabled but email sender address name is not configured (notifications.email.fromAddress)");

                if(string.IsNullOrEmpty(config.Notifications?.Admin?.EmailAddress))
                    throw new PoolStartupException($"Admin notifications are enabled but recipient address is not configured (notifications.admin.emailAddress)");
            }

            if(string.IsNullOrEmpty(config.Logging?.LogFile))
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
        Console.WriteLine(SerializeParsedConfig(config));
    }

    internal static string SerializeParsedConfig(ClusterConfig config) =>
        JsonConvert.SerializeObject(config,
            ConfigurationJson.CreateSerializerSettings(Formatting.Indented));

    private static void GenerateJsonConfigSchema()
    {
        var filename = generateSchemaOption.Value();

        var schema = GenerateJsonConfigSchemaDocument();

        using(var stream = File.Create(filename))
        {
            using(var writer = new JsonTextWriter(new StreamWriter(stream, Encoding.UTF8)))
            {
                writer.Formatting = Formatting.Indented;
                schema.WriteTo(writer);
                writer.WriteWhitespace(Environment.NewLine);

                writer.Flush();
            }
        }
    }

    internal static string[] GetPoolsWithSkippedStratumListenerValidation(
        ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Pools?
            .Where(pool => !pool.Enabled &&
                pool.EnableInternalStratum == true &&
                pool.Ports?.Any() == true)
            .Select(pool => string.IsNullOrEmpty(pool.Id)
                ? "<unnamed>"
                : pool.Id)
            .ToArray() ?? Array.Empty<string>();
    }

    internal static void ConfigureApiJsonSerializerOptions(
        System.Text.Json.JsonSerializerOptions options,
        bool legacyNullValueHandling)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.WriteIndented = true;
        options.DefaultIgnoreCondition = legacyNullValueHandling
            ? JsonIgnoreCondition.Never
            : JsonIgnoreCondition.WhenWritingNull;
    }

    internal static string[] GetEnabledRelayOnlyNullStratumEndpoints(
        ClusterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Pools?
            .Where(pool => pool?.Enabled == true &&
                pool.EnableInternalStratum == false)
            .SelectMany(pool => pool.Ports?
                    .Where(endpoint => endpoint.Value == null)
                    .Select(endpoint =>
                        $"{(string.IsNullOrEmpty(pool.Id) ? "<unnamed>" : pool.Id)}:{endpoint.Key}") ??
                Array.Empty<string>())
            .ToArray() ?? Array.Empty<string>();
    }

    private static void LogSkippedStratumListenerValidation(
        ClusterConfig config)
    {
        var poolIds = GetPoolsWithSkippedStratumListenerValidation(config);
        if(poolIds.Length > 0)
        {
            logger.Info(() =>
                $"Stratum listener validation skipped for disabled pool(s): {string.Join(", ", poolIds)}. Listener settings will be validated when the pool is enabled");
        }

        var nullRelayEndpoints =
            GetEnabledRelayOnlyNullStratumEndpoints(config);
        if(nullRelayEndpoints.Length > 0)
        {
            logger.Warn(() =>
                $"Enabled relay-only pool(s) contain unusable null Stratum endpoint entries: {string.Join(", ", nullRelayEndpoints)}. Internal Stratum is disabled and these entries will be omitted from the public API");
        }
    }

    internal static JObject GenerateJsonConfigSchemaDocument()
    {
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

        return JObject.Parse(generator.Generate(typeof(ClusterConfig))
            .ToString());
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

    internal static ClusterConfig ReadConfig(string file,
        bool skipApiListenerSettings = false)
    {
        try
        {
            Console.WriteLine($"Using configuration file '{file}'");
            if(skipApiListenerSettings)
                Console.WriteLine(
                    "Recovery mode: unused live cluster and pool configuration discarded " +
                    "(no API, Stratum, payout, or daemon services are started)");

            var serializer = JsonSerializer.Create(
                ConfigurationJson.CreateSerializerSettings());

            using(var reader = new StreamReader(file, Encoding.UTF8))
            {
                // This reader materializes the JObject before schema validation and CLR
                // binding, so it is the authoritative boundary for preserving strings.
                using(var jsonReader = ConfigurationJson.CreateReader(reader))
                {
                    var document = LoadConfigurationDocument(jsonReader,
                        skipApiListenerSettings);

                    RejectCaseInsensitivePropertyDuplicates(document);
                    // Recovery mode discards live pool settings, but the same
                    // source document must never make malformed or ambiguous
                    // security-sensitive switches appear acceptable in one
                    // startup mode and invalid in another.
                    ValidateBitcoinDirectSoloSyntax(document);
                    ValidateBitcoinBip54CoinbaseSyntax(document);
                    if(skipApiListenerSettings)
                    {
                        // Recovery configuration policy:
                        // - cluster: stream-rebuilt from the explicit recovery allowlist;
                        // - logging: rebuilt from console settings consumed by recovery;
                        // - pools[]: rebuilt from the recovery allowlist (id plus optional coin
                        //   metadata) because import starts no live pool services;
                        // - coinTemplates: valid paths retained, malformed optional metadata removed.
                        // Configuration consumed by recovery remains subject to normal duplicate,
                        // schema and CLR-binding validation.
                        SanitizeConfigurationForRecovery(document);
                        SanitizeLoggingForRecovery(document);
                        SanitizeCoinTemplatesForRecovery(document);
                    }
                    RemoveDisabledApiSettings(document);

                    using(var documentReader = document.CreateReader())
                    using(var validatingReader = new JSchemaValidatingReader(documentReader)
                    {
                        Schema =  LoadSchema()
                    })
                    {
                        return serializer.Deserialize<ClusterConfig>(
                            validatingReader);
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

    private static void RejectCaseInsensitivePropertyDuplicates(
        JObject document)
    {
        foreach(var current in document.DescendantsAndSelf()
                    .OfType<JObject>()
                    .Where(current => !IsFreeFormConfigurationObject(current)))
        {
            var duplicate = current.Properties()
                .GroupBy(property => property.Name,
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Skip(1).Any());
            if(duplicate == null)
                continue;

            var names = string.Join(", ", duplicate.Select(property =>
                $"'{property.Name}'"));
            var path = string.IsNullOrEmpty(current.Path) ? "$" :
                current.Path;
            var locationProperty = duplicate.Skip(1)
                .Concat(duplicate.Take(1))
                .FirstOrDefault(property =>
                    property is IJsonLineInfo lineInfo &&
                    lineInfo.HasLineInfo());
            var location = GetJsonLocationSuffix(
                locationProperty as IJsonLineInfo,
                locationProperty?.Path);
            var container = string.IsNullOrEmpty(location)
                ? $" at '{path}'"
                : string.Empty;

            throw new JsonSerializationException(
                $"Properties {names}{container} differ only by case." +
                location);
        }
    }

    private static bool IsFreeFormConfigurationObject(JObject current) =>
        current.Ancestors().OfType<JProperty>().Any(property =>
            property.Name.Equals("payoutSchemeConfig",
                StringComparison.OrdinalIgnoreCase));

    internal static void ValidateBitcoinDirectSoloSyntax(JObject document)
    {
        foreach(var pool in document?["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var property = pool.Properties().FirstOrDefault(candidate =>
                candidate.Name.Equals("soloCoinbasePayout",
                    StringComparison.OrdinalIgnoreCase));
            if(property == null)
                continue;

            if(!string.Equals(property.Name, "soloCoinbasePayout",
                   StringComparison.Ordinal))
                throw new JsonSerializationException(
                    $"Property '{property.Name}' must use canonical casing 'soloCoinbasePayout'." +
                    GetJsonLocationSuffix(property as IJsonLineInfo,
                        property.Path));
            if(property.Value.Type != JTokenType.Boolean)
                throw new JsonSerializationException(
                    "Property 'soloCoinbasePayout' must be a JSON Boolean." +
                    GetJsonLocationSuffix(property.Value as IJsonLineInfo,
                        property.Path));
        }
    }

    internal static void ValidateBitcoinBip54CoinbaseSyntax(JObject document)
    {
        foreach(var pool in document?["pools"]?.Children<JObject>() ??
                    Enumerable.Empty<JObject>())
        {
            var property = pool.Properties().FirstOrDefault(candidate =>
                candidate.Name.Equals("bip54Coinbase",
                    StringComparison.OrdinalIgnoreCase));
            if(property == null)
                continue;

            if(!string.Equals(property.Name, "bip54Coinbase",
                   StringComparison.Ordinal))
                throw new JsonSerializationException(
                    $"Property '{property.Name}' must use canonical casing 'bip54Coinbase'." +
                    GetJsonLocationSuffix(property as IJsonLineInfo,
                        property.Path));
            if(property.Value.Type != JTokenType.Boolean)
                throw new JsonSerializationException(
                    "Property 'bip54Coinbase' must be a JSON Boolean." +
                    GetJsonLocationSuffix(property.Value as IJsonLineInfo,
                        property.Path));
            if(!string.Equals(pool["coin"]?.Value<string>(), "bitcoin",
                   StringComparison.Ordinal))
                throw new JsonSerializationException(
                    "Property 'bip54Coinbase' is supported only on the canonical 'bitcoin' pool template." +
                    GetJsonLocationSuffix(property as IJsonLineInfo,
                        property.Path));
        }
    }

    private static string GetJsonLocationSuffix(IJsonLineInfo source,
        string path = null)
    {
        if(source?.HasLineInfo() != true)
            return string.Empty;

        return string.IsNullOrEmpty(path)
            ? $" Line {source.LineNumber}, position {source.LinePosition}."
            : $" Path '{path}', line {source.LineNumber}, position {source.LinePosition}.";
    }

    internal static JObject LoadConfigurationDocument(JsonReader reader,
        bool recoveryMode)
    {
        var strictSettings = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling =
                DuplicatePropertyNameHandling.Error,
        };

        if(!recoveryMode)
            return JObject.Load(reader, strictSettings);

        // Recovery consumes only the allowlisted cluster properties. Filter every other
        // top-level case variant while streaming so malformed or duplicate live-only settings
        // cannot block emergency work. Exact and case-variant duplicates within the recovery
        // boundary remain errors.
        if(!ReadNextContentToken(reader) ||
            reader.TokenType != JsonToken.StartObject)
            throw new JsonSerializationException(
                "The configuration root must be a JSON object." +
                GetJsonLocationSuffix(reader as IJsonLineInfo,
                    reader.Path));

        var document = new JObject();
        var rootProperties = new HashSet<string>(StringComparer.Ordinal);

        while(ReadNextContentToken(reader))
        {
            if(reader.TokenType == JsonToken.EndObject)
                return document;

            if(reader.TokenType != JsonToken.PropertyName)
            {
                // JsonTextReader rejects malformed object structure before reaching this
                // branch. Keep the reader path for custom readers that can surface an
                // unexpected structural token with meaningful path context.
                throw new JsonSerializationException(
                    $"Expected a configuration property but found {reader.TokenType}." +
                    GetJsonLocationSuffix(reader as IJsonLineInfo,
                        reader.Path));
            }

            var propertyName = (string) reader.Value;
            var propertyLocation = GetJsonLocationSuffix(
                reader as IJsonLineInfo, propertyName);
            if(!ReadNextContentToken(reader))
                throw new JsonSerializationException(
                    $"Configuration property '{propertyName}' has no value." +
                    propertyLocation);

            if(!RecoveryConfigurationProperties.Contains(propertyName))
            {
                reader.Skip();
                continue;
            }

            if(!rootProperties.Add(propertyName))
                throw new JsonSerializationException(
                    $"Property with the name '{propertyName}' already exists in the current JSON object." +
                    propertyLocation);

            document.Add(propertyName, JToken.Load(reader, strictSettings));
        }

        throw new JsonSerializationException(
            "Unexpected end of configuration file." +
            GetJsonLocationSuffix(reader as IJsonLineInfo,
                reader.Path));
    }

    private static bool ReadNextContentToken(JsonReader reader)
    {
        while(reader.Read())
        {
            if(reader.TokenType != JsonToken.Comment)
                return true;
        }

        return false;
    }

    private static void SanitizeConfigurationForRecovery(
        JObject document)
    {
        var pools = document.GetValue("pools",
            StringComparison.OrdinalIgnoreCase) as JArray;
        if(pools == null)
            return;

        foreach(var pool in pools.OfType<JObject>())
        {
            // Import consumes only the pool ID for attribution and optional coin metadata for
            // best-effort notification enrichment. Rebuild the object from that allowlist so
            // malformed live-only settings cannot block emergency recovery. Duplicate and
            // case-variant ambiguity checks have already run above.
            var id = pool.Properties().FirstOrDefault(property =>
                property.Name.Equals("id",
                    StringComparison.OrdinalIgnoreCase))?.Value.DeepClone();
            var coin = pool.Properties().FirstOrDefault(property =>
                property.Name.Equals("coin",
                    StringComparison.OrdinalIgnoreCase));
            var coinValue = coin?.Value.Type == JTokenType.String
                ? coin.Value.Value<string>() ?? string.Empty
                : string.Empty;

            pool.RemoveAll();

            // Do not synthesize pool identity: missing or malformed IDs remain subject to the
            // normal schema and recovery validator. Canonical schema fillers represent services
            // recovery deliberately does not start.
            if(id != null)
                pool["id"] = id;
            pool["coin"] = coinValue;
            pool["ports"] = new JObject();
            pool["daemons"] = new JArray();
        }
    }

    private static void SanitizeLoggingForRecovery(JObject document)
    {
        var property = document.Properties().FirstOrDefault(property =>
            property.Name.Equals("logging",
                StringComparison.OrdinalIgnoreCase));
        var sanitized = new JObject();

        if(property?.Value is JObject logging)
        {
            // Recovery always writes to the console and only consumes these two settings.
            // Discard file-only and live-service logging fields so stale values cannot block
            // an emergency import. Preserve the consumed tokens so schema/CLR validation still
            // rejects malformed console settings rather than silently changing their meaning.
            foreach(var name in new[] { "level", "enableConsoleColors" })
            {
                var consumed = logging.Properties().FirstOrDefault(candidate =>
                    candidate.Name.Equals(name,
                        StringComparison.OrdinalIgnoreCase));
                if(consumed != null)
                    sanitized[name] = consumed.Value.DeepClone();
            }
        }

        if(property == null)
            document["logging"] = sanitized;
        else
            property.Value = sanitized;
    }

    private static void SanitizeCoinTemplatesForRecovery(JObject document)
    {
        var property = document.Properties().FirstOrDefault(property =>
            property.Name.Equals("coinTemplates",
                StringComparison.OrdinalIgnoreCase));
        if(property == null)
            return;

        if(property.Value is not JArray templates)
        {
            // Recovery can use the bundled definitions without this optional metadata. Remove
            // malformed values before schema validation so they cannot block emergency work.
            if(property.Value.Type != JTokenType.Null)
                property.Remove();
            return;
        }

        // Keep valid custom definitions while removing elements that normal startup correctly
        // rejects. Duplicate and case-variant properties have already failed above.
        foreach(var item in templates.Where(item =>
                    item.Type != JTokenType.String).ToArray())
            item.Remove();
    }

    private static void RemoveDisabledApiSettings(JObject document)
    {
        var api = document.GetValue("api",
            StringComparison.OrdinalIgnoreCase) as JObject;
        if(api == null)
            return;

        var enabledToken = api.GetValue("enabled",
            StringComparison.OrdinalIgnoreCase);
        var enabled = enabledToken is JValue
        {
            Type: JTokenType.Boolean,
            Value: bool value,
        } && value;
        if(enabled)
            return;

        // No API setting other than the disabled marker is consumed when no HTTP sockets are
        // opened. Remove the inactive subtree before schema validation and CLR binding; an
        // invalid enabled token remains so the schema reports its real type error.
        foreach(var property in api.Properties().Where(property =>
                    !property.Name.Equals("enabled",
                        StringComparison.OrdinalIgnoreCase)).ToArray())
            property.Remove();
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
        Console.WriteLine(FormatDonationAddresses());
        Console.WriteLine();
    }

    internal static string FormatDonationAddresses()
    {
        var result = new StringBuilder();

        result.AppendLine(
            " Donations to support development and maintenance of this NINJAK1DD Miningcore fork:");
        result.AppendLine();

        for(var i = 0; i < DonationAddresses.Length; i++)
        {
            var (symbol, address) = DonationAddresses[i];
            result.Append($" {symbol,-4} - {address}");

            if(i < DonationAddresses.Length - 1)
                result.AppendLine();
        }

        return result.ToString();
    }

    private static void ConfigureLogging()
    {
        // Recovery must remain visible even when an operator deliberately supplies the smallest
        // accepted import configuration or constructs it outside the JSON sanitization path.
        var config = clusterConfig.Logging ??
            (isShareRecoveryMode ? new ClusterLoggingConfig() : null);
        LogManager.Configuration = CreateLoggingConfiguration(config,
            isShareRecoveryMode, clusterConfig.Pools);

        logger = LogManager.GetLogger("Core");
    }

    internal static LoggingConfiguration CreateLoggingConfiguration(
        ClusterLoggingConfig config, bool recoveryMode,
        IEnumerable<PoolConfig> pools)
    {
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
            if(!string.IsNullOrEmpty(config.ApiLogFile) && !recoveryMode)
            {
                var target = CreateFileTarget("api-file", GetLogPath(config, config.ApiLogFile), layout);

                loggingConfig.AddTarget(target);
                loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target, "Microsoft.AspNetCore.*", true);
            }

            if(config.EnableConsoleLog || recoveryMode)
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
                    // This wildcard route must continue to include dedicated
                    // startup categories such as PaymentExtraDiagnostics.
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
                }

                else
                {
                    var target = new ConsoleTarget("console")
                    {
                        Layout = layout
                    };

                    loggingConfig.AddTarget(target);
                    // This wildcard route must continue to include dedicated
                    // startup categories such as PaymentExtraDiagnostics.
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
                }
            }

            if(!string.IsNullOrEmpty(config.LogFile) && !recoveryMode)
            {
                var target = CreateFileTarget("main-file", GetLogPath(config, config.LogFile), layout);

                loggingConfig.AddTarget(target);
                // This wildcard route must continue to include dedicated
                // startup categories such as PaymentExtraDiagnostics.
                loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target);
            }

            if(config.PerPoolLogFile && !recoveryMode)
            {
                foreach(var poolConfig in pools ?? Array.Empty<PoolConfig>())
                {
                    var target = CreateFileTarget($"pool-{poolConfig.Id}-file",
                        GetLogPath(config, poolConfig.Id + ".log"), layout);

                    loggingConfig.AddTarget(target);
                    loggingConfig.AddRule(level, NLog.LogLevel.Fatal, target, poolConfig.Id);
                }
            }
        }

        return loggingConfig;
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

        // Recovery stops at its database and ownership boundary. It neither consumes mining
        // concurrency configuration nor initializes native hashing and solver runtimes. The
        // journal performs its evidence-driven block-index check immediately before import.
        if(isShareRecoveryMode)
            return;

        if(RequiresSynchronousBlockCandidatePersistence(clusterConfig))
        {
            await EnsureMergedMiningSchemaAsync(clusterConfig,
                services.GetService<IConnectionFactory>(),
                services.GetService<IBlockRepository>(), CancellationToken.None);
        }

        await EnsureShareAccountingSchemaAsync(clusterConfig,
            services.GetService<IConnectionFactory>(),
            services.GetService<IShareRepository>(), CancellationToken.None);

        await EnsureBitcoinDirectSoloSchemaAsync(clusterConfig,
            services.GetService<IConnectionFactory>(),
            services.GetService<IBlockRepository>(), CancellationToken.None);

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
            RequiresSynchronousBlockCandidatePersistence(config);
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
            .Where(x => recoveryMode || x.Enabled)
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

        var poolScope = recoveryMode ? "configured recovery" : "enabled";
        throw new PoolStartupException(
            $"The partitioned PostgreSQL shares table has no partition for {poolScope} pool ID(s): " +
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

    internal static bool RequiresSynchronousBlockCandidatePersistence(
        ClusterConfig config) => RequiresMergedMiningPersistence(config) ||
        RequiresBitcoinDirectSoloPersistence(config) ||
        config?.Pools?.Any(pool => pool.Enabled &&
            pool.PaymentProcessing?.Enabled == true &&
            pool.PaymentProcessing.PayoutScheme == PayoutScheme.PPS) == true;

    internal static bool RequiresShareAccountingPersistence(ClusterConfig config)
    {
        if(config?.Pools?.Any(pool => pool.Enabled &&
               pool.PaymentProcessing?.Enabled == true &&
               pool.PaymentProcessing.PayoutScheme == PayoutScheme.PPS) == true)
            return true;

        if(config?.ShareRelay != null)
            return false;

        return config.Pools?.Any(pool => pool.Enabled &&
            (MergedMiningUsesPooledAccounting(config, pool) ||
             pool.PaymentProcessing?.Enabled == true &&
             pool.PaymentProcessing.PayoutScheme == PayoutScheme.PPS)) == true;
    }

    private static bool MergedMiningUsesPooledAccounting(ClusterConfig config,
        PoolConfig parent)
    {
        var merged = MergedMiningConfigLoader.GetNormalizedConfig(parent);
        if(merged?.Enabled != true)
            return false;

        var auxiliary = config.Pools?.FirstOrDefault(pool =>
            string.Equals(pool.Id, merged.AuxPoolId,
                StringComparison.OrdinalIgnoreCase));

        return parent.PaymentProcessing?.PayoutScheme != PayoutScheme.SOLO ||
            auxiliary?.PaymentProcessing?.PayoutScheme != PayoutScheme.SOLO;
    }

    internal static void ValidatePpsDeployment(ClusterConfig config,
        bool requireAssignedTemplates = false)
    {
        var ppsPools = config?.Pools?.Where(pool => pool.Enabled &&
            pool.PaymentProcessing?.PayoutScheme == PayoutScheme.PPS).ToArray() ??
            Array.Empty<PoolConfig>();

        var paymentDisabledPool = ppsPools.FirstOrDefault(pool =>
            pool.PaymentProcessing.Enabled != true);
        if(paymentDisabledPool != null)
            throw new PoolStartupException(
                $"Pool '{paymentDisabledPool.Id}' uses PPS and must enable pool-level payment " +
                "processing before it can accept shares",
                paymentDisabledPool.Id);

        if(ppsPools.Length > 0 && config.PaymentProcessing?.Enabled != true)
            throw new PoolStartupException(
                "PPS requires cluster-level payment processing so committed liabilities are paid " +
                "and share-accounting retention is maintained.");

        foreach(var pool in ppsPools)
        {
            if(pool.Template == null)
            {
                if(requireAssignedTemplates)
                    throw new PoolStartupException(
                        $"Pool '{pool.Id}' uses PPS but its coin template was not assigned " +
                        "before the PPS runtime contract was checked",
                        pool.Id);
            }
            else if(pool.Template.Family != CoinFamily.Bitcoin)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' uses PPS, which is currently supported only by the " +
                    "audited Bitcoin-family share and reward contract",
                    pool.Id);

            var recipients = pool.RewardRecipients ?? Array.Empty<RewardRecipient>();
            if(recipients.Any(x => x == null || x.Percentage < 0))
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' uses PPS but contains a null or negative reward-recipient percentage",
                    pool.Id);

            decimal recipientPercent;
            try
            {
                recipientPercent = recipients.Where(x => x.Percentage > 0)
                    .Sum(x => x.Percentage);
            }
            catch(OverflowException ex)
            {
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' uses PPS but its reward-recipient percentages exceed the supported accounting range",
                    pool.Id, ex);
            }

            if(recipientPercent >= 100)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' uses PPS but reward recipients leave no positive operator-funded reward basis",
                    pool.Id);
        }

        if(ppsPools.Length > 0 && config.Persistence?.Postgres == null)
            throw new PoolStartupException(
                "Every PPS accepting node requires PostgreSQL so accepted block candidates persist synchronously and the share receipt, liability ledger, precision remainder and miner balance commit atomically.");
    }

    internal static bool RequiresBitcoinDirectSoloPersistence(
        ClusterConfig config) => config?.Pools?.Any(pool => pool.Enabled &&
        pool.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>()?
            .SoloCoinbasePayout == true) == true;

    internal static void ValidateBitcoinDirectSoloDeployment(
        ClusterConfig config, bool requireAssignedTemplates = false)
    {
        var directPools = config?.Pools?.Where(pool => pool.Enabled &&
            pool.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>()?
                .SoloCoinbasePayout == true).ToArray() ??
            Array.Empty<PoolConfig>();
        if(directPools.Length == 0)
            return;

        if(config.Persistence?.Postgres == null)
            throw new PoolStartupException(
                "Bitcoin direct SOLO coinbase payout requires PostgreSQL for synchronous accepted-block audit persistence.");
        if(config.PaymentProcessing?.Enabled != true)
            throw new PoolStartupException(
                "Bitcoin direct SOLO coinbase payout requires cluster-level payment processing for maturity, reorg and notification tracking.");
        if(config.ShareRelay != null || config.ShareRelays?.Length > 0)
            throw new PoolStartupException(
                "Bitcoin direct SOLO coinbase payout does not support share-relay sender, receiver or recorder topologies in its initial BTC-only contract.");

        foreach(var pool in directPools)
        {
            if(!string.Equals(pool.Coin, "bitcoin",
                   StringComparison.Ordinal))
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' enables direct SOLO coinbase payout, which is supported only by the canonical 'bitcoin' template",
                    pool.Id);
            if(pool.EnableInternalStratum != true)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO coinbase payout requires the internal Stratum server",
                    pool.Id);
            if(pool.PaymentProcessing?.Enabled != true ||
               pool.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct coinbase payout requires enabled pool-level payment processing with payoutScheme 'SOLO'",
                    pool.Id);
            if(MergedMiningConfigLoader.GetNormalizedConfig(pool)?.Enabled ==
               true)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO coinbase payout does not support merged-mining topology",
                    pool.Id);

            if(pool.Template == null)
            {
                if(requireAssignedTemplates)
                    throw new PoolStartupException(
                        $"Pool '{pool.Id}' direct SOLO coinbase payout template was not assigned before its runtime contract was checked",
                        pool.Id);
            }
            else if(pool.Template.Family != CoinFamily.Bitcoin ||
                    !string.Equals(pool.Template.Symbol, "BTC",
                        StringComparison.Ordinal) ||
                    !string.Equals(pool.Template.CanonicalName, "Bitcoin",
                        StringComparison.Ordinal))
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO coinbase payout requires the canonical BTC runtime template",
                    pool.Id);

            var recipients = pool.RewardRecipients ??
                Array.Empty<RewardRecipient>();
            if(recipients.Any(x => x == null || x.Percentage < 0 ||
                    x.Percentage > 0 &&
                    string.IsNullOrWhiteSpace(x.Address)))
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO coinbase payout contains a null, negative or addressless positive reward recipient",
                    pool.Id);

            decimal total;
            try
            {
                total = recipients.Where(x => x.Percentage > 0)
                    .Sum(x => x.Percentage);
            }
            catch(OverflowException ex)
            {
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO recipient percentages exceed the supported range",
                    pool.Id, ex);
            }

            if(total >= 100m)
                throw new PoolStartupException(
                    $"Pool '{pool.Id}' direct SOLO recipient percentages must total less than 100%",
                    pool.Id);
        }
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
        if(!RequiresSynchronousBlockCandidatePersistence(config))
            return;

        var schemaReady = await cf.Run(con =>
            blockRepo.HasMergedMiningBlockIndexesAsync(con, ct));

        if(!schemaReady)
            throw new PoolStartupException(
                "Synchronous merged-mining and direct-PPS block persistence requires the block idempotency migration. Apply add_auxpow_block_idempotency.sql before enabling Litecoin-Dogecoin merged mining or direct Bitcoin-family PPS.");
    }

    internal static async Task EnsureShareAccountingSchemaAsync(ClusterConfig config,
        IConnectionFactory cf, IShareRepository shareRepo, CancellationToken ct)
    {
        if(!RequiresShareAccountingPersistence(config))
            return;

        if(config.Persistence?.Postgres == null || cf == null || shareRepo == null)
            throw new PoolStartupException(
                "PPS and merged-mining pooled payouts require PostgreSQL share-accounting persistence.");

        var schemaReady = await cf.Run(con =>
            shareRepo.HasShareAccountingSchemaAsync(con, ct));
        if(!schemaReady)
            throw new PoolStartupException(
                "PPS and merged-mining pooled payouts require the transactional share-accounting schema. Apply add_share_accounting.sql before enabling them.");
    }

    internal static async Task EnsureBitcoinDirectSoloSchemaAsync(
        ClusterConfig config, IConnectionFactory cf,
        IBlockRepository blockRepo, CancellationToken ct)
    {
        if(!RequiresBitcoinDirectSoloPersistence(config))
            return;
        if(cf == null || blockRepo == null)
            throw new PoolStartupException(
                "Bitcoin direct SOLO coinbase payout requires PostgreSQL block persistence.");

        var ready = await cf.Run(con =>
            blockRepo.HasBitcoinDirectSoloSchemaAsync(con, ct));
        if(!ready)
            throw new PoolStartupException(
                "Bitcoin direct SOLO coinbase payout requires the direct-settlement block schema. Apply add_bitcoin_direct_solo.sql before enabling soloCoinbasePayout.");
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

    private static void UseIpWhiteList(IApplicationBuilder app,
        bool defaultToLoopback, string[] locations, string[] whitelist,
        bool gpdrCompliantLogging, TimeProvider timeProvider = null,
        CollectorRegistry rejectionMetricsRegistry = null)
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

            logger?.Info(() => $"API Access to {string.Join(",", locations)} restricted to {string.Join(",", ipList.Select(x => x.ToString()))}");

            // UseMiddleware cannot match an explicitly supplied null argument to
            // a constructor parameter, so always pass a concrete registry.
            app.UseMiddleware<IPAccessWhitelistMiddleware>(locations,
                ipList.ToArray(), gpdrCompliantLogging,
                timeProvider ?? TimeProvider.System,
                rejectionMetricsRegistry ?? Metrics.DefaultRegistry);
        }
    }

    internal static List<string> CreateIpRateLimitEndpointWhitelist() =>
        new()
        {
            "*:/notifications",
        };

    internal static bool ShouldApplyIpRateLimiting(PathString path,
        string method)
    {
        var value = path.Value;
        var isScrapeEndpoint =
            string.Equals(value, MetricsRoutePrefix,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, MetricsRoutePrefix + "/",
                StringComparison.OrdinalIgnoreCase);

        return !isScrapeEndpoint ||
            !MetricsMethodPolicyMiddleware.IsAllowedMethod(method);
    }

    internal static void AddApiRateLimiting(IServiceCollection services,
        ApiConfig apiConfig)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apiConfig);

        services.AddMemoryCache();
        services.Configure<IpRateLimitOptions>(options =>
            ConfigureIpRateLimitOptions(options, apiConfig));
        services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
        services.AddSingleton<IRateLimitCounterStore,
            MemoryCacheRateLimitCounterStore>();
        services.AddSingleton<IRateLimitConfiguration,
            RateLimitConfiguration>();
        services.AddSingleton<IProcessingStrategy,
            AsyncKeyLockProcessingStrategy>();
    }

    internal static void ConfigureIpRateLimitOptions(
        IpRateLimitOptions options, ApiConfig apiConfig)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(apiConfig);

        options.EnableEndpointRateLimiting = false;

        // Exact metrics GET/HEAD requests bypass the middleware in
        // ConfigureApiPipeline because this dependency cannot preserve the raw
        // case-sensitive method token. WebSocket notifications remain exempt here.
        // Administrative routes remain throttled; trusted sources may opt out
        // through the separate rate-limiting IP whitelist.
        options.EndpointWhitelist = CreateIpRateLimitEndpointWhitelist();

        options.IpWhitelist = apiConfig.RateLimiting?.IpWhitelist?.ToList();

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
        var rules = apiConfig.RateLimiting?.Rules?.ToList();

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

        logger?.Info(() => $"API access limited to {(string.Join(", ", rules.Select(x => $"{x.Limit} requests per {x.Period}")))}, except from {string.Join(", ", options.IpWhitelist)}");
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
