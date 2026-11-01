using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Prometheus;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlMetricsTests
{
    [Fact]
    public async Task ScrapeContainsBoundedOutcomesSizesAndOperatingState()
    {
        var registry = Metrics.NewCustomRegistry();
        var metrics = new PearlMetrics(Metrics.WithCustomRegistry(registry));
        foreach(var measure in Enum.GetValues<PearlMeasure>()) metrics.Set("pearl", measure, 2);
        foreach(var outcome in Enum.GetValues<PearlEvent>()) metrics.Count("pearl", outcome);
        metrics.Proof("pearl", PearlProofPhase.Verify, 800000, TimeSpan.FromMilliseconds(40));
        metrics.Proof("pearl", PearlProofPhase.Prove, 50000, TimeSpan.FromSeconds(47));
        metrics.Set("pearl", PearlMeasure.Height, double.NaN);
        metrics.Set("pearl", (PearlMeasure) 999, 1);
        metrics.Count("pearl", (PearlEvent) 999);
        metrics.Proof("pearl", (PearlProofPhase) 999, 1, TimeSpan.Zero);
        await using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("miningcore_pearl_state{pool=\"pearl\",measure=\"Height\"} 2", text);
        Assert.Contains("miningcore_pearl_events_total{pool=\"pearl\",event=\"PayoutUncertain\"} 1", text);
        Assert.Contains("miningcore_pearl_proof_bytes_sum{pool=\"pearl\",phase=\"Verify\"} 800000", text);
        Assert.Contains("miningcore_pearl_proof_seconds_sum{pool=\"pearl\",phase=\"Prove\"} 47", text);
        Assert.DoesNotContain("999", text);
        Assert.DoesNotContain("NaN", text);
    }

    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public void JobMetricsFollowTipExpiryAndDifficultyGrace()
    {
        var time = new Clock();
        var worker = new PearlWorkerContext(time) {Difficulty = 1};
        var block = PearlWorkTests.Block();
        worker.Issue(block, 4);
        Assert.Equal((1, 0), worker.JobCounts(block.PreviousBlockHash, 60, 10));
        worker.Difficulty = 2;
        worker.Issue(block, 4);
        Assert.Equal((2, 0), worker.JobCounts(block.PreviousBlockHash, 60, 10));
        time.Seconds = 10;
        Assert.Equal((1, 1), worker.JobCounts(block.PreviousBlockHash, 60, 10));
        Assert.Equal((0, 2), worker.JobCounts("another tip", 60, 10));
        time.Seconds = 60;
        Assert.Equal((0, 2), worker.JobCounts(block.PreviousBlockHash, 60, 10));
    }
}
