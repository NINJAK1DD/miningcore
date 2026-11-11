using Miningcore.Configuration;
using NLog;
using Prometheus;

namespace Miningcore.Payments;

/// <summary>Process-local pause visibility, independent of API and relay configuration.</summary>
public sealed class PayoutPauseMonitor : StartupGatedBackgroundService
{
    public PayoutPauseMonitor(ClusterConfig config) : this(config, Metrics.DefaultFactory,
        message => LogManager.GetCurrentClassLogger().Warn(message)) { }

    internal PayoutPauseMonitor(ClusterConfig config, IMetricFactory factory, Action<string> warn)
    {
        // The pause is restart-only: do not make metrics imply a runtime resume is supported.
        paused = Program.IsPaymentProcessorPaused(config);
        this.warn = warn;
        factory.CreateGauge("miningcore_payout_processor_paused",
            "1 when a configured local payout processor is paused; 0 otherwise. Not a cluster-wide stop.")
            .Set(paused ? 1 : 0);
    }

    private readonly bool paused;
    private readonly Action<string> warn;

    internal void ReportPause()
    {
        if(paused)
            warn("Payout processor remains paused: reconciliation and retention are suspended. " +
                "Monitor pending blocks, accounting storage and PPS reserves; resume requires a controlled restart.");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        SignalStartupReady();
        if(!paused)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while(await timer.WaitForNextTickAsync(ct))
            ReportPause();
    }
}
