using Microsoft.Extensions.Caching.Memory;
using Miningcore.Contracts;
using Miningcore.Nicehash.API;
using Miningcore.Rest;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Nicehash;

public class NicehashService
{
    public NicehashService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache) : this(httpClientFactory, cache, LogManager.GetCurrentClassLogger())
    {
    }

    internal NicehashService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger logger)
    {
        this.cache = cache;
        this.logger = logger;
        client = new SimpleRestClient(httpClientFactory, NicehashConstants.ApiBaseUrl);
    }

    private readonly SimpleRestClient client;
    private readonly IMemoryCache cache;

    private readonly ILogger logger;

    public Task<double?> GetStaticDiff(string coin, string algo, CancellationToken ct)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(coin));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(algo));

        return Guard(async () =>
        {
            var algos = await cache.GetOrCreateAsync("nicehash_algos", async (entry) =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);

                // query nicehash API
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(3000);

                var response = await client.Get<NicehashMiningAlgorithmsResponse>("/mining/algorithms", cts.Token);

                // transform
                return response.Algorithms.ToDictionary(x => x.Algorithm, x=> x, StringComparer.InvariantCultureIgnoreCase);
            });

            var niceHashAlgo = GetNicehashAlgo(coin, algo);

            if(!algos.TryGetValue(niceHashAlgo, out var item))
                return (double?) null;

            return item.MinimalPoolDifficulty;
        }, ex =>
        {
            // Disconnect/shutdown cancellation is expected. A service timeout or
            // an unrelated failure must still be visible, even during shutdown.
            if(ex is OperationCanceledException && ct.IsCancellationRequested)
                return;
            logger.Error(() => $"Error updating Nicehash diffs: {ex.Message}");
        });
    }

    private string GetNicehashAlgo(string coin, string algo)
    {
        if(coin == "Beam" && algo == "BeamHash")
            return "beamv3";

        if(coin == "Monero" && algo == "RandomX")
            return "randomxmonero";

        return algo;
    }
}
