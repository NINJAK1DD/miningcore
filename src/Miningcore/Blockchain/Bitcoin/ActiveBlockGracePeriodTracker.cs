using System.Collections.Concurrent;

namespace Miningcore.Blockchain.Bitcoin;

public interface IActiveBlockGracePeriodTracker
{
    bool TryAcquireNotification(string poolId, long blockId, string blockHash, string blockType,
        DateTime now, TimeSpan alertAfter, TimeSpan? repeatAfter = null, string reason = null);

    void MarkNotificationSent(string poolId, long blockId, string blockHash, string blockType);

    void ReleaseNotification(string poolId, long blockId, string blockHash, string blockType);

    void Clear(string poolId, long blockId, string blockHash, string blockType);
}

public class ActiveBlockGracePeriodTracker : IActiveBlockGracePeriodTracker
{
    private readonly ConcurrentDictionary<string, Episode> episodes =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquireNotification(string poolId, long blockId, string blockHash,
        string blockType, DateTime now, TimeSpan alertAfter, TimeSpan? repeatAfter = null, string reason = null)
    {
        var episode = episodes.GetOrAdd(CreateKey(poolId, blockId, blockHash, blockType),
            _ => new Episode(now));

        lock(episode)
        {
            if(now - episode.FirstUnavailableAt < alertAfter)
                return false;

            if(episode.NotificationInProgress)
                return false;

            var reasonChanged = reason != null && !string.Equals(reason, episode.LastReason, StringComparison.Ordinal);
            if(episode.NotificationSent && !reasonChanged &&
               (!repeatAfter.HasValue || now - episode.LastSentAt < repeatAfter.Value))
                return false;

            episode.NotificationInProgress = true;
            episode.PendingAt = now;
            episode.PendingReason = reason;
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
            episode.LastSentAt = episode.PendingAt;
            episode.LastReason = episode.PendingReason;
        }
    }

    public void ReleaseNotification(string poolId, long blockId, string blockHash,
        string blockType)
    {
        if(!episodes.TryGetValue(CreateKey(poolId, blockId, blockHash, blockType), out var episode))
            return;

        lock(episode)
        {
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
        public DateTime LastSentAt { get; set; }
        public DateTime PendingAt { get; set; }
        public string LastReason { get; set; }
        public string PendingReason { get; set; }
    }
}
