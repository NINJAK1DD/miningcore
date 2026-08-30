using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Miningcore.Blockchain;
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
    private Histogram auxiliaryTemplateRpcDurationHistogram;
    private Counter auxiliaryTemplateFallbackCounter;
    private Gauge auxiliaryTemplateAvailableGauge;
    private Gauge auxiliaryTemplateDegradedGauge;
    private Counter shareAccountingBatchCounter;
    private Counter shareAccountingProjectionCounter;
    private Counter ppsCreditCounter;
    private Counter ppsLiabilityCounter;
    private Counter mergedMiningAttributionRejectionCounter;
    private Counter unsupportedShareRelayWireFormatCounter;
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

        auxiliaryTemplateRpcDurationHistogram = metricFactory.CreateHistogram(
            "miningcore_auxiliary_template_rpc_duration_seconds",
            "Duration of auxiliary createauxblock RPC attempts in seconds",
            new HistogramConfiguration
            {
                LabelNames = new[] { "pool", "aux_pool", "phase", "outcome" },
                Buckets = new[]
                {
                    0.05, 0.1, 0.2, 0.35, 0.5, 0.75, 1, 2, 5, 10, 15,
                },
            });
        auxiliaryTemplateFallbackCounter = metricFactory.CreateCounter(
            "miningcore_auxiliary_template_fallback_total",
            "Number of auxiliary template fallback episodes",
            new CounterConfiguration { LabelNames = new[] { "pool", "aux_pool" } });
        auxiliaryTemplateAvailableGauge = metricFactory.CreateGauge(
            "miningcore_auxiliary_template_available",
            "Whether a usable auxiliary template is currently available",
            new GaugeConfiguration { LabelNames = new[] { "pool", "aux_pool" } });
        auxiliaryTemplateDegradedGauge = metricFactory.CreateGauge(
            "miningcore_auxiliary_template_degraded",
            "Whether parent mining is currently using a cached auxiliary template",
            new GaugeConfiguration { LabelNames = new[] { "pool", "aux_pool" } });
        shareAccountingBatchCounter = metricFactory.CreateCounter(
            "miningcore_share_accounting_batches_total",
            "Durable correlated share-accounting groups by commit outcome",
            new CounterConfiguration { LabelNames = new[] { "outcome" } });
        shareAccountingProjectionCounter = metricFactory.CreateCounter(
            "miningcore_share_accounting_projections_total",
            "Durably committed share projections by pool, role, and commit outcome",
            new CounterConfiguration
            {
                LabelNames = new[] { "pool", "role", "outcome" },
            });
        ppsCreditCounter = metricFactory.CreateCounter(
            "miningcore_pps_share_credits_total",
            "Durably committed PPS share liabilities by pool and commit outcome",
            new CounterConfiguration { LabelNames = new[] { "pool", "outcome" } });
        ppsLiabilityCounter = metricFactory.CreateCounter(
            "miningcore_pps_liability_coin_total",
            "Exact calculated PPS liability in whole coin units before database balance rounding",
            new CounterConfiguration { LabelNames = new[] { "pool" } });
        mergedMiningAttributionRejectionCounter = metricFactory.CreateCounter(
            "miningcore_merged_mining_attribution_rejections_total",
            "Merged-mining logins rejected before accepting unattributed auxiliary work",
            new CounterConfiguration
            {
                LabelNames = new[] { "pool", "aux_pool", "reason" },
            });
        unsupportedShareRelayWireFormatCounter = metricFactory.CreateCounter(
            "miningcore_share_relay_unsupported_wire_format_total",
            "Rejected share-relay messages using an unsupported financial wire format",
            new CounterConfiguration
            {
                LabelNames = new[] { "relay", "format" },
            });
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
        auxiliaryTemplateRpcDurationHistogram.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId, phase, outcome)
            .Observe(msg.Elapsed.TotalSeconds);
    }

    private void OnAuxiliaryTemplateStateTelemetry(
        AuxiliaryTemplateStateTelemetryEvent msg)
    {
        auxiliaryTemplateAvailableGauge.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId)
            .Set(msg.Available ? 1 : 0);
        auxiliaryTemplateDegradedGauge.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId)
            .Set(msg.Degraded ? 1 : 0);

        if(msg.FallbackStarted)
            auxiliaryTemplateFallbackCounter.WithLabels(msg.ParentPoolId,
                msg.AuxiliaryPoolId).Inc();
    }

    private void OnShareAccountingTelemetry(ShareAccountingTelemetryEvent msg)
    {
        var outcome = msg.Outcome switch
        {
            Persistence.Model.ShareAccountingInsertResult.Inserted => "inserted",
            Persistence.Model.ShareAccountingInsertResult.AlreadyCommitted =>
                "replay_suppressed",
            _ => throw new ArgumentOutOfRangeException(nameof(msg.Outcome)),
        };
        shareAccountingBatchCounter.WithLabels(outcome).Inc();

        foreach(var projection in msg.Projections)
        {
            var role = projection.Role switch
            {
                ShareAccountingRole.Single => "single",
                ShareAccountingRole.Parent => "parent",
                ShareAccountingRole.Auxiliary => "auxiliary",
                _ => "invalid",
            };
            shareAccountingProjectionCounter.WithLabels(projection.PoolId,
                role, outcome).Inc();
        }

        foreach(var credit in msg.PpsCredits)
        {
            ppsCreditCounter.WithLabels(credit.PoolId, outcome).Inc();
            if(msg.Outcome == Persistence.Model.ShareAccountingInsertResult.Inserted)
                ppsLiabilityCounter.WithLabels(credit.PoolId)
                    .Inc(decimal.ToDouble(credit.Amount));
        }
    }

    private void OnMergedMiningAttributionRejected(
        MergedMiningAttributionRejectedTelemetryEvent msg)
    {
        var reason = msg.Reason switch
        {
            MergedMiningAttributionRejection.Missing => "missing",
            MergedMiningAttributionRejection.Invalid => "invalid",
            MergedMiningAttributionRejection.ValidationUnavailable =>
                "validation_unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(msg.Reason)),
        };
        mergedMiningAttributionRejectionCounter.WithLabels(msg.ParentPoolId,
            msg.AuxiliaryPoolId, reason).Inc();
    }

    private void OnUnsupportedShareRelayWireFormat(
        UnsupportedShareRelayWireFormatTelemetryEvent msg)
    {
        unsupportedShareRelayWireFormatCounter.WithLabels(msg.RelayUrl,
            msg.WireFormat.ToString(CultureInfo.InvariantCulture)).Inc();
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

            var shareAccountingTelemetry = messageBus
                .Listen<ShareAccountingTelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x => Guard(() => OnShareAccountingTelemetry(x),
                    ex => logger.Error(ex.Message)))
                .Select(_ => Unit.Default);

            var mergedMiningAttributionRejected = messageBus
                .Listen<MergedMiningAttributionRejectedTelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x => Guard(() => OnMergedMiningAttributionRejected(x),
                    ex => logger.Error(ex.Message)))
                .Select(_ => Unit.Default);

            var unsupportedShareRelayWireFormat = messageBus
                .Listen<UnsupportedShareRelayWireFormatTelemetryEvent>()
                .ObserveOn(TaskPoolScheduler.Default)
                .Do(x => Guard(() => OnUnsupportedShareRelayWireFormat(x),
                    ex => logger.Error(ex.Message)))
                .Select(_ => Unit.Default);

            var processing = Observable.Merge(telemetryEvents, hashrateNotifications,
                    auxiliaryTemplateRpcTelemetry, auxiliaryTemplateStateTelemetry,
                    shareAccountingTelemetry, mergedMiningAttributionRejected,
                    unsupportedShareRelayWireFormat)
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
