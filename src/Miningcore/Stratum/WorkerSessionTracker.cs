using System.Collections.Concurrent;

namespace Miningcore.Stratum;

public static class WorkerSessionTracker
{
    private sealed class SessionEntry
    {
        public string SessionId { get; set; }
        public string IpAddress { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, SessionEntry> Sessions = new();
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(10);

    private static string MakeKey(string poolId, string miner, string worker)
    {
        return $"{poolId}|{miner}|{worker ?? string.Empty}".ToLowerInvariant();
    }

    public static string GetOrCreateSessionId(string poolId, string miner, string worker, string ipAddress, DateTime nowUtc)
    {
        var key = MakeKey(poolId, miner, worker);
        var normalizedIp = ipAddress ?? string.Empty;

        if(Sessions.TryGetValue(key, out var existing))
        {
            var withinWindow = nowUtc - existing.LastSeenUtc <= ReuseWindow;
            var sameIp = string.Equals(existing.IpAddress ?? string.Empty, normalizedIp, StringComparison.OrdinalIgnoreCase);

            if(withinWindow && sameIp)
            {
                existing.LastSeenUtc = nowUtc;
                return existing.SessionId;
            }
        }

        var sessionId = Guid.NewGuid().ToString("N");

        Sessions[key] = new SessionEntry
        {
            SessionId = sessionId,
            IpAddress = normalizedIp,
            LastSeenUtc = nowUtc
        };

        return sessionId;
    }

    public static string GetCurrentSessionId(string poolId, string miner, string worker, DateTime nowUtc)
    {
        var key = MakeKey(poolId, miner, worker);

        if(Sessions.TryGetValue(key, out var existing))
        {
            var withinWindow = nowUtc - existing.LastSeenUtc <= ReuseWindow;

            if(withinWindow)
                return existing.SessionId;
        }

        return null;
    }

    public static void Touch(string poolId, string miner, string worker, string sessionId, string ipAddress, DateTime nowUtc)
    {
        var key = MakeKey(poolId, miner, worker);

        Sessions.AddOrUpdate(key,
            _ => new SessionEntry
            {
                SessionId = sessionId,
                IpAddress = ipAddress ?? string.Empty,
                LastSeenUtc = nowUtc
            },
            (_, existing) =>
            {
                existing.SessionId = sessionId;
                existing.IpAddress = ipAddress ?? string.Empty;
                existing.LastSeenUtc = nowUtc;
                return existing;
            });
    }
}
