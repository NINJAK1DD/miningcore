using Prometheus;

namespace Miningcore.Blockchain.Pearl;

// Labels are configuration-owned pool IDs and finite enums, never miner/RPC/proof text.
internal enum PearlMeasure { Height, CertificateVersion, MinimumRank, TemplateAgeSeconds, ActiveJobs, StaleJobs, CandidateQueue, ActiveVerifiers, ActiveProvers, CircuitCaches, PayoutState }
internal enum PearlEvent { ShareAccepted, ShareInvalid, ShareStale, ShareDuplicate, ShareBusy, RequestRejected, Unauthorized, NativeFailure, CandidateSubmitted, CandidateUncertain, CandidateAccepted, CandidateRejected, CandidateRetired, RewardPending, RewardMature, RewardOrphaned, RewardQuarantined, PayoutAttempt, PayoutUncertain, PayoutCompleted, PayoutHeld }
internal enum PearlProofPhase { Verify, Prove }

internal sealed class PearlMetrics
{
    internal static readonly PearlMetrics Default = new(Metrics.DefaultFactory);
    private readonly Gauge state;
    private readonly Counter events;
    private readonly Histogram bytes;
    private readonly Histogram duration;
    internal PearlMetrics(IMetricFactory factory)
    {
        state = factory.CreateGauge("miningcore_pearl_state", "Pearl current operating state; measure labels have documented units", new GaugeConfiguration {LabelNames = new[] {"pool", "measure"}});
        events = factory.CreateCounter("miningcore_pearl_events_total", "Pearl share, candidate, reward observation and payout outcomes", new CounterConfiguration {LabelNames = new[] {"pool", "event"}});
        bytes = factory.CreateHistogram("miningcore_pearl_proof_bytes", "Decoded proof or generated certificate bytes", new HistogramConfiguration {LabelNames = new[] {"pool", "phase"}, Buckets = new double[] {1024, 60000, 262144, 1048576, 4194304, 8388608}});
        duration = factory.CreateHistogram("miningcore_pearl_proof_seconds", "Verification or certification wall time in seconds", new HistogramConfiguration {LabelNames = new[] {"pool", "phase"}, Buckets = new double[] {.01, .05, .1, .5, 1, 5, 30, 60, 120, 180}});
    }
    // Telemetry cannot alter a financial or protocol outcome.
    internal void Set(string pool, PearlMeasure measure, double value)
    {
        if(!Enum.IsDefined(measure) || !double.IsFinite(value)) return;
        try {state.WithLabels(pool, measure.ToString()).Set(value);} catch { }
    }
    internal void Count(string pool, PearlEvent outcome)
    {
        if(!Enum.IsDefined(outcome)) return;
        try {events.WithLabels(pool, outcome.ToString()).Inc();} catch { }
    }
    internal void Proof(string pool, PearlProofPhase phase, int size, TimeSpan elapsed)
    {
        if(!Enum.IsDefined(phase) || size < 0 || elapsed < TimeSpan.Zero) return;
        try {bytes.WithLabels(pool, phase.ToString()).Observe(size); duration.WithLabels(pool, phase.ToString()).Observe(elapsed.TotalSeconds);} catch { }
    }
}
