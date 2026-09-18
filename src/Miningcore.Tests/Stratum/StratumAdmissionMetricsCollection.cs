using Xunit;

namespace Miningcore.Tests.Stratum;

// Admission fixtures inspect the production collectors in the process-wide registry.
// prometheus-net 8.2.0's UpdateRegistryMetrics enumerates its family dictionary without
// its registration lock. A full-suite run reproduced a scrape/registration race while
// unrelated fixtures initialized their collectors. Isolate these registry assertions
// from other collections; keep the fixtures' deliberate concurrent connections intact.
// Revisit when the dependency protects registry self-metrics during registration.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StratumAdmissionMetricsCollection
{
    public const string Name = "Stratum admission registry metrics";
}
