using System.Text;
using Prometheus;

internal static class MetricsRegistrationProbe
{
    internal static async Task<int> RunAsync()
    {
        // Use the same default-registry self-metrics as /metrics. A plain custom
        // registry does not enable them, and would miss the 8.2.0 defect entirely.
        var registry = Metrics.DefaultRegistry;
        using(var initial = new MemoryStream())
        {
            await registry.CollectAndExportAsTextAsync(initial);
            if(!Encoding.UTF8.GetString(initial.ToArray()).Contains("prometheus_net_metric_families{"))
                throw new InvalidOperationException("Registry self-metrics must be enabled for this regression");
        }

        const int rounds = 16;
        const int familiesPerRound = 256;
        const int scrapesPerRound = 4;
        using var rendezvous = new Barrier(3);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var scrapeCount = 0;

        Task Worker(Action<int> action) => Task.Factory.StartNew(() =>
        {
            try
            {
                for(var round = 0; round < rounds; round++)
                {
                    rendezvous.SignalAndWait(stop.Token);
                    action(round);
                    rendezvous.SignalAndWait(stop.Token);
                }
            }
            catch
            {
                stop.Cancel(); // Release other workers even if collection throws.
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var registration = Worker(round =>
        {
            for(var offset = 0; offset < familiesPerRound; offset++)
            {
                var index = round * familiesPerRound + offset;
                Metrics.CreateGauge($"registration_probe_{index}", "Synthetic concurrent registration").Set(index);
                // Keep registration spread across the scrape rather than allowing
                // one fast writer to finish the entire batch before readers run.
                Thread.SpinWait(5000);
            }
        });
        Task Scraper() => Worker(_ =>
        {
            for(var i = 0; i < scrapesPerRound; i++)
            {
                registry.CollectAndExportAsTextAsync(Stream.Null, stop.Token).GetAwaiter().GetResult();
                Interlocked.Increment(ref scrapeCount);
            }
        });
        await Task.WhenAll(registration, Scraper(), Scraper());

        using var final = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(final, stop.Token);
        var samples = Encoding.UTF8.GetString(final.ToArray()).Split('\n')
            .Where(line => line.StartsWith("registration_probe_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var expectedFamilies = rounds * familiesPerRound;
        if(samples.Count != expectedFamilies ||
            Enumerable.Range(0, expectedFamilies).Any(i => !samples.Contains($"registration_probe_{i} {i}")))
            throw new InvalidOperationException("Concurrent registration lost or changed metric samples");

        Console.WriteLine($"metrics-registration: {samples.Count} families, {scrapeCount} concurrent scrapes, all values verified");
        return 0;
    }
}
