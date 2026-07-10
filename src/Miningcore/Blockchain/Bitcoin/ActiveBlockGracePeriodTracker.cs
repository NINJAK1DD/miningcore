using System.Collections.Concurrent;

namespace Miningcore.Blockchain.Bitcoin;

public interface IActiveBlockGracePeriodTracker
{
    bool ObserveUnavailable(string poolId, long blockId, string blockHash, string blockType,
        DateTime now, TimeSpan alertAfter);

    void Clear(string poolId, long blockId, string blockHash, string blockType);
}

public class ActiveBlockGracePeriodTracker : IActiveBlockGracePeriodTracker
{
    private readonly ConcurrentDictionary<string, Episode> episodes =
        new(StringComparer.OrdinalIgnoreCase);

    public bool ObserveUnavailable(string poolId, long blockId, string blockHash,
        string blockType, DateTime now, TimeSpan alertAfter)
    {
        var episode = episodes.GetOrAdd(CreateKey(poolId, blockId, blockHash, blockType),
            _ => new Episode(now));

        lock(episode)
        {
            if(now - episode.FirstUnavailableAt < alertAfter)
                return false;

            if(episode.NotificationSent)
                return false;

            episode.NotificationSent = true;
            return true;
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
    }
}
