using Autofac;
using AutoMapper;
using Microsoft.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.BitcoinBlake2b;

[CoinFamily(CoinFamily.BitcoinBlake2b)]
public class BitcoinBlake2bPool : BitcoinPool
{
    public BitcoinBlake2bPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf, IStatsRepository statsRepo, IMapper mapper,
        IMasterClock clock, IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm, NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus,
            rmsm, nicehashService)
    {
    }

    private int jobPipelineFailed;

    protected override Task OnSubmitAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        // Check raw tokens before BitcoinPool's ParamsAs<string[]> can coerce
        // numbers or Booleans into apparently valid hexadecimal strings.
        try
        {
            ValidateSubmissionParameters(request.Value.Params);
        }
        catch(StratumException)
        {
            // Preserve the ordinary invalid-share/ban boundary even though
            // strict token validation precedes the base conversion path.
            var context = connection.ContextAs<BitcoinWorkerContext>();
            context.Stats.InvalidShares++;
            ConsiderBan(connection, context, poolConfig.Banning);
            throw;
        }
        return base.OnSubmitAsync(connection, request, ct);
    }

    internal static void ValidateSubmissionParameters(object parameters)
    {
        var valid = parameters switch
        {
            JArray array => array.Count == 5 && array.All(x => x.Type == JTokenType.String),
            object[] array => array.Length == 5 && array.All(x => x is string),
            _ => false,
        };
        if(!valid)
            throw new StratumException(StratumError.Other,
                "Bitcoin BLAKE2b mining.submit requires exactly five JSON strings");
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<BitcoinBlake2bJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider),
                new BitcoinBlake2bExtraNonceProvider()));
        manager.Configure(poolConfig, clusterConfig);
        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() => OnNewJobAsync(job)))
                .Concat()
                .Subscribe(_ => { }, HandleBlake2bPipelineFailure));
            await manager.Jobs.Take(1).ToTask(ct);
        }
        else
            disposables.Add(manager.Jobs.Subscribe(_ => { }, HandleBlake2bPipelineFailure));
    }

    private void HandleBlake2bPipelineFailure(Exception ex)
    {
        if(Interlocked.Exchange(ref jobPipelineFailed, 1) != 0)
            return;

        logger.Fatal(ex, "Bitcoin BLAKE2b work contract failed; invalidating work and stopping Miningcore");
        ctx.Resolve<IMiningFailStopCoordinator>().BeginFailStopAndCapture(
            ProcessExitCodes.GeneralFailure, () =>
            {
                foreach(var connection in connections.Values.ToArray())
                {
                    connection.ContextAs<BitcoinWorkerContext>().ClearJobs();
                    Disconnect(connection);
                }
                return true;
            });
    }

    protected override async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;
        logger.Info(() => $"Broadcasting job {((object[]) jobParams)[0]}");
        async Task BroadcastAsync() => await ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();
            if(!context.IsSubscribed)
                return;
            // Unlike Bitcoin SV1, the assigned target is also inside notify.
            // Apply VarDiff before taking the immutable job/target snapshot.
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty,
                    new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                CreateWorkerJob(connection, (bool) ((object[]) jobParams)[^1]));
        });
        await Guard(BroadcastAsync);
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        var previousDifficulty = context.Difficulty;
        await base.OnRequestAsync(connection, request, ct);
        if(context.Difficulty == previousDifficulty)
            return;

        try
        {
            ((BitcoinBlake2bJobManager) manager).ValidateWorkerDifficulty(context.Difficulty);
        }
        catch(ArgumentOutOfRangeException)
        {
            // A miner's malformed difficulty request must not terminate the
            // shared job pipeline or leave an impossible target installed.
            context.SetDifficulty(previousDifficulty);
            context.ClearJobs();
            Disconnect(connection);
            return;
        }

        if(context.IsSubscribed)
        {
            // Subscribe already sends both messages (including a NiceHash
            // override). Custodial authorize sends difficulty only, so it
            // still needs the fresh BLAKE2b job below.
            if(request.Value.Method == BitcoinStratumMethods.Subscribe)
                return;
            // Unlike authorize/suggest-difficulty, the inherited BIP310
            // handler changes context state without announcing difficulty.
            if(request.Value.Method == BitcoinStratumMethods.MiningConfigure)
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty,
                    new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                CreateWorkerJob(connection, false));
        }
    }

    protected override object CreateWorkerJob(StratumConnection connection,
        bool cleanJob)
    {
        if(Volatile.Read(ref jobPipelineFailed) != 0)
            throw new StratumException(StratumError.JobNotFound,
                "Bitcoin BLAKE2b work has been invalidated");
        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(manager.GetJobForStratum() is not BitcoinBlake2bJob job)
            throw new StratumException(StratumError.JobNotFound,
                "Bitcoin BLAKE2b job is unavailable");

        BitcoinBlake2bJob workerJob;
        try
        {
            var target = ((BitcoinBlake2bJobManager) manager).ValidateWorkerDifficulty(context.Difficulty);
            workerJob = job.ForDifficulty(context.Difficulty, target);
        }
        catch(ArgumentOutOfRangeException ex)
        {
            throw new StratumException(StratumError.Other, ex.Message);
        }
        context.AddJob(workerJob, manager.maxActiveJobs);
        return workerJob.GetJobParams(cleanJob);
    }
}
