using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using NLog;
using Prometheus;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Notifications;

public class MetricsPublisher : StartupGatedBackgroundService
{
    public MetricsPublisher(IMessageBus messageBus,
        ISharePersistenceQueueMetricsProvider sharePersistenceMetrics = null) :
        this(messageBus, sharePersistenceMetrics, Metrics.DefaultFactory,
            Metrics.DefaultRegistry)
    {
    }

    internal MetricsPublisher(IMessageBus messageBus,
        ISharePersistenceQueueMetricsProvider sharePersistenceMetrics,
        IMetricFactory metricFactory, CollectorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(metricFactory);
        ArgumentNullException.ThrowIfNull(registry);

        CreateMetrics(metricFactory);

        this.messageBus = messageBus;
        this.sharePersistenceMetrics = sharePersistenceMetrics;

        if(sharePersistenceMetrics != null)
            registry.AddBeforeCollectCallback(PublishSharePersistenceMetrics);
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    private readonly IMessageBus messageBus;
    private readonly ISharePersistenceQueueMetricsProvider sharePersistenceMetrics;

    private Summary btStreamLatencySummary;
    private Counter shareCounter;
    private Summary rpcRequestDurationSummary;
    private Summary stratumRequestDurationSummary;
    private Summary apiRequestDurationSummary;
    private Counter validShareCounter;
    private Counter invalidShareCounter;
    private Summary hashComputationSummary;
    private Gauge poolConnectionsGauge;
    private Gauge poolHashrateGauge;
    private Gauge sharePersistenceQueueDepthGauge;
    private Gauge sharePersistenceQueueHighWatermarkGauge;
    private Gauge sharePersistenceQueueCapacityGauge;
    private Counter sharePersistenceQueueOverflowCounter;
    private Summary auxiliaryTemplateRpcDurationSummary;
    private Counter auxiliaryTemplateRpcOutcomeCounter;
    private Counter auxiliaryTemplateFallbackCounter;
    private Gauge auxiliaryTemplateDegradedGauge;
    private readonly object sharePersistenceOverflowPublishGate = new();
    private long publishedPrimaryOverflowCount;
    private long publishedEmergencyOverflowCount;

    private void CreateMetrics(IMetricFactory metricFactory)
    {
        poolConnectionsGauge = metricFactory.CreateGauge("miningcore_pool_connections", "Number of connections per pool", new GaugeConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        poolHashrateGauge = metricFactory.CreateGauge("miningcore_pool_hashrate", "Hashrate per pool", new GaugeConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        btStreamLatencySummary = metricFactory.CreateSummary("miningcore_btstream_latency", "Latency of streaming block-templates in ms", new SummaryConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        shareCounter = metricFactory.CreateCounter("miningcore_shares_total", "Received shares per pool", new CounterConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        validShareCounter = metricFactory.CreateCounter("miningcore_valid_shares_total", "Valid received shares per pool", new CounterConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        invalidShareCounter = metricFactory.CreateCounter("miningcore_invalid_shares_total", "Invalid received shares per pool", new CounterConfiguration
        {
            LabelNames = new[] { "pool" }
        });

        rpcRequestDurationSummary = metricFactory.CreateSummary("miningcore_rpcrequest_execution_time", "RPC request execution time ms", new SummaryConfiguration
        {
            LabelNames = new[] { "pool", "method" }
        });

        stratumRequestDurationSummary = metricFactory.CreateSummary("miningcore_stratum_request_execution_time", "Stratum request execution time ms", new SummaryConfiguration
        {
            LabelNames = new[] { "pool", "method" }
        });

        apiRequestDurationSummary = metricFactory.CreateSummary("miningcore_api_request_execution_time", "API request execution time ms", new SummaryConfiguration
        {
            LabelNames = new[] { "request" }
        });

        hashComputationSummary = metricFactory.CreateSummary("miningcore_hash_computation_time", "Hash computation time ms", new SummaryConfiguration
        {
            LabelNames = new[] { "algo" }
        });

        sharePersistenceQueueDepthGauge = metricFactory.CreateGauge(
            "miningcore_share_persistence_queue_depth",
            "Current number of shares waiting in a bounded persistence queue",
            new GaugeConfiguration { LabelNames = new[] { "queue" } });
        sharePersistenceQueueHighWatermarkGauge = metricFactory.CreateGauge(
            "miningcore_share_persistence_queue_high_watermark",
            "Largest observed number of shares waiting in a bounded persistence queue",
            new GaugeConfiguration { LabelNames = new[] { "queue" } });
        sharePersistenceQueueCapacityGauge = metricFactory.CreateGauge(
            "miningcore_share_persistence_queue_capacity",
            "Configured capacity of a bounded share persistence queue",
            new GaugeConfiguration { LabelNames = new[] { "queue" } });
        sharePersistenceQueueOverflowCounter = metricFactory.CreateCounter(
            "miningcore_share_persistence_queue_overflow_total",
            "Number of writes rejected by a bounded share persistence queue",
            new CounterConfiguration { LabelNames = new[] { "queue" } });

        auxiliaryTemplateRpcDurationSummary = metricFactory.CreateSummary(
            "miningcore_auxiliary_template_rpc_duration_ms",
            "Duration of auxiliary createauxblock RPC attempts in milliseconds",
            new SummaryConfiguration
            {
                LabelNames = new[] { "pool", "aux_pool", "phase", "outcome" },
            });
        auxiliaryTemplateRpcOutcomeCounter = metricFactory.CreateCounter(
            "miningcore_auxiliary_template_rpc_total",
            "Number of auxiliary createauxblock RPC attempts by outcome",
            new CounterConfiguration
            {
                LabelNames = new[] { "pool", "aux_pool", "phase", "outcome" },
            });
        auxiliaryTemplateFallbackCounter = metricFactory.CreateCounter(
            "miningcore_auxiliary_template_fallback_total",
            "Number of auxiliary template refreshes that reused the last valid template",
            new CounterConfiguration { LabelNames = new[] { "pool", "aux_pool" } });
        auxiliaryTemplateDegradedGauge = metricFactory.CreateGauge(
            "miningcore_auxiliary_template_degraded",
            "Whether parent mining is currently using a cached auxiliary template",
            new GaugeConfiguration { LabelNames = new[] { "pool", "aux_pool" } });
    }

    private void PublishSharePersistenceMetrics()
    {
        var primary = sharePersistenceMetrics.GetPersistenceQueueMetrics();
        var emergency = sharePersistenceMetrics
            .GetEmergencyJournalQueueMetrics();
        SetSharePersistenceQueueMetrics("primary", primary);
        SetSharePersistenceQueueMetrics("emergency_journal", emergency);

        lock(sharePersistenceOverflowPublishGate)
        {
            PublishSharePersistenceOverflow("primary",
                primary.OverflowCount,
                ref publishedPrimaryOverflowCount);
            PublishSharePersistenceOverflow("emergency_journal",
                emergency.OverflowCount,
                ref publishedEmergencyOverflowCount);
        }
    }

    private void SetSharePersistenceQueueMetrics(string queue,
        SharePersistenceQueueMetricsSnapshot snapshot)
    {
        sharePersistenceQueueDepthGauge.WithLabels(queue).Set(snapshot.Depth);
        sharePersistenceQueueHighWatermarkGauge.WithLabels(queue)
            .Set(snapshot.HighWatermark);
        sharePersistenceQueueCapacityGauge.WithLabels(queue)
            .Set(snapshot.Capacity);
    }

    private void PublishSharePersistenceOverflow(string queue, long current,
        ref long published)
    {
        var counter = sharePersistenceQueueOverflowCounter.WithLabels(queue);

        if(current <= published)
            return;

        counter.Inc(current - published);
        published = current;
    }

    private void OnTelemetryEvent(TelemetryEvent msg)
    {
        switch(msg.Category)
        {
            case TelemetryCategory.Share:
                shareCounter.WithLabels(msg.GroupId).Inc();

                if(msg.Success.HasValue)
                {
                    if(msg.Success.Value)
                        validShareCounter.WithLabels(msg.GroupId).Inc();
                    else
                        invalidShareCounter.WithLabels(msg.GroupId).Inc();
                }
                break;

            case TelemetryCategory.BtStream:
                btStreamLatencySummary.WithLabels(msg.GroupId).Observe(msg.Elapsed.TotalMilliseconds);
                break;

            case TelemetryCategory.RpcRequest:
                rpcRequestDurationSummary.WithLabels(msg.GroupId, msg.Info).Observe(msg.Elapsed.TotalMilliseconds);
                break;

            case TelemetryCategory.StratumRequest:
                stratumRequestDurationSummary.WithLabels(msg.GroupId, msg.Info).Observe(msg.Elapsed.TotalMilliseconds);
                break;

            case TelemetryCategory.Connections:
                poolConnectionsGauge.WithLabels(msg.GroupId).Set(msg.Total);
                break;

            case TelemetryCategory.Hash:
                hashComputationSummary.WithLabels(msg.GroupId).Observe(msg.Elapsed.TotalMilliseconds);
                break;
        }
    }

    private void OnHashrateNotification(HashrateNotification msg)
    {
        poolHashrateGauge.WithLabels(msg.PoolId).Set(msg.Hashrate);
    }

    private void OnAuxiliaryTemplateRpcTelemetry(
        AuxiliaryTemplateRpcTelemetryEvent msg)
    {
        var outcome = GetAuxiliaryTemplateRpcOutcomeLabel(msg.Outcome);
        var phase = GetAuxiliaryTemplateRpcPhaseLabel(msg.Phase);
        auxiliaryTemplateRpcDurationSummary.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId, phase, outcome)
            .Observe(msg.Elapsed.TotalMilliseconds);
        auxiliaryTemplateRpcOutcomeCounter.WithLabels(msg.ParentPoolId,
            msg.AuxiliaryPoolId, phase, outcome).Inc();
    }

    private void OnAuxiliaryTemplateStateTelemetry(
        AuxiliaryTemplateStateTelemetryEvent msg)
    {
        auxiliaryTemplateDegradedGauge.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId)
            .Set(msg.Degraded ? 1 : 0);

        if(msg.FallbackUsed)
            auxiliaryTemplateFallbackCounter.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId).Inc();
    }

    internal static string GetAuxiliaryTemplateRpcOutcomeLabel(
        AuxiliaryTemplateRpcOutcome outcome) => outcome switch
    {
        AuxiliaryTemplateRpcOutcome.Success => "success",
        AuxiliaryTemplateRpcOutcome.RpcError => "rpc_error",
        AuxiliaryTemplateRpcOutcome.Timeout => "timeout",
        AuxiliaryTemplateRpcOutcome.Cancellation => "cancellation",
        AuxiliaryTemplateRpcOutcome.TransportFailure => "transport_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
            "Unknown auxiliary-template RPC outcome"),
    };

    internal static string GetAuxiliaryTemplateRpcPhaseLabel(
        AuxiliaryTemplateRpcPhase phase) => phase switch
    {
        AuxiliaryTemplateRpcPhase.Startup => "startup",
        AuxiliaryTemplateRpcPhase.Refresh => "refresh",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase,
            "Unknown auxiliary-template RPC phase"),
    };

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var telemetryEvents = messageBus.Listen<TelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x=> Guard(()=> OnTelemetryEvent(x), ex=> logger.Error(ex.Message)))
                .Select(_=> Unit.Default);

            var hashrateNotifications = messageBus.Listen<HashrateNotification>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x=> Guard(()=> OnHashrateNotification(x), ex=> logger.Error(ex.Message)))
                .Select(_=> Unit.Default);

            var auxiliaryTemplateRpcTelemetry = messageBus
                .Listen<AuxiliaryTemplateRpcTelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x => Guard(() => OnAuxiliaryTemplateRpcTelemetry(x),
                    ex => logger.Error(ex.Message)))
                .Select(_ => Unit.Default);

            var auxiliaryTemplateStateTelemetry = messageBus
                .Listen<AuxiliaryTemplateStateTelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x => Guard(() => OnAuxiliaryTemplateStateTelemetry(x),
                    ex => logger.Error(ex.Message)))
                .Select(_ => Unit.Default);

            var processing = Observable.Merge(telemetryEvents, hashrateNotifications,
                    auxiliaryTemplateRpcTelemetry, auxiliaryTemplateStateTelemetry)
                .ToTask(ct);
            SignalStartupReady();
            return processing;
        }

        catch(Exception ex)
        {
            SignalStartupFailure(ex);
            return Task.FromException(ex);
        }
    }
}
