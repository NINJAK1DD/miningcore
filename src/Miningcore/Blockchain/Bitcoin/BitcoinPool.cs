using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Bitcoin;

[CoinFamily(CoinFamily.Bitcoin)]
public class BitcoinPool : PoolBase
{
    public BitcoinPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    protected object currentJobParams;
    protected BitcoinJobManager manager;
    private BitcoinTemplate coin;
    private int directJobPipelineFailed;
    internal bool DirectJobPipelineFailed =>
        Volatile.Read(ref directJobPipelineFailed) != 0;

    internal enum VersionRollingNegotiationStatus
    {
        Enabled,
        TemplateDisabled,
        InvalidMinerMask,
        DisjointMask,
    }

    internal readonly record struct VersionRollingNegotiation(
        VersionRollingNegotiationStatus Status, uint? Mask);

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        var data = new object[]
        {
            new object[]
            {
                new object[] { BitcoinStratumMethods.SetDifficulty, connection.ConnectionId },
                new object[] { BitcoinStratumMethods.MiningNotify, connection.ConnectionId }
            }
        }
        .Concat(manager.GetSubscriberData(connection))
        .ToArray();

        // Nicehash's stupid validator insists on "error" property present
        // in successful responses which is a violation of the JSON-RPC spec
        // [Respect the goddamn standards Nicehack :(]
        var response = new JsonRpcResponse<object[]>(data, request.Id);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        await connection.RespondAsync(response);

        // setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams.FirstOrDefault()?.Trim();

        // Nicehash support
        var nicehashDiff = await GetNicehashStaticMinDiff(context, coin.Name, coin.GetAlgorithmName());

        if(nicehashDiff.HasValue)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(nicehashDiff.Value);
        }

        // send initial update. Direct-SOLO work is withheld until the username
        // address has passed network-aware authorization.
        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        if(!manager.DirectCoinbasePayoutEnabled || context.IsAuthorized)
        {
            var minerJobParams = CreateWorkerJob(connection,
                context.IsSubscribed);
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                minerJobParams);
        }
    }

    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that minerName is an address
        context.IsAuthorized = await ValidateWorkerAsync(context, minerName, password, ct);
        context.Miner = minerName;
        context.Worker = workerName;

        if(context.IsAuthorized)
        {
            // Nicehash's stupid validator insists on "error" property present
            // in successful responses which is a violation of the JSON-RPC spec
            // [Respect the goddamn standards Nicehack :(]
            var response = new JsonRpcResponse<object>(context.IsAuthorized, request.Id);

            if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
            {
                response.Extra = new Dictionary<string, object>();
                response.Extra["error"] = null;
            }

            // respond
            await connection.RespondAsync(response);

            context.SessionId = WorkerSessionTracker.GetOrCreateSessionId(
                poolConfig.Id,
                context.Miner,
                context.Worker,
                connection.RemoteEndpoint?.Address?.ToString(),
                DateTime.UtcNow);

            // log association
            logger.Info(() => manager.DirectCoinbasePayoutEnabled
                ? $"[{connection.ConnectionId}] Authorized direct-SOLO worker (payout destination retained in immutable job audit state)"
                : $"[{connection.ConnectionId}] Authorized worker {workerValue}");

            // extract control vars from password
            var staticDiff = GetStaticDiffFromPassparts(passParts);

            // Static diff
            if(staticDiff.HasValue &&
               (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
            {
                context.VarDiff = null; // disable vardiff
                context.SetDifficulty(staticDiff.Value);

                logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            }

            if(manager.DirectCoinbasePayoutEnabled && context.IsSubscribed)
            {
                var minerJobParams = CreateWorkerJob(connection, true);
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                    minerJobParams);
            }
        }

        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                logger.Info(() => manager.DirectCoinbasePayoutEnabled
                    ? $"[{connection.ConnectionId}] Banning unauthorized direct-SOLO worker for {loginFailureBanTimeout.TotalSeconds} sec"
                    : $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);

                Disconnect(connection);
            }
        }
    }

    protected virtual Task<bool> ValidateWorkerAsync(BitcoinWorkerContext context,
        string minerName, string password, CancellationToken ct)
    {
        if(!manager.DirectCoinbasePayoutEnabled)
            return manager.ValidateAddressAsync(minerName, ct);

        return ValidateDirectWorkerAsync(context, minerName, ct);
    }

    private async Task<bool> ValidateDirectWorkerAsync(
        BitcoinWorkerContext context, string minerName, CancellationToken ct)
    {
        var destination = await manager.ValidateDirectPayoutAddressAsync(
            minerName, ct);
        if(destination == null)
            return false;

        await context.SetDirectPayoutAuthorizationAsync(minerName,
            destination, ct);

        return true;
    }

    private object CreateWorkerJob(StratumConnection connection, bool cleanJob)
    {
        if(manager.DirectCoinbasePayoutEnabled &&
           Volatile.Read(ref directJobPipelineFailed) != 0)
            throw new InvalidOperationException(
                "Direct SOLO job delivery has entered fail-stop state");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(manager.DirectCoinbasePayoutEnabled)
        {
            // Building a per-worker coinbase can overlap reauthorization. Publish
            // only a job built for the authorization generation that is still
            // current at insertion time; otherwise rebuild from the new snapshot.
            for(var attempt = 0; attempt < 2; attempt++)
            {
                var authorization = context.GetDirectPayoutAuthorization() ??
                    throw new InvalidOperationException(
                        "Direct SOLO worker has no payout authorization");
                var directJob = manager.GetDirectJobForStratum(
                    authorization.Address, authorization.Destination,
                    authorization.Generation);

                if(context.TryAddDirectJob(directJob,
                       manager.maxActiveJobs))
                    return directJob.GetJobParams(cleanJob);
            }

            throw new InvalidOperationException(
                "Direct SOLO payout authorization changed while assigning work");
        }

        var job = manager.GetJobForStratum();
        context.AddJob(job, manager.maxActiveJobs);
        return job.GetJobParams(cleanJob);
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(manager.DirectCoinbasePayoutEnabled &&
               Volatile.Read(ref directJobPipelineFailed) != 0)
                throw new StratumException(StratumError.JobNotFound,
                    "Direct SOLO job delivery has entered fail-stop state");

            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if(requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // check worker state
            context.LastActivity = clock.Now;

            // validate worker
            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
            else if(!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "not subscribed");

            var requestParams = request.ParamsAs<string[]>();

            // Direct submission and successful reauthorization form one ordered
            // financial boundary. If submission entered first, reauthorization
            // cannot report success until that immutable job finishes. If
            // reauthorization entered first, the old generation cannot resolve.
            Miningcore.Blockchain.Share share;
            if(manager.DirectCoinbasePayoutEnabled)
            {
                await context.EnterDirectPayoutSubmissionAsync(ct);
                try
                {
                    share = await manager.SubmitShareAsync(connection,
                        requestParams, ct);
                }
                finally
                {
                    context.ExitDirectPayoutSubmission();
                }
            }
            else
                share = await manager.SubmitShareAsync(connection,
                    requestParams, ct);

            // Nicehash's stupid validator insists on "error" property present
            // in successful responses which is a violation of the JSON-RPC spec
            // [Respect the goddamn standards Nicehack :(]
            var response = new JsonRpcResponse<object>(true, request.Id);

            if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
            {
                response.Extra = new Dictionary<string, object>();
                response.Extra["error"] = null;
            }

            if(string.IsNullOrEmpty(context.SessionId))
            {
                context.SessionId = WorkerSessionTracker.GetOrCreateSessionId(
                    poolConfig.Id,
                    context.Miner,
                    context.Worker,
                    connection.RemoteEndpoint?.Address?.ToString(),
                    DateTime.UtcNow);
            }

            share.SessionId = context.SessionId;

            WorkerSessionTracker.Touch(
                share.PoolId,
                share.Miner,
                share.Worker,
                share.SessionId,
                share.IpAddress,
                DateTime.UtcNow);

            // Merged mining publishes its cleared statistical copy before starting the
            // independent parent/auxiliary submission paths. Other Bitcoin-family managers
            // publish here. In both cases the positive response is admitted only after the
            // statistical share entered the accounting pipeline.
            await PublishShareAndAcknowledgeAsync(share,
                () => connection.RespondAsync(response),
                ShouldPublishStatisticalShare(share));

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty * coin.ShareMultiplier, 3)}");

            // update pool stats
            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;

            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch(StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    internal static bool ShouldPublishStatisticalShare(Share share)
    {
        return share?.StatisticalRecordEmitted != true;
    }

    private async Task OnSuggestDifficultyAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        // Nicehash's stupid validator insists on "error" property present
        // in successful responses which is a violation of the JSON-RPC spec
        // [Respect the goddamn standards Nicehack :(]
        var response = new JsonRpcResponse<object>(true, request.Id);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        // acknowledge
        await connection.RespondAsync(response);

        try
        {
            var requestParams = request.ParamsAs<object[]>();
            var requestedDiff = (double) Convert.ChangeType(requestParams.FirstOrDefault()?.ToString().Trim(), typeof(double));

            // client may suggest higher-than-base difficulty, but not a lower one
            var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

            if(requestedDiff > poolEndpoint.Difficulty)
            {
                context.SetDifficulty(requestedDiff);
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

                logger.Info(() => $"[{connection.ConnectionId}] Difficulty set to {requestedDiff} as requested by miner");
            }
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"Unable to convert suggested difficulty {request.Params}");
        }
    }

    private async Task OnConfigureMiningAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        var requestParams = request.ParamsAs<JToken[]>();
        var extensions = requestParams[0].ToObject<string[]>();
        var extensionParams = requestParams[1].ToObject<Dictionary<string, JToken>>();
        var result = new Dictionary<string, object>();

        if(extensions != null)
        {
            foreach(var extension in extensions)
            {
                switch(extension)
                {
                    case BitcoinStratumExtensions.VersionRolling:
                        ConfigureVersionRolling(connection, context, extensionParams, result);
                        break;

                    case BitcoinStratumExtensions.MinimumDiff:
                        ConfigureMinimumDiff(connection, context, extensionParams, result);
                        break;
                }
            }
        }

        // Nicehash's stupid validator insists on "error" property present
        // in successful responses which is a violation of the JSON-RPC spec
        // [Respect the goddamn standards Nicehack :(]
        var response = new JsonRpcResponse<object>(result, request.Id);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        await connection.RespondAsync(response);
    }

    private void ConfigureVersionRolling(StratumConnection connection, BitcoinWorkerContext context,
        IReadOnlyDictionary<string, JToken> extensionParams, Dictionary<string, object> result)
    {
        var hasRequestedMask = extensionParams.TryGetValue(
            BitcoinStratumExtensions.VersionRollingMask, out var requestedMaskValue);
        var negotiation = NegotiateVersionRolling(poolConfig.Template,
            hasRequestedMask, requestedMaskValue);

        ApplyVersionRollingNegotiation(context, result, negotiation);

        if(negotiation.Status != VersionRollingNegotiationStatus.Enabled)
        {
            var reason = negotiation.Status switch
            {
                VersionRollingNegotiationStatus.TemplateDisabled =>
                    "disabled by the coin-template policy",
                VersionRollingNegotiationStatus.InvalidMinerMask =>
                    "declined because the miner supplied an invalid mask",
                VersionRollingNegotiationStatus.DisjointMask =>
                    "declined because the miner and pool masks are disjoint",
                _ => throw new InvalidOperationException(
                    $"Unhandled version-rolling status {negotiation.Status}"),
            };

            logger.Info(() => $"[{connection.ConnectionId}] Version rolling {reason} " +
                $"for {poolConfig.Template.Symbol}");
            return;
        }

        logger.Info(() => $"[{connection.ConnectionId}] Using version-rolling " +
            $"mask {result[BitcoinStratumExtensions.VersionRollingMask]}");
    }

    internal static void ApplyVersionRollingNegotiation(
        BitcoinWorkerContext context, IDictionary<string, object> result,
        VersionRollingNegotiation negotiation)
    {
        var enabled =
            negotiation.Status == VersionRollingNegotiationStatus.Enabled;

        if(enabled && !negotiation.Mask.HasValue)
        {
            throw new InvalidOperationException(
                "Enabled version rolling requires a negotiated mask");
        }

        context.VersionRollingMask = enabled ? negotiation.Mask : null;
        result[BitcoinStratumExtensions.VersionRolling] = enabled;
        result.Remove(BitcoinStratumExtensions.VersionRollingMask);

        if(enabled)
        {
            result[BitcoinStratumExtensions.VersionRollingMask] =
                negotiation.Mask.Value.ToStringHex8();
        }
    }

    internal static VersionRollingNegotiation NegotiateVersionRolling(
        CoinTemplate coin, bool hasRequestedMask, JToken requestedMaskValue)
    {
        if(coin is BitcoinTemplate {DisableVersionRolling: true})
        {
            return new VersionRollingNegotiation(
                VersionRollingNegotiationStatus.TemplateDisabled, null);
        }

        // BIP310 defines an unprefixed, case-insensitive eight-digit TMask.
        // Accept an optional 0x prefix defensively, but reject every other shape
        // without tearing down the miner connection.
        // An omitted miner mask means no miner-side narrowing. ResolveVersionRollingMask
        // still applies the template mask and the global BIP310 envelope.
        var requestedMask = uint.MaxValue;

        if(hasRequestedMask &&
           !TryParseRequestedVersionRollingMask(requestedMaskValue,
               out requestedMask))
        {
            return new VersionRollingNegotiation(
                VersionRollingNegotiationStatus.InvalidMinerMask, null);
        }

        // A merged pool evaluates its parent template here because rolling changes
        // only the parent header; the auxiliary header remains daemon-owned.
        var mask = ResolveVersionRollingMask(coin, requestedMask);

        return mask.HasValue
            ? new VersionRollingNegotiation(
                VersionRollingNegotiationStatus.Enabled, mask)
            : new VersionRollingNegotiation(
                VersionRollingNegotiationStatus.DisjointMask, null);
    }

    internal static bool TryParseRequestedVersionRollingMask(JToken value,
        out uint mask)
    {
        mask = 0;

        if(value?.Type != JTokenType.String)
            return false;

        var text = value.Value<string>();

        if(text?.Length == 10 && text[0] == '0' &&
           (text[1] == 'x' || text[1] == 'X'))
        {
            text = text[2..];
        }

        return text?.Length == 8 && uint.TryParse(text,
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
            out mask);
    }

    internal static uint? ResolveVersionRollingMask(CoinTemplate coin,
        uint requestedMask)
    {
        var poolMask = BitcoinConstants.VersionRollingPoolMask;

        if(coin is BitcoinTemplate bitcoinTemplate)
        {
            if(bitcoinTemplate.DisableVersionRolling)
                return null;

            poolMask = bitcoinTemplate.AllowedVersionRollingMask ?? poolMask;
        }

        var negotiatedMask = poolMask & requestedMask;

        return negotiatedMask == 0 ? null : negotiatedMask;
    }

    private void ConfigureMinimumDiff(StratumConnection connection, BitcoinWorkerContext context,
        IReadOnlyDictionary<string, JToken> extensionParams, Dictionary<string, object> result)
    {
        var requestedDiff = extensionParams[BitcoinStratumExtensions.MinimumDiffValue].Value<double>();

        // client may suggest higher-than-base difficulty, but not a lower one
        var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

        if(requestedDiff > poolEndpoint.Difficulty)
        {
            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(requestedDiff);

            logger.Info(() => $"[{connection.ConnectionId}] Difficulty set to {requestedDiff} as requested by miner. VarDiff now disabled.");

            // enabled
            result[BitcoinStratumExtensions.MinimumDiff] = true;
        }
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;

        logger.Info(() => $"Broadcasting job {((object[]) jobParams)[0]}");

        async Task BroadcastAsync() => await ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();
            if(manager.DirectCoinbasePayoutEnabled && !context.IsAuthorized)
                return;
            var minerJobParams = CreateWorkerJob(connection, (bool) ((object[]) jobParams)[^1]);

            // varDiff: if the client has a pending difficulty change, apply it now
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // send job
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
        });

        if(manager.DirectCoinbasePayoutEnabled)
            await BroadcastAsync();
        else
            await Guard(BroadcastAsync);
    }

    internal void HandleJobPipelineFailure(Exception ex)
    {
        if(!manager.DirectCoinbasePayoutEnabled)
        {
            logger.Debug(ex, nameof(OnNewJobAsync));
            return;
        }

        if(Interlocked.Exchange(ref directJobPipelineFailed, 1) != 0)
            return;

        logger.Fatal(ex,
            "Direct SOLO job delivery failed after startup. Invalidating all work and stopping Miningcore to prevent mining against an unverifiable coinbase contract");

        Guard(() => messageBus.SendMessage(new AdminNotification(
            "Bitcoin direct-SOLO job delivery stopped",
            $"Pool {poolConfig.Id} invalidated all jobs and is stopping because a direct-coinbase template could not be constructed safely: {ex.Message}")));

        void InvalidateWork()
        {
            foreach(var connection in connections.Values.ToArray())
            {
                try
                {
                    connection.ContextAs<BitcoinWorkerContext>().ClearJobs();
                    Disconnect(connection);
                }
                catch(Exception invalidateError)
                {
                    logger.Error(invalidateError,
                        "Failed to invalidate a direct SOLO worker during fail-stop");
                }
            }
        }

        var failStop = ctx.ResolveOptional<IMiningFailStopCoordinator>();
        if(failStop != null)
        {
            failStop.BeginFailStopAndCapture(ProcessExitCodes.GeneralFailure,
                () =>
                {
                    InvalidateWork();
                    return true;
                });
        }
        else
            InvalidateWork();
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var multiplier = BitcoinConstants.Pow2x32;
        var result = shares * multiplier / interval;

        if(coin.HashrateMultiplier.HasValue)
            result *= coin.HashrateMultiplier.Value;

        return result;
    }

    public override double ShareMultiplier => coin.ShareMultiplier;

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        if(UsesUnauditedDefaultVersionRolling(coin))
        {
            logger.Warn(() => $"Pool '{pc.Id}' coin template '{pc.Coin}' " +
                $"({coin.Symbol}) uses " +
                $"the compatibility BIP310 mask " +
                $"0x{BitcoinConstants.VersionRollingPoolMask:x8} without a " +
                "source-reviewed version-rolling policy");
        }
    }

    internal static bool UsesUnauditedDefaultVersionRolling(
        BitcoinTemplate template) => !template.DisableVersionRolling &&
        !template.AllowedVersionRollingMask.HasValue &&
        !template.VersionRollingConsensusMask.HasValue;

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<BitcoinJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(job),
                        ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    HandleJobPipelineFailure(ex);
                }));

            // start with initial blocktemplate
            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new BitcoinWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch(request.Method)
            {
                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    await OnSuggestDifficultyAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.MiningConfigure:
                    await OnConfigureMiningAsync(connection, tsRequest);
                    // ignored
                    break;

                case BitcoinStratumMethods.ExtraNonceSubscribe:
                    var context = connection.ContextAs<BitcoinWorkerContext>();

                    // Nicehash's stupid validator insists on "error" property present
                    // in successful responses which is a violation of the JSON-RPC spec
                    // [Respect the goddamn standards Nicehack :(]
                    var response = new JsonRpcResponse<object>(true, request.Id);

                    if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
                    {
                        response.Extra = new Dictionary<string, object>();
                        response.Extra["error"] = null;
                    }

                    await connection.RespondAsync(response);
                    break;

                case BitcoinStratumMethods.GetTransactions:
                    // ignored
                    break;

                case BitcoinStratumMethods.MiningMultiVersion:
                    // ignored
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        if(connection.Context.ApplyPendingDifficulty())
        {
            var cleanJob = (bool) ((object[]) currentJobParams)[^1];
            if(cleanJob)
                cleanJob = !cleanJob;

            var minerJobParams = CreateWorkerJob(connection, cleanJob);

            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { connection.Context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
        }
    }

    #endregion // Overrides
}
