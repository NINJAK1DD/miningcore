using System.Collections.Concurrent;

namespace Miningcore.Blockchain.Bitcoin;

public interface IActiveBlockGracePeriodTracker
{
    bool TryAcquireNotification(string poolId, long blockId, string blockHash, string blockType,
        DateTime now, TimeSpan alertAfter);

    void MarkNotificationSent(string poolId, long blockId, string blockHash, string blockType);

    void ReleaseNotification(string poolId, long blockId, string blockHash, string blockType);

    void Clear(string poolId, long blockId, string blockHash, string blockType);
}

public class ActiveBlockGracePeriodTracker : IActiveBlockGracePeriodTracker
{
    private readonly ConcurrentDictionary<string, Episode> episodes =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquireNotification(string poolId, long blockId, string blockHash,
        string blockType, DateTime now, TimeSpan alertAfter)
    {
        var episode = episodes.GetOrAdd(CreateKey(poolId, blockId, blockHash, blockType),
            _ => new Episode(now));

        lock(episode)
        {
            if(now - episode.FirstUnavailableAt < alertAfter)
                return false;

            if(episode.NotificationSent || episode.NotificationInProgress)
                return false;

            episode.NotificationInProgress = true;
            return true;
        }
    }

    public void MarkNotificationSent(string poolId, long blockId, string blockHash,
        string blockType)
    {
        if(!episodes.TryGetValue(CreateKey(poolId, blockId, blockHash, blockType), out var episode))
            return;

        lock(episode)
        {
            episode.NotificationInProgress = false;
            episode.NotificationSent = true;
        }
    }

    public void ReleaseNotification(string poolId, long blockId, string blockHash,
        string blockType)
    {
        if(!episodes.TryGetValue(CreateKey(poolId, blockId, blockHash, blockType), out var episode))
            return;

        lock(episode)
        {
            if(!episode.NotificationSent)
                episode.NotificationInProgress = false;
        }
    }

    public void Clear(string poolId, long blockId, string blockHash, string blockType)
    {
        episodes.TryRemove(CreateKey(poolId, blockId, blockHash, blockType), out _);
    }

    private static string CreateKey(string poolId, long blockId, string blockHash,
        string blockType)
    {
        return $"{poolId}:{blockId}:{blockHash}:{blockType}";
    }

    private class Episode
    {
        public Episode(DateTime firstUnavailableAt)
        {
            FirstUnavailableAt = firstUnavailableAt;
        }

        public DateTime FirstUnavailableAt { get; }
        public bool NotificationSent { get; set; }
        public bool NotificationInProgress { get; set; }
    }
}
