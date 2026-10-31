using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Rest;

namespace Miningcore.Pushover;

public class PushoverClient
{
    // Pushover asks clients to keep at most two requests in flight. Reserve one
    // slot for critical durability alerts when ordinary traffic is active.
    private static readonly SemaphoreSlim Requests = new(2, 2);
    private static readonly SemaphoreSlim OrdinaryRequests = new(1, 1);

    public PushoverClient(ClusterConfig clusterConfig, IHttpClientFactory httpClientFactory)
    {
        config = clusterConfig?.Notifications?.Pushover;
        client = new SimpleRestClient(httpClientFactory, PushoverConstants.ApiBaseUrl);
    }

    private readonly SimpleRestClient client;
    private readonly PushoverConfig config;

    public async Task<PushoverReponse> PushMessage(string title, string message,
        PushoverMessagePriority priority, CancellationToken ct,
        bool critical = false)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(title));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(message));

        var msg = new PushoverRequest
        {
            User = config.User,
            Token = config.Token,
            Title = PushoverMessagePolicy.Title(title),
            Message = PushoverMessagePolicy.Message(message),
            Priority = (int) priority,
            Timestamp = (int) DateTimeOffset.Now.ToUnixTimeSeconds(),
        };

        var ordinaryHeld = false;
        var requestHeld = false;
        try
        {
            if(!critical)
            {
                await OrdinaryRequests.WaitAsync(ct);
                ordinaryHeld = true;
            }

            await Requests.WaitAsync(ct);
            requestHeld = true;
            var response = await client.Post<PushoverReponse>("/messages.json", msg, ct);
            if(response?.Status != 1)
                throw new IOException("Pushover did not confirm message acceptance");
            return response;
        }
        finally
        {
            if(requestHeld)
                Requests.Release();
            if(ordinaryHeld)
                OrdinaryRequests.Release();
        }
    }
}
