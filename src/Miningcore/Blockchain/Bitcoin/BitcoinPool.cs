using Miningcore.Rpc;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
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

    protected BitcoinJobManager manager;
    private async Task NotifyAssignedDifficultyAsync(StratumConnection connection)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        context.AnnouncedDifficulty = context.Difficulty;
    }
    private BitcoinTemplate coin;
    private int directJobPipelineFailed;
    private readonly ConditionalWeakTable<StratumConnection, PublicationFailureState> publicationFailures = new();

    private sealed class PublicationFailureState
    {
        private int reported;
        internal bool BeforeResponse;
        internal bool TryReport() => Interlocked.Exchange(ref reported, 1) == 0;
    }
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

    // Dispatch rejects duplicates before this extension hook. Overrides must also
    // preserve the core guard if they invoke subscription outside normal dispatch.
    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        if(await RejectDuplicateSubscribeAsync(connection, tsRequest.Value))
            return;
        var userAgent = ReadSubscribeUserAgent(tsRequest.Value);
        var lookupContext = new BitcoinWorkerContext { UserAgent = userAgent };
        var prepared = new PreparedSubscription(userAgent,
            await GetNicehashStaticMinDiff(lookupContext, coin.Name, coin.GetAlgorithmName(), ct));
        await RunTerminalAssignmentAsync(connection, () => OnSubscribeCoreAsync(connection, tsRequest, prepared), ct);
    }

    private const string DuplicateSubscribeError =
        "Already subscribed; another subscription attempt will close this connection";

    private async Task<bool> RejectDuplicateSubscribeAsync(StratumConnection connection, JsonRpcRequest request)
    {
        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(!context.IsSubscribed)
            return false;

        if(context.TryWarnDuplicateSubscribe())
        {
            await connection.RespondErrorAsync(StratumError.Other, DuplicateSubscribeError, request.Id, false);
            GuardPublicationCleanup(connection, () => PublishTelemetry(
                TelemetryCategory.StratumAdmission, "duplicate-subscribe-warning", TimeSpan.Zero));
        }
        else
            CloseDuplicateSubscription(connection);
        return true;
    }

    protected virtual void CloseDuplicateSubscription(StratumConnection connection) =>
        CloseStratumAdmission(connection, StratumDiagnostics.Event.DuplicateSubscription, "duplicate-subscribe");

    private protected void CloseStratumAdmission(StratumConnection connection, StratumDiagnostics.Event reason, string outcome)
    {
        // Stop buffered dispatch and concurrent job insertion before observers.
        connection.TryBeginDisconnect();
        GuardPublicationCleanup(connection, () => connection.ContextAs<BitcoinWorkerContext>().CloseJobs());
        GuardPublicationCleanup(connection, () => Disconnect(connection));
        GuardPublicationCleanup(connection, () => StratumDiagnostics.Write(logger,
            NLog.LogLevel.Info, reason, connection.ConnectionId));
        GuardPublicationCleanup(connection, () => PublishTelemetry(
            TelemetryCategory.StratumAdmission, outcome, TimeSpan.Zero));
    }

    // Carry the exact parsed user agent and completed lookup together, including
    // null values, so lookup identity cannot diverge from committed worker state.
    protected readonly record struct PreparedSubscription(string UserAgent, double? NicehashDifficulty);

    protected static string ReadSubscribeUserAgent(JsonRpcRequest request) =>
        request.ParamsAs<string[]>()?.FirstOrDefault()?.Trim();

    protected async Task OnSubscribeCoreAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
        PreparedSubscription preparedSubscription)
    {
        var request = tsRequest.Value;

        // Reject before parsing parameters, allocating another extranonce,
        // consulting NiceHash or publishing work. Existing jobs and direct
        // payout authorization remain usable after one stray duplicate.
        if(await RejectDuplicateSubscribeAsync(connection, request))
            return;
        var context = connection.ContextAs<BitcoinWorkerContext>();
        var userAgent = preparedSubscription.UserAgent;

        if(preparedSubscription.NicehashDifficulty is { } preparedDifficulty)
            context.ValidateRequestedDifficulty(preparedDifficulty);

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
        context.UserAgent = userAgent;

        // Nicehash support
        var nicehashDiff = preparedSubscription.NicehashDifficulty;

        if(nicehashDiff.HasValue)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(nicehashDiff.Value);
        }

        // send initial update. Direct-SOLO work is withheld until the username
        // address has passed network-aware authorization.
        await NotifyAssignedDifficultyAsync(connection);
        if(!manager.DirectCoinbasePayoutEnabled || context.IsAuthorized)
        {
            var minerJobParams = CreateWorkerJob(connection,
                context.IsSubscribed);
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                minerJobParams);
        }
    }

    protected virtual Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct) =>
        OnAuthorizeCoreAsync(connection, tsRequest, ct);

    // A present wrapper also represents a parsed password with no static difficulty.
    protected readonly record struct ParsedStaticDifficulty(double? Value);

    protected async Task OnAuthorizeCoreAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
        CancellationToken ct, ParsedStaticDifficulty? parsedDifficulty = null, string[] parsedParameters = null)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = parsedParameters ?? request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var staticDiff = parsedDifficulty.HasValue ? parsedDifficulty.Value.Value :
            GetStaticDiffFromPassparts(password?.Split(PasswordControlVarsSeparator));
        if(ShouldApplyStaticDifficulty(context, staticDiff) && !IsValidDifficulty(context, staticDiff.Value))
        {
            await connection.RespondErrorAsync(StratumError.Other, "Difficulty is outside the supported range", request.Id, false);
            return;
        }

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
                : $"[{connection.ConnectionId}] Authorized worker (identity withheld)");

            // extract control vars from password
            await ApplyStaticDifficultyAsync(connection, staticDiff, ct);

            if(manager.DirectCoinbasePayoutEnabled && context.IsSubscribed)
            {
                await RunTerminalAssignmentAsync(connection, async () =>
                {
                    var minerJobParams = CreateWorkerJob(connection, true);
                    await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify,
                        minerJobParams);
                }, ct, responseAlreadyStarted: true);
            }
        }

        else
        {
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Authorization failed", request.Id, context.IsAuthorized);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                // issue short-time ban if unauthorized to prevent DDos on daemon (validateaddress RPC)
                if(BanClient(connection, loginFailureBanTimeout))
                    logger.Info(() => manager.DirectCoinbasePayoutEnabled
                    ? $"[{connection.ConnectionId}] Banning unauthorized direct-SOLO worker for {loginFailureBanTimeout.TotalSeconds} sec"
                    : $"[{connection.ConnectionId}] Banning unauthorized worker (identity withheld) for {loginFailureBanTimeout.TotalSeconds} sec");

                Disconnect(connection);
            }
        }
    }

    protected static bool ShouldApplyStaticDifficulty(BitcoinWorkerContext context, double? difficulty) =>
        difficulty.HasValue &&
        (context.VarDiff != null && difficulty.Value >= context.VarDiff.Config.MinDiff ||
            context.VarDiff == null && difficulty.Value > context.Difficulty);

    // Address validation and identity updates finish before this assignment-only hook.
    protected virtual Task ApplyStaticDifficultyAsync(StratumConnection connection,
        double? staticDiff, CancellationToken ct) =>
        RunTerminalAssignmentAsync(connection, () => ApplyStaticDifficultyCoreAsync(connection, staticDiff, ct), ct, responseAlreadyStarted: true);

    // BLAKE2b invokes the same mutation inside its own gated failure boundary.
    protected async Task ApplyStaticDifficultyCoreAsync(StratumConnection connection,
        double? staticDiff, CancellationToken ct)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        // Static diff
        if(ShouldApplyStaticDifficulty(context, staticDiff))
        {
            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(staticDiff.Value);

            logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

            await NotifyAssignedDifficultyAsync(connection);
            // Direct SOLO publishes after the payout authorization is committed.
            if(!manager.DirectCoinbasePayoutEnabled)
                await PublishReplacementAssignmentAsync(connection);
        }
    }

    protected virtual async Task PublishReplacementAssignmentAsync(StratumConnection connection)
    {
        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(context.IsSubscribed && (!manager.DirectCoinbasePayoutEnabled || context.IsAuthorized))
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, CreateWorkerJob(connection, false));
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

    protected virtual object CreateWorkerJob(StratumConnection connection,
        bool cleanJob)
    {
        if(connection.IsDisconnectRequested)
            throw new StratumConnectionClosedException();

        if(manager.DirectCoinbasePayoutEnabled &&
           Volatile.Read(ref directJobPipelineFailed) != 0)
            throw new StratumException(StratumError.JobNotFound,
                "Direct SOLO job delivery has entered fail-stop state");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        if(manager.DirectCoinbasePayoutEnabled)
        {
            // Building a per-worker coinbase can overlap reauthorization. Publish
            // only a job built for the authorization generation that is still
            // current at insertion time; otherwise rebuild from the new snapshot.
            for(var attempt = 0; attempt < 2; attempt++)
            {
                if(connection.IsDisconnectRequested)
                    throw new StratumConnectionClosedException();

                var authorization = context.GetDirectPayoutAuthorization() ??
                    throw new StratumException(StratumError.JobNotFound,
                        "Direct SOLO worker has no payout authorization");
                var directJob = manager.GetDirectJobForStratum(
                    authorization.Address, authorization.Destination,
                    authorization.Generation).ForWorker(context, () => manager.NextWorkerJobId());

                if(context.TryAddDirectJob(directJob,
                       manager.maxActiveJobs))
                    return directJob.GetJobParams(cleanJob);
            }

            throw new StratumException(StratumError.JobNotFound,
                "Direct SOLO payout authorization changed while assigning work");
        }

        var job = (BitcoinJob) manager.GetJobForStratum().ForWorker(context, () => manager.NextWorkerJobId());
        context.AddJob(job, manager.maxActiveJobs);
        return job.GetJobParams(cleanJob);
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        var acceptedProofSequence = context.AcceptedProofSequence;
        Share share;
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
        }

        catch(StratumException ex) when(context.AcceptedProofSequence == acceptedProofSequence)
        {
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);
            context.Stats.InvalidShares++;
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Info, "BitcoinPool.OnSubmitAsync", failure: ex, connectionId: connection.ConnectionId);
            ConsiderBan(connection, context, poolConfig.Banning);
            throw;
        }

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
            ShouldPublishStatisticalShare(share), onAdmitted: () =>
            {
                // Persistence ownership, not successful wire delivery, is the
                // acceptance milestone. Count it once even if acknowledgement fails.
                context.Stats.ValidShares++;
                if(share.IsBlockCandidate)
                    poolStats.LastPoolBlockTime = clock.Now;

                // Observability must not veto acknowledgement of an admitted share.
                GuardPublicationCleanup(connection, () =>
                    PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true));
                GuardPublicationCleanup(connection, () =>
                    logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty * coin.ShareMultiplier, 3)}"));
            });

        // Work publication after acceptance is not proof validation. Its failure
        // must not count the accepted share again as invalid or feed miner bans.
        await UpdateVarDiffAsync(connection, false, ct);
    }

    internal static bool ShouldPublishStatisticalShare(Share share)
    {
        return share?.StatisticalRecordEmitted != true;
    }

    // A present wrapper with no value is an already parsed no-op, not a request
    // to parse again. BLAKE2b validates and executes the same converted value.
    protected readonly record struct ParsedSuggestedDifficulty(double? Value);

    protected double? ReadSuggestedDifficulty(JsonRpcRequest request)
    {
        try
        {
            var requestParams = request.ParamsAs<object[]>();
            var value = requestParams.FirstOrDefault();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            // Wire numerics are always invariant. Boxed JSON numbers need no text round-trip.
            if(value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return Convert.ToDouble(value, culture);
            return double.TryParse(value?.ToString().Trim(), System.Globalization.NumberStyles.Float, culture, out var parsed) ? parsed : null;
        }
        catch(Exception ex)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinPool.ReadSuggestedDifficulty", failure: ex);
            return null;
        }
    }

    protected async Task OnSuggestDifficultyAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
        ParsedSuggestedDifficulty? parsedDifficulty = null)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        var requestedDiff = parsedDifficulty.HasValue ? parsedDifficulty.Value.Value : ReadSuggestedDifficulty(request);
        if(requestedDiff is { } invalid && (invalid < context.MinimumDifficulty ||
            (!parsedDifficulty.HasValue || invalid > poolConfig.Ports[connection.LocalEndpoint.Port].Difficulty) &&
            !IsValidDifficulty(context, invalid)))
        {
            await connection.RespondErrorAsync(StratumError.Other, "Difficulty is outside the supported range or negotiated minimum", request.Id, false);
            return;
        }
        if(requestedDiff is { } nonpositive && (!double.IsFinite(nonpositive) || nonpositive <= 0))
            requestedDiff = null;
        if(!requestedDiff.HasValue && !parsedDifficulty.HasValue)
        {
            await connection.RespondErrorAsync(StratumError.Other, "Invalid difficulty", request.Id, false);
            return;
        }

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

        // client may suggest higher-than-base difficulty, but not a lower one
        var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

        // BLAKE2b supplies its validated value and retains its separate legacy
        // notification contract; generic no-ops publish nothing beyond the ack.
        if(requestedDiff > poolEndpoint.Difficulty && (parsedDifficulty.HasValue || requestedDiff != context.Difficulty))
        {
            context.SetDifficulty(requestedDiff.Value);
            await NotifyAssignedDifficultyAsync(connection);
            await PublishReplacementAssignmentAsync(connection);

            logger.Info(() => $"[{connection.ConnectionId}] Difficulty set to {requestedDiff} as requested by miner");
        }
    }

    protected virtual async Task OnConfigureMiningAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest,
        double? validatedMinimumDifficulty = null)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        if(request.Id == null)
        {
            await connection.RespondErrorAsync(StratumError.MinusOne, "missing request id", null, false);
            return;
        }
        // Validate the envelope before conversion or any negotiation mutation.
        // Keep ignored trailing parameters compatible with existing firmware.
        // A response-parameter name cannot also be an extension result key.
        if(!BitcoinConfigureRequest.TryParse(request.Params, out var extensionList, out var options))
        {
            await connection.RespondErrorAsync(StratumError.Other, "Invalid request parameters", request.Id, false);
            return;
        }
        var previousDifficulty = context.Difficulty;
        var extensions = extensionList.Values<string>().Distinct(StringComparer.Ordinal);
        var extensionParams = options.ToObject<Dictionary<string, JToken>>();
        var result = new Dictionary<string, object>();

        foreach(var extension in extensions)
        {
            switch(extension)
            {
                case BitcoinStratumExtensions.VersionRolling:
                    ConfigureVersionRolling(connection, context, extensionParams, result);
                    break;

                case BitcoinStratumExtensions.MinimumDiff:
                    ConfigureMinimumDiff(connection, context, extensionParams, result, validatedMinimumDifficulty);
                    break;

                default:
                    // BIP310 requires a result for every requested extension.
                    result[extension] = false;
                    break;
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
        if(context.Difficulty != previousDifficulty)
            await NotifyConfiguredAssignmentAsync(connection);
    }

    protected virtual async Task NotifyConfiguredAssignmentAsync(StratumConnection connection)
    {
        if(!connection.Context.IsSubscribed)
            return;
        await NotifyAssignedDifficultyAsync(connection);
        await PublishReplacementAssignmentAsync(connection);
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
        IReadOnlyDictionary<string, JToken> extensionParams, Dictionary<string, object> result, double? validatedMinimumDifficulty)
    {
        result[BitcoinStratumExtensions.MinimumDiff] = "Minimum difficulty must be finite, positive and within the supported range; disabling requires reconnecting";
        var requestedDiff = validatedMinimumDifficulty;
        if(!requestedDiff.HasValue)
        {
            if(!extensionParams.TryGetValue(BitcoinStratumExtensions.MinimumDiffValue, out var value) ||
                !TryReadMinimumDifficulty(value, out var parsed))
                return;
            requestedDiff = parsed;
        }

        if(!IsValidDifficulty(context, requestedDiff.Value))
            return;

        // client may suggest higher-than-base difficulty, but not a lower one
        var poolEndpoint = poolConfig.Ports[connection.LocalEndpoint.Port];

        if(requestedDiff >= poolEndpoint.Difficulty)
        {
            context.SetMinimumDifficulty(requestedDiff.Value);

            logger.Info(() => $"[{connection.ConnectionId}] Difficulty set to {requestedDiff} as requested by miner. VarDiff now disabled.");

            // enabled
            result[BitcoinStratumExtensions.MinimumDiff] = true;
        }
        else
            result[BitcoinStratumExtensions.MinimumDiff] = "Minimum difficulty is below the configured port difficulty";
    }

    private static bool IsValidDifficulty(BitcoinWorkerContext context, double difficulty)
    {
        try { context.ValidateRequestedDifficulty(difficulty); return true; }
        catch(ArgumentOutOfRangeException) { return false; }
    }

    protected static bool TryReadMinimumDifficulty(JToken value, out double difficulty)
    {
        difficulty = 0;
        if(value?.Type is not (JTokenType.Integer or JTokenType.Float or JTokenType.String))
            return false;
        // Preserve numeric-string firmware compatibility without culture coercion,
        // Boolean conversion, thousands separators or nonfinite targets.
        var text = value.Type == JTokenType.String ? value.Value<string>() : value.ToString(Formatting.None);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out difficulty) &&
            double.IsFinite(difficulty) && difficulty > 0;
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        logger.Info(() => $"Broadcasting job {((object[]) jobParams)[0]}");

        async Task BroadcastAsync() => await ForEachMinerAssignmentAsync(async (connection, ct) =>
        {
            if(connection.IsDisconnectRequested)
                return;
            var context = connection.ContextAs<BitcoinWorkerContext>();
            if(manager.DirectCoinbasePayoutEnabled && !context.IsAuthorized)
                return;
            try
            {
                // varDiff: if the client has a pending difficulty change, apply it now
                if(context.ApplyPendingDifficulty() ||
                    context.AnnouncedDifficulty is { } announcedDifficulty && announcedDifficulty != context.Difficulty)
                    await NotifyAssignedDifficultyAsync(connection);

                var minerJobParams = CreateWorkerJob(connection, (bool) ((object[]) jobParams)[^1]);

                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
            }
            catch(OperationCanceledException ex) when(ex is StratumConnectionClosedException or BitcoinJobRegistryClosedException)
            {
                // A concurrent disconnect owns this cancellation. Finish cleanup
                // without promoting ordinary connection churn to a broadcast error.
                CloseRequestPublicationFailure(connection, ex, reportFailure: false);
            }
            catch(Exception ex)
            {
                CloseRequestPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && ct.IsCancellationRequested));
                throw;
            }
        });

        await Guard(BroadcastAsync);
    }

    internal void HandleJobPipelineFailure(Exception ex)
    {
        if(!manager.DirectCoinbasePayoutEnabled)
        {
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Debug, "BitcoinPool.HandleJobPipelineFailure", failure: ex);
            return;
        }

        if(Interlocked.Exchange(ref directJobPipelineFailed, 1) != 0)
            return;

        RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Fatal, "BitcoinPool.HandleJobPipelineFailure", failure: ex);
        logger.Fatal("Invalidating all work and stopping Miningcore because direct-SOLO job construction failed. Operator investigation and restart are required.");

        Guard(() => messageBus.SendMessage(new AdminNotification(
            "Bitcoin direct-SOLO job delivery stopped",
            $"Pool {poolConfig.Id} invalidated all jobs and is stopping because a direct-coinbase template could not be constructed safely. Operator investigation and restart are required. {RpcConsumerDiagnostics.WithheldError}")));

        void InvalidateWork()
        {
            foreach(var connection in connections.Values.ToArray())
            {
                try
                {
                    connection.ContextAs<BitcoinWorkerContext>().CloseJobs();
                }
                catch(Exception invalidateError)
                {
                    RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "BitcoinPool.HandleJobPipelineFailure", failure: invalidateError);
                }
                finally { GuardPublicationCleanup(connection, () => Disconnect(connection)); }
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

        manager.DirectJobConstructionFailed += HandleJobPipelineFailure;

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(job),
                        ex=> RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Debug, "BitcoinPool.SetupJobManager", failure: ex))))
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
        var context = new BitcoinWorkerContext();
        ConfigureWorkerDifficultyContext(context);
        return context;
    }

    protected virtual void ConfigureWorkerDifficultyContext(BitcoinWorkerContext context) =>
        context.ConfigureDifficultyPolicy(ShareMultiplier);

    protected override double MaximumVarDiff => Math.Min(double.MaxValue, (double) BitcoinConstants.Diff1 * ShareMultiplier);

    protected Task RunTerminalAssignmentAsync(StratumConnection connection, Func<Task> assignment, CancellationToken ct,
        bool responseAlreadyStarted = false) =>
        RunAssignmentAsync(connection, async () =>
        {
            var responseSequence = connection.ResponseSequence;
            try { await assignment(); }
            catch(Exception ex)
            {
                publicationFailures.GetValue(connection, static _ => new()).BeforeResponse =
                    !responseAlreadyStarted && connection.ResponseSequence == responseSequence;
                CloseRequestPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && ct.IsCancellationRequested));
                throw;
            }
        }, ct);

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        if(!connection.IsDisconnectRequested && tsRequest.Value.Id == null &&
            tsRequest.Value.Method is BitcoinStratumMethods.MiningConfigure or BitcoinStratumMethods.SuggestDifficulty or
                BitcoinStratumMethods.Authorize or BitcoinStratumMethods.Subscribe)
        {
            try { await connection.RespondErrorAsync(StratumError.MinusOne, "missing request id", null, false); }
            catch(Exception ex)
            {
                CloseRequestPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && ct.IsCancellationRequested));
                throw;
            }
            return;
        }
        if(!connection.IsDisconnectRequested && tsRequest.Value.Method == BitcoinStratumMethods.MiningConfigure &&
            !BitcoinConfigureRequest.TryParse(tsRequest.Value.Params, out _, out _))
        {
            await RunTerminalAssignmentAsync(connection, () => connection.RespondErrorAsync(StratumError.Other,
                "Invalid request parameters", tsRequest.Value.Id, false), ct);
            return;
        }
        if(!await AdmitDifficultyRequestAsync(connection, tsRequest.Value))
            return;

        if(connection.IsDisconnectRequested)
        {
            Guard(() => Disconnect(connection));
            return;
        }

        var request = tsRequest.Value;
        var responseSequence = connection.ResponseSequence;
        BitcoinWorkerContext workerContext = null;
        long acceptedProofSequence = 0;

        try
        {
            // A downstream context factory must preserve this dispatch contract.
            // Treat missing/incompatible context as terminal before any handler runs.
            workerContext = connection.ContextAs<BitcoinWorkerContext>() ??
                throw new InvalidOperationException("Bitcoin worker context is required");
            acceptedProofSequence = workerContext.AcceptedProofSequence;
            switch(request.Method)
            {
                case BitcoinStratumMethods.Subscribe:
                    if(!await RejectDuplicateSubscribeAsync(connection, request))
                        await OnSubscribeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    await RunTerminalAssignmentAsync(connection, () => OnSuggestDifficultyAsync(connection, tsRequest), ct);
                    break;

                case BitcoinStratumMethods.MiningConfigure:
                    await RunTerminalAssignmentAsync(connection, () => OnConfigureMiningAsync(connection, tsRequest), ct);
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
                    RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Debug, "BitcoinPool.OnRequestAsync");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(Exception ex) when(workerContext == null || connection.ResponseSequence != responseSequence ||
            workerContext.AcceptedProofSequence != acceptedProofSequence)
        {
            // A response attempt owns this request even if enqueue failed. Never
            // follow it with an error or keep a partially published session alive.
            // A validated proof likewise cannot become a rejection because later
            // accounting/candidate work failed, even before any response attempt.
            CloseRequestPublicationFailure(connection, ex,
                !(ex is OperationCanceledException && ct.IsCancellationRequested));
            if(ex is not StratumException)
                throw;
        }

        catch(StratumException ex)
        {
            await OnRequestErrorAsync(connection, request, ex, false, ct);
        }
    }

    protected override Task RespondDifficultyBudgetRefusalAsync(StratumConnection connection, JsonRpcRequest request)
    {
        if(request.Method != BitcoinStratumMethods.MiningConfigure ||
            !BitcoinConfigureRequest.TryParse(request.Params, out var extensions, out _))
            return base.RespondDifficultyBudgetRefusalAsync(connection, request);
        return RunTerminalAssignmentAsync(connection, () =>
        {
            var result = new Dictionary<string, object>();
            foreach(var name in extensions.Values<string>().Distinct(StringComparer.Ordinal))
                result[name] = name is BitcoinStratumExtensions.MinimumDiff or BitcoinStratumExtensions.VersionRolling
                    ? "Too many difficulty requests; retry later" : false;
            var response = new JsonRpcResponse<object>(result, request.Id);
            if(connection.ContextAs<BitcoinWorkerContext>().IsNicehash || poolConfig.EnableAsicBoost == true)
                response.Extra = new Dictionary<string, object> { ["error"] = null };
            return connection.RespondAsync(response);
        }, CancellationToken.None);
    }

    protected virtual async Task OnRequestErrorAsync(StratumConnection connection, JsonRpcRequest request,
        StratumException error, bool responseStarted, CancellationToken ct)
    {
        if(responseStarted || connection.IsDisconnectRequested)
        {
            // An intentional ban before any response needs terminal cleanup, but
            // must leave the report allowance available for a concrete failure.
            CloseRequestPublicationFailure(connection, error, reportFailure: responseStarted);
            return;
        }

        try
        {
            await connection.RespondErrorAsync(error.Code, error.Message, request.Id, false);
        }
        catch(Exception ex)
        {
            // The recovery response can itself fail to enqueue. It is still the
            // sole attempt for this request; there is no safe second error reply.
            CloseRequestPublicationFailure(connection, ex,
                !(ex is OperationCanceledException && ct.IsCancellationRequested));
            throw;
        }
    }

    protected virtual void CloseRequestPublicationFailure(StratumConnection connection, Exception failure,
        bool reportFailure = true)
    {
        reportFailure &= failure is not (StratumConnectionClosedException or BitcoinJobRegistryClosedException) &&
            !connection.IsMiningFailStopCancellation(failure);
        connection.TryBeginDisconnect();
        try
        {
            GuardPublicationCleanup(connection, () =>
                connection.ContextAs<BitcoinWorkerContext>().CloseJobs());
            if(reportFailure)
                GuardPublicationCleanup(connection, () => ReportPublicationFailureOnce(connection, failure));
        }
        finally { GuardPublicationCleanup(connection, () => Disconnect(connection)); }
    }

    private void ReportPublicationFailureOnce(StratumConnection connection, Exception failure)
    {
        var state = publicationFailures.GetValue(connection, static _ => new());
        if(!state.TryReport())
            return;

        GuardPublicationCleanup(connection, () =>
            StratumDiagnostics.Write(logger, NLog.LogLevel.Info,
                state.BeforeResponse ? StratumDiagnostics.Event.AssignmentMutationFailure :
                    StratumDiagnostics.Event.AssignmentPublicationFailure, connection.ConnectionId, failure: failure));
        GuardPublicationCleanup(connection, () =>
            PublishTelemetry(TelemetryCategory.StratumAdmission, "publication-failure", TimeSpan.Zero));
    }

    protected void GuardPublicationCleanup(StratumConnection connection, Action cleanup)
    {
        Guard(cleanup, error => Guard(() => StratumDiagnostics.Write(logger, NLog.LogLevel.Debug,
            StratumDiagnostics.Event.PublicationCleanupFailure, connection.ConnectionId, error)));
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        if(connection.IsDisconnectRequested)
            return;

        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        if(connection.Context.ApplyPendingDifficulty())
        {
            try
            {
                // A difficulty change preserves work from the current block.
                var minerJobParams = CreateWorkerJob(connection, false);

                await NotifyAssignedDifficultyAsync(connection);
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
            }
            catch(OperationCanceledException ex) when(ex is StratumConnectionClosedException or BitcoinJobRegistryClosedException)
            {
                // Construction can finish after a concurrent ban closed jobs.
                // This is terminal cleanup, not an idle VarDiff failure.
                CloseRequestPublicationFailure(connection, ex, reportFailure: false);
            }
            catch(Exception ex)
            {
                // Idle VarDiff has no request boundary. A committed difficulty
                // without its work notification is terminal there as well.
                CloseRequestPublicationFailure(connection, ex,
                    !(ex is OperationCanceledException && ct.IsCancellationRequested));
                throw;
            }
        }
    }

    #endregion // Overrides
}
