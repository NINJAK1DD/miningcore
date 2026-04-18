using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using MinerStats = Miningcore.Persistence.Model.Projections.MinerStats;

namespace Miningcore.Persistence.Postgres.Repositories;

public class StatsRepository : IStatsRepository
{
    public StatsRepository(IMapper mapper, IMasterClock clock)
    {
        this.mapper = mapper;
        this.clock = clock;
    }

    private readonly IMapper mapper;
    private readonly IMasterClock clock;
    private static readonly TimeSpan MinerStatsMaxAge = TimeSpan.FromMinutes(20);
    
    private sealed class WorkerBestShareRecord
    {
        public string Worker { get; set; }
        public double BestShare { get; set; }
    }

    private sealed class WorkerBestSessionShareRecord
    {
        public string Worker { get; set; }
        public string SessionId { get; set; }
        public double BestSessionShare { get; set; }
    }

    public async Task InsertPoolStatsAsync(IDbConnection con, IDbTransaction tx, PoolStats stats, CancellationToken ct)
    {
        var mapped = mapper.Map<Entities.PoolStats>(stats);

        const string query = @"INSERT INTO poolstats(poolid, connectedminers, poolhashrate, networkhashrate,
            networkdifficulty, lastnetworkblocktime, blockheight, connectedpeers, sharespersecond, created)
            VALUES(@poolid, @connectedminers, @poolhashrate, @networkhashrate, @networkdifficulty,
            @lastnetworkblocktime, @blockheight, @connectedpeers, @sharespersecond, @created)";

        await con.ExecuteAsync(new CommandDefinition(query, mapped, tx, cancellationToken: ct));
    }

    public async Task InsertMinerWorkerPerformanceStatsAsync(IDbConnection con, IDbTransaction tx, MinerWorkerPerformanceStats stats, CancellationToken ct)
    {
        var mapped = mapper.Map<Entities.MinerWorkerPerformanceStats>(stats);

        if(string.IsNullOrEmpty(mapped.Worker))
            mapped.Worker = string.Empty;

        const string query = @"INSERT INTO minerstats(poolid, miner, worker, sessionid, hashrate, sharespersecond, created)
            VALUES(@poolid, @miner, @worker, @sessionid, @hashrate, @sharespersecond, @created)";

        await con.ExecuteAsync(new CommandDefinition(query, mapped, tx, cancellationToken: ct));
    }

    public async Task<PoolStats> GetLastPoolStatsAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = "SELECT * FROM poolstats WHERE poolid = @poolId ORDER BY created DESC FETCH NEXT 1 ROWS ONLY";

        var entity = await con.QuerySingleOrDefaultAsync<Entities.PoolStats>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));

        return entity == null ? null : mapper.Map<PoolStats>(entity);
    }

    public Task<decimal> GetTotalPoolPaymentsAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query = "SELECT sum(amount) FROM payments WHERE poolid = @poolId";

        return con.ExecuteScalarAsync<decimal>(new CommandDefinition(query, new { poolId }, cancellationToken: ct));
    }

    public async Task<PoolStats[]> GetPoolPerformanceBetweenAsync(IDbConnection con, string poolId,
        SampleInterval interval, DateTime start, DateTime end, CancellationToken ct)
    {
        var trunc = interval switch
        {
            SampleInterval.Hour => "hour",
            SampleInterval.Day => "day",
            _ => null
        };

        var query = @$"SELECT date_trunc('{trunc}', created) AS created,
            AVG(poolhashrate) AS poolhashrate, AVG(networkhashrate) AS networkhashrate, AVG(networkdifficulty) AS networkdifficulty,
            CAST(AVG(connectedminers) AS BIGINT) AS connectedminers
            FROM poolstats
            WHERE poolid = @poolId AND created >= @start AND created <= @end
            GROUP BY date_trunc('{trunc}', created)
            ORDER BY created;";

        return (await con.QueryAsync<Entities.PoolStats>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct)))
            .Select(mapper.Map<PoolStats>)
            .ToArray();
    }

    public async Task<MinerStats> GetMinerStatsAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        var query = @"
            SELECT
                COALESCE((SELECT SUM(difficulty) FROM shares WHERE poolid = @poolId AND miner = @miner), 0) AS pendingshares,
                COALESCE((SELECT amount FROM balances WHERE poolid = @poolId AND address = @miner), 0) AS pendingbalance,
                COALESCE((SELECT SUM(amount) FROM payments WHERE poolid = @poolId AND address = @miner), 0) AS totalpaid,
                COALESCE((SELECT SUM(amount) FROM payments WHERE poolid = @poolId AND address = @miner AND created >= date_trunc('day', now())), 0) AS todaypaid,
                COALESCE((SELECT MAX(actualdifficulty) FROM shares WHERE poolid = @poolId AND miner = @miner), 0) AS bestshare,
                0 AS bestsessionshare";

        var result = await con.QuerySingleOrDefaultAsync<MinerStats>(
            new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct));

        if(result != null)
        {
            query = @"SELECT * FROM payments WHERE poolid = @poolId AND address = @miner
                ORDER BY created DESC LIMIT 1";

            result.LastPayment = await con.QuerySingleOrDefaultAsync<Payment>(
                new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct));

            query = @"SELECT created FROM minerstats WHERE poolid = @poolId AND miner = @miner
                ORDER BY created DESC LIMIT 1";

            var lastUpdate = await con.QuerySingleOrDefaultAsync<DateTime?>(
                new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct));

            if(lastUpdate.HasValue && (clock.Now - DateTime.SpecifyKind(lastUpdate.Value, DateTimeKind.Utc) > MinerStatsMaxAge))
                lastUpdate = null;

            if(lastUpdate.HasValue)
            {
                query = @"SELECT COALESCE(worker, '') AS worker, COALESCE(MAX(actualdifficulty), 0) AS bestshare
                    FROM shares
                    WHERE poolid = @poolId AND miner = @miner
                    GROUP BY COALESCE(worker, '')";

                var workerBestShares = (await con.QueryAsync<WorkerBestShareRecord>(
                    new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct)))
                    .ToDictionary(x => x.Worker ?? string.Empty, x => x.BestShare);

                query = @"SELECT * FROM minerstats WHERE poolid = @poolId AND miner = @miner AND created = @created";

                var stats = (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(
                        new CommandDefinition(query, new { poolId, miner, created = lastUpdate }, tx, cancellationToken: ct)))
                    .Select(mapper.Map<MinerWorkerPerformanceStats>)
                    .ToArray();

                var currentSessionIds = stats
                    .Where(x => !string.IsNullOrEmpty(x.SessionId))
                    .Select(x => x.SessionId)
                    .Distinct()
                    .ToArray();

                Dictionary<string, double> workerSessionBestShares = new();

                if(currentSessionIds.Length > 0)
                {
                    query = @"SELECT COALESCE(MAX(actualdifficulty), 0)
                        FROM shares
                        WHERE poolid = @poolId AND miner = @miner AND sessionid = ANY(@sessionIds)";

                    result.BestSessionShare = await con.ExecuteScalarAsync<double>(
                        new CommandDefinition(query, new { poolId, miner, sessionIds = currentSessionIds }, tx, cancellationToken: ct));

                    query = @"SELECT COALESCE(worker, '') AS worker, sessionid,
                        COALESCE(MAX(actualdifficulty), 0) AS bestsessionshare
                        FROM shares
                        WHERE poolid = @poolId AND miner = @miner AND sessionid = ANY(@sessionIds)
                        GROUP BY COALESCE(worker, ''), sessionid";

                    workerSessionBestShares = (await con.QueryAsync<WorkerBestSessionShareRecord>(
                        new CommandDefinition(query, new { poolId, miner, sessionIds = currentSessionIds }, tx, cancellationToken: ct)))
                        .ToDictionary(
                            x => $"{x.Worker ?? string.Empty}|{x.SessionId ?? string.Empty}",
                            x => x.BestSessionShare);
                }
                else
                {
                    result.BestSessionShare = 0;
                }

                if(stats.Any())
                {
                    foreach(var stat in stats)
                    {
                        if(stat.Worker == null)
                        {
                            stat.Worker = string.Empty;
                            break;
                        }
                    }

                    result.Performance = new WorkerPerformanceStatsContainer
                    {
                        Workers = stats.ToDictionary(x => x.Worker ?? string.Empty, x => new WorkerPerformanceStats
                        {
                            Hashrate = x.Hashrate,
                            SharesPerSecond = x.SharesPerSecond,
                            BestSessionShare = workerSessionBestShares.TryGetValue(
                                $"{x.Worker ?? string.Empty}|{x.SessionId ?? string.Empty}", out var bestSessionShare)
                                ? bestSessionShare
                                : 0,
                            BestShare = workerBestShares.TryGetValue(x.Worker ?? string.Empty, out var bestShare)
                                ? bestShare
                                : 0
                        }),

                        Created = stats.First().Created
                    };
                }
            }
        }

        return result;
    }

    public async Task<MinerWorkerHashrate[]> GetPoolMinerWorkerHashratesAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        const string query =
            @"SELECT s.miner, s.worker, s.hashrate FROM
            (
                WITH cte AS
                (
                    SELECT
                        ROW_NUMBER() OVER (partition BY miner, worker ORDER BY created DESC) as rk,
                        miner, worker, hashrate
                    FROM minerstats
                    WHERE poolid = @poolId
                )
                SELECT miner, worker, hashrate
                FROM cte
                WHERE rk = 1
            ) s
            WHERE s.hashrate > 0;";

        return (await con.QueryAsync<MinerWorkerHashrate>(new CommandDefinition(query, new { poolId }, cancellationToken: ct)))
            .ToArray();
    }

    public async Task<WorkerPerformanceStatsContainer[]> GetMinerPerformanceBetweenThreeMinutelyAsync(IDbConnection con, string poolId, string miner,
        DateTime start, DateTime end, CancellationToken ct)
    {
         const string query = @"SELECT date_trunc('hour', created) AS created,
            (extract(minute FROM created)::int / 3) AS partition,
            worker, AVG(hashrate) AS hashrate, AVG(sharespersecond) AS sharespersecond
            FROM minerstats
            WHERE poolid = @poolId AND miner = @miner AND created >= @start AND created <= @end
            GROUP BY 1, 2, worker
            ORDER BY 1, 2, worker";

        var entities = (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(new CommandDefinition(query,
                new { poolId, miner, start, end }, cancellationToken: ct)))
            .ToArray();

        foreach(var entity in entities)
        {
            // ensure worker is not null
            entity.Worker ??= string.Empty;

            // adjust creation time by partition
            entity.Created = entity.Created.AddMinutes(3 * entity.Partition);
        }

        // group
        var entitiesByDate = entities
            .GroupBy(x => x.Created);

        var tmp = entitiesByDate.Select(x => new WorkerPerformanceStatsContainer
        {
            Created = x.Key,
            Workers = x.ToDictionary(y => y.Worker ?? string.Empty, y => new WorkerPerformanceStats
            {
                Hashrate = y.Hashrate,
                SharesPerSecond = y.SharesPerSecond
            })
        })
        .ToArray();

        return tmp;
    }

    public async Task<WorkerPerformanceStatsContainer[]> GetMinerPerformanceBetweenMinutelyAsync(IDbConnection con, string poolId, string miner,
        DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"SELECT worker, date_trunc('minute', created) AS created, AVG(hashrate) AS hashrate,
            AVG(sharespersecond) AS sharespersecond FROM minerstats
            WHERE poolid = @poolId AND miner = @miner AND created >= @start AND created <= @end
            GROUP BY date_trunc('minute', created), worker
            ORDER BY created, worker;";

        var entities = (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(new CommandDefinition(query,
                new { poolId, miner, start, end }, cancellationToken: ct)))
            .ToArray();

        // ensure worker is not null
        foreach(var entity in entities)
            entity.Worker ??= string.Empty;

        // group
        var entitiesByDate = entities
            .GroupBy(x => x.Created);

        var tmp = entitiesByDate.Select(x => new WorkerPerformanceStatsContainer
        {
            Created = x.Key,
            Workers = x.ToDictionary(y => y.Worker ?? string.Empty, y => new WorkerPerformanceStats
            {
                Hashrate = y.Hashrate,
                SharesPerSecond = y.SharesPerSecond
            })
        })
        .ToArray();

        return tmp;
    }

    public async Task<WorkerPerformanceStatsContainer[]> GetMinerPerformanceBetweenHourlyAsync(IDbConnection con, string poolId, string miner,
        DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"SELECT worker, date_trunc('hour', created) AS created, AVG(hashrate) AS hashrate,
            AVG(sharespersecond) AS sharespersecond FROM minerstats
            WHERE poolid = @poolId AND miner = @miner AND created >= @start AND created <= @end
            GROUP BY date_trunc('hour', created), worker
            ORDER BY created, worker;";

        var entities = (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(new CommandDefinition(query,
                new { poolId, miner, start, end }, cancellationToken: ct)))
            .ToArray();

        // ensure worker is not null
        foreach(var entity in entities)
            entity.Worker ??= string.Empty;

        // group
        var entitiesByDate = entities
            .GroupBy(x => x.Created);

        var tmp = entitiesByDate.Select(x => new WorkerPerformanceStatsContainer
        {
            Created = x.Key,
            Workers = x.ToDictionary(y => y.Worker ?? string.Empty, y => new WorkerPerformanceStats
            {
                Hashrate = y.Hashrate,
                SharesPerSecond = y.SharesPerSecond
            })
        })
        .ToArray();

        return tmp;
    }

    public async Task<WorkerPerformanceStatsContainer[]> GetMinerPerformanceBetweenDailyAsync(IDbConnection con,  string poolId, string miner,
        DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"SELECT worker, date_trunc('day', created) AS created, AVG(hashrate) AS hashrate,
            AVG(sharespersecond) AS sharespersecond FROM minerstats
            WHERE poolid = @poolId AND miner = @miner AND created >= @start AND created <= @end
            GROUP BY date_trunc('day', created), worker
            ORDER BY created, worker;";

        var entitiesByDate = (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(new CommandDefinition(query,
                new { poolId, miner, start, end }, cancellationToken: ct)))
            .ToArray()
            .GroupBy(x => x.Created);

        var tmp = entitiesByDate.Select(x => new WorkerPerformanceStatsContainer
        {
            Created = x.Key,
            Workers = x.ToDictionary(y => y.Worker ?? string.Empty, y => new WorkerPerformanceStats
            {
                Hashrate = y.Hashrate,
                SharesPerSecond = y.SharesPerSecond
            })
        })
        .ToArray();

        return tmp;
    }

    public async Task<MinerWorkerPerformanceStats[]> PagePoolMinersByHashrateAsync(IDbConnection con, string poolId,
        DateTime from, int page, int pageSize, CancellationToken ct)
    {
        const string query =
            @"WITH tmp AS
            (
            	SELECT
            		ms.miner,
            		ms.hashrate,
            		ms.sharespersecond,
            		ROW_NUMBER() OVER(PARTITION BY ms.miner ORDER BY ms.hashrate DESC) AS rk
            	FROM (SELECT miner, SUM(hashrate) AS hashrate, SUM(sharespersecond) AS sharespersecond
                   FROM minerstats
                   WHERE poolid = @poolid AND created >= @from GROUP BY miner, created) ms
            )
            SELECT t.miner, t.hashrate, t.sharespersecond
            FROM tmp t
            WHERE t.rk = 1
            ORDER by t.hashrate DESC
            OFFSET @offset FETCH NEXT @pageSize ROWS ONLY";

        return (await con.QueryAsync<Entities.MinerWorkerPerformanceStats>(new CommandDefinition(query,
                new { poolId, from, offset = page * pageSize, pageSize }, cancellationToken: ct)))
            .Select(mapper.Map<MinerWorkerPerformanceStats>)
            .ToArray();
    }

    public Task<int> DeletePoolStatsBeforeAsync(IDbConnection con, DateTime date, CancellationToken ct)
    {
        const string query = @"DELETE FROM poolstats WHERE created < @date";

        return con.ExecuteAsync(new CommandDefinition(query, new { date }, cancellationToken: ct));
    }

    public Task<int> DeleteMinerStatsBeforeAsync(IDbConnection con, DateTime date, CancellationToken ct)
    {
        const string query = @"DELETE FROM minerstats WHERE created < @date";

        return con.ExecuteAsync(new CommandDefinition(query, new { date }, cancellationToken: ct));
    }

    public Task<uint> GetMinerTotalConfirmedBlocksAsync(IDbConnection con, string poolId, string miner, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND miner = @miner AND status = 'confirmed'";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId, miner }, cancellationToken: ct));
    }

    public Task<uint> GetMinerTotalPendingBlocksAsync(IDbConnection con, string poolId, string miner, CancellationToken ct)
    {
        const string query = @"SELECT COUNT(*) FROM blocks WHERE poolid = @poolId AND miner = @miner AND status = 'pending'";

        return con.ExecuteScalarAsync<uint>(new CommandDefinition(query, new { poolId, miner }, cancellationToken: ct));
    }
}
