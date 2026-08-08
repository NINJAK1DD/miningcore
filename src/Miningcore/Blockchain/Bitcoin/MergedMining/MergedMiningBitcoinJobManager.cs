using System.Diagnostics;
using System.Globalization;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal enum AuxiliaryTemplateChange
{
    None,
    Template,
    ChainTip,
}

internal enum AuxiliaryAddressValidation
{
    Valid,
    Invalid,
    Unavailable,
}

internal enum AuxiliarySubmissionResult
{
    Accepted,
    Rejected,
    Ambiguous,
}

internal enum AuxiliaryBlockLookupResult
{
    Accepted,
    LostToDifferentProof,
    MissingProof,
    Orphaned,
    Unavailable,
}

internal enum ParentBlockLookupResult
{
    Accepted,
    MissingCoinbase,
    KnownInactive,
    Unavailable,
}

internal sealed record AuxiliaryTemplateRpcResult(
    RpcResponse<AuxBlockTemplate> Response,
    AuxiliaryTemplateRpcOutcome Outcome,
    TimeSpan Timeout);

public class MergedMiningBitcoinJobManager : BitcoinJobManager
{
    public MergedMiningBitcoinJobManager(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider,
        IBlockCandidateRecorder blockCandidateRecorder) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
        Contract.RequiresNonNull(blockCandidateRecorder);
        this.blockCandidateRecorder = blockCandidateRecorder;
    }

    private const string CreateAuxBlock = "createauxblock";
    private const string SubmitAuxBlock = "submitauxblock";
    private static readonly TimeSpan DefaultAuxiliaryTemplatePollTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AuxiliaryStartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AuxiliaryAddressValidationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BlockSubmissionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AmbiguousSubmissionLookupTimeout = TimeSpan.FromSeconds(5);
    private const int ValidatedAuxiliaryAddressCacheCapacity = 4096;

    private MergedMiningConfig mergedMiningConfig;
    private PoolConfig auxiliaryPoolConfig;
    private BitcoinTemplate parentCoin;
    private BitcoinTemplate auxiliaryCoin;
    private RpcClient auxiliaryRpc;
    private bool auxiliaryTemplateDegraded;
    private string startupAuxiliaryTemplateFallbackFailure;
    internal AuxBlockTemplate StartupAuxiliaryTemplate { get; private set; }
    private readonly IBlockCandidateRecorder blockCandidateRecorder;
    private readonly AuxiliaryAddressValidationCache validatedAuxiliaryAddresses =
        new(ValidatedAuxiliaryAddressCacheCapacity);
    private readonly object candidateOperationsLock = new();
    private readonly Dictionary<long, TaskCompletionSource<bool>> candidateOperations = new();
    private long nextCandidateOperationId;
    private int candidatePreparations;
    private bool candidateOperationsQuiescing;
    private TaskCompletionSource<bool> candidateQuiescence;

    private bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;

    protected override bool PollJobsWithBlockTemplateStream => MergedMiningEnabled;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        parentCoin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);

        mergedMiningConfig = MergedMiningConfigLoader.GetNormalizedConfig(pc);
        if(!MergedMiningEnabled)
            return;

        if(!string.Equals(parentCoin.Symbol, "LTC", StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Merged mining currently requires Litecoin as the parent chain", pc.Id);

        if(pc.PaymentProcessing?.Enabled != true || pc.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO)
            throw new PoolStartupException("Merged mining requires enabled SOLO payment processing on the parent pool", pc.Id);

        if(string.IsNullOrWhiteSpace(mergedMiningConfig.AuxPoolId))
            throw new PoolStartupException("mergedMining.auxPoolId is required", pc.Id);

        auxiliaryPoolConfig = cc.Pools.FirstOrDefault(x =>
            string.Equals(x.Id, mergedMiningConfig.AuxPoolId, StringComparison.OrdinalIgnoreCase));

        if(auxiliaryPoolConfig == null)
            throw new PoolStartupException($"Auxiliary pool '{mergedMiningConfig.AuxPoolId}' was not found", pc.Id);

        if(string.Equals(auxiliaryPoolConfig.Id, pc.Id, StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Parent and auxiliary pool ids must be different", pc.Id);

        if(auxiliaryPoolConfig.Enabled != true)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must be enabled", pc.Id);

        auxiliaryCoin = auxiliaryPoolConfig.Template as BitcoinTemplate;
        if(auxiliaryCoin == null)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must use the Bitcoin coin family", pc.Id);

        if(!string.Equals(auxiliaryCoin.Symbol, "DOGE", StringComparison.OrdinalIgnoreCase))
            throw new PoolStartupException("Merged mining currently requires Dogecoin as the auxiliary chain", pc.Id);

        if(auxiliaryPoolConfig.PaymentProcessing?.Enabled != true ||
            auxiliaryPoolConfig.PaymentProcessing.PayoutScheme != PayoutScheme.SOLO)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' must use enabled SOLO payment processing", pc.Id);

        if(string.IsNullOrWhiteSpace(auxiliaryPoolConfig.Address))
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' requires a pool wallet address", pc.Id);

        if(auxiliaryPoolConfig.Daemons == null || auxiliaryPoolConfig.Daemons.Length == 0)
            throw new PoolStartupException($"Auxiliary pool '{auxiliaryPoolConfig.Id}' requires a daemon endpoint", pc.Id);

        if(auxiliaryPoolConfig.Daemons.Length > 1)
            logger.Warn(() => $"Auxiliary pool '{auxiliaryPoolConfig.Id}' has {auxiliaryPoolConfig.Daemons.Length} daemon endpoints; merged mining uses only the first endpoint");

        if(!MergedMiningPasswordParser.IsValidAddressParameter(mergedMiningConfig.AddressParameter))
            throw new PoolStartupException(
                "mergedMining.addressParameter must not be 'd' or contain ';' or '='",
                pc.Id);

        if(!mergedMiningConfig.RequireAuxAddress)
            logger.Warn(() => "Merged mining allows workers without a DOGE address; their auxiliary candidates will not be submitted because no SOLO beneficiary can be attributed");

        // Parent ZMQ and Bitcoin Template Stream notifications do not report auxiliary-chain
        // tip changes. Polling guarantees that a fresh Dogecoin template is broadcast independently.
        if(pc.BlockRefreshInterval <= 0)
            pc.BlockRefreshInterval = 1000;

        var serializerSettings = ctx.Resolve<JsonSerializerSettings>();
        auxiliaryRpc = new RpcClient(auxiliaryPoolConfig.Daemons.First(), serializerSettings,
            messageBus, auxiliaryPoolConfig.Id);
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        await base.EnsureDaemonsSynchedAsync(ct);

        if(!MergedMiningEnabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var notificationShown = false;

        do
        {
            var request = await GetAuxBlockTemplateAsync(ct, AuxiliaryStartupTimeout,
                AuxiliaryTemplateRpcPhase.Startup);
            ct.ThrowIfCancellationRequested();

            var freshTemplate = request.Outcome == AuxiliaryTemplateRpcOutcome.Success
                ? request.Response?.Response
                : null;
            if(freshTemplate != null)
            {
                CacheStartupAuxiliaryTemplate(freshTemplate);
                // A parseable daemon response is not yet a usable mining template.
                // Keep availability false until UpdateJob successfully constructs and
                // installs the first merged-mining job from it.
                _ = SetAndPublishAuxiliaryTemplateState(false, false);
                logger.Info(() => $"Auxiliary daemon for {auxiliaryCoin.Name} is synched");
                return;
            }

            _ = SetAndPublishAuxiliaryTemplateState(false, false);

            if(!notificationShown)
            {
                logger.Info(() => $"Auxiliary daemon for {auxiliaryCoin.Name} is still syncing");
                notificationShown = true;
            }

            logger.Debug(() => $"Auxiliary daemon reports: " +
                DescribeAuxiliaryTemplateRpcFailure(request));
        } while(await timer.WaitForNextTickAsync(ct));
    }

    private async Task<AuxiliaryTemplateRpcResult> GetAuxBlockTemplateAsync(CancellationToken ct,
        TimeSpan timeout, AuxiliaryTemplateRpcPhase phase)
    {
        // Do not create an RPC attempt after host shutdown has already won. In-flight
        // requests still use the independent token below so completion, deadline, and
        // caller cancellation can be arbitrated exactly once.
        ct.ThrowIfCancellationRequested();

        using var requestCts = new CancellationTokenSource();
        using var deadlineCts = new CancellationTokenSource();
        var callerCancellation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellationRegistration = ct.Register(() =>
            callerCancellation.TrySetResult(true));
        var stopwatch = Stopwatch.StartNew();

        var rpcTask = auxiliaryRpc.ExecuteAsync<AuxBlockTemplate>(logger,
            CreateAuxBlock, requestCts.Token, new[] { auxiliaryPoolConfig.Address });
        var callerCancellationTask = callerCancellation.Task;
        var deadlineTask = Task.Delay(timeout, deadlineCts.Token);

        // Let the RPC task and both cancellation sources compete once. If the RPC wins,
        // its outcome is fixed here and a deadline firing before the continuation resumes
        // cannot retroactively reclassify the completed response as a timeout.
        var completedTask = await Task.WhenAny(rpcTask, callerCancellationTask,
            deadlineTask);
        var callerCancellationWon = completedTask == callerCancellationTask;
        var deadlineWon = completedTask == deadlineTask;

        if(completedTask != rpcTask)
            await requestCts.CancelAsync();

        var response = await rpcTask;
        stopwatch.Stop();
        await deadlineCts.CancelAsync();

        var outcome = ClassifyAuxiliaryTemplateRpcOutcome(response,
            callerCancellationWon, deadlineWon);
        messageBus.SendMessage(new AuxiliaryTemplateRpcTelemetryEvent(
            poolConfig.Id, auxiliaryPoolConfig.Id, phase, outcome,
            stopwatch.Elapsed));

        return new AuxiliaryTemplateRpcResult(response, outcome, timeout);
    }

    internal static AuxiliaryTemplateRpcOutcome ClassifyAuxiliaryTemplateRpcOutcome(
        RpcResponse<AuxBlockTemplate> response, bool callerCancellationWon,
        bool deadlineWon)
    {
        if(callerCancellationWon)
            return AuxiliaryTemplateRpcOutcome.Cancellation;

        if(deadlineWon)
            return AuxiliaryTemplateRpcOutcome.Timeout;

        if(response?.Error == null && response?.Response != null)
            return AuxiliaryTemplateRpcOutcome.Success;

        // RpcClient attaches JSON/protocol parsing failures as inner exceptions. The
        // endpoint replied but did not produce a usable RPC envelope, so keep those
        // distinct from connection and other transport failures.
        if(response?.Error?.InnerException is Newtonsoft.Json.JsonException)
            return AuxiliaryTemplateRpcOutcome.RpcError;

        // RpcClient preserves client-side cancellation and transport failures as
        // inner exceptions. Daemon-originated errors, even if their text happens to
        // be "Cancelled", remain RPC errors instead of being classified by message.
        if(response == null || response.Error?.InnerException != null ||
            response.Error == null)
            return AuxiliaryTemplateRpcOutcome.TransportFailure;

        return AuxiliaryTemplateRpcOutcome.RpcError;
    }

    internal static string DescribeAuxiliaryTemplateRpcFailure(
        AuxiliaryTemplateRpcResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            AuxiliaryTemplateRpcOutcome.Timeout =>
                $"timed out after {result.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)} ms",
            AuxiliaryTemplateRpcOutcome.Cancellation =>
                "cancelled by host shutdown",
            AuxiliaryTemplateRpcOutcome.RpcError =>
                result.Response?.Error?.Message ?? "RPC error",
            AuxiliaryTemplateRpcOutcome.TransportFailure =>
                result.Response?.Error?.Message ?? "transport failure",
            AuxiliaryTemplateRpcOutcome.Success => "completed successfully",
            _ => "unknown auxiliary-template RPC failure",
        };
    }

    private AuxiliaryTemplateStateTransition SetAndPublishAuxiliaryTemplateState(
        bool available, bool degraded)
    {
        var transition = new AuxiliaryTemplateStateTransition(
            FallbackStarted: degraded && !auxiliaryTemplateDegraded,
            Recovered: !degraded && auxiliaryTemplateDegraded);
        auxiliaryTemplateDegraded = degraded;

        // Gauges are level-triggered: reassert unchanged state so a transient
        // subscriber/metric update failure self-heals on the next refresh. Only the
        // FallbackStarted flag retains edge semantics for its counter.
        messageBus.SendMessage(new AuxiliaryTemplateStateTelemetryEvent(
            poolConfig.Id, auxiliaryPoolConfig.Id, available, degraded,
            transition.FallbackStarted));

        return transition;
    }

    internal static AuxiliaryTemplateChange ClassifyAuxiliaryTemplateChange(
        AuxBlockTemplate previous, AuxBlockTemplate current)
    {
        if(current == null)
            return AuxiliaryTemplateChange.None;

        if(previous == null || previous.Height != current.Height ||
            !string.Equals(previous.PreviousBlockhash, current.PreviousBlockhash,
                StringComparison.OrdinalIgnoreCase))
            return AuxiliaryTemplateChange.ChainTip;

        return !string.Equals(previous.Hash, current.Hash, StringComparison.OrdinalIgnoreCase)
            ? AuxiliaryTemplateChange.Template
            : AuxiliaryTemplateChange.None;
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct,
        bool forceUpdate, string via = null, string json = null)
    {
        if(!MergedMiningEnabled)
            return await base.UpdateJob(ct, forceUpdate, via, json);

        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            // BT-stream payloads are snapshots that may wait behind newer poll events. Treat every
            // merged-mining trigger as a refresh signal and fetch the daemon's latest parent tip.
            var parentResponse = await GetBlockTemplateAsync(ct);

            if(parentResponse.Error != null || parentResponse.Response == null)
            {
                var error = parentResponse.Error?.Message ?? "empty response";
                logger.Warn(() => $"Unable to update parent job: {error}");
                return (false, forceUpdate);
            }

            var previousJob = currentJob as MergedMiningBitcoinJob;
            var blockTemplate = parentResponse.Response;

            var previousAuxiliaryTemplate = previousJob?.AuxiliaryBlockTemplate ??
                StartupAuxiliaryTemplate;
            var shouldRefreshAuxiliaryTemplate = ShouldRefreshAuxiliaryTemplate(via,
                previousAuxiliaryTemplate != null);
            AuxBlockTemplate auxiliaryTemplate;
            var freshAuxiliaryTemplatePendingCommit = false;
            var cachedAuxiliaryTemplatePendingCommit = false;
            string cachedAuxiliaryTemplateFailure = null;

            if(!shouldRefreshAuxiliaryTemplate)
            {
                // Parent block-template stream events cannot prove that the auxiliary daemon has
                // recovered. Reuse the cached template without changing the degraded state.
                auxiliaryTemplate = previousAuxiliaryTemplate;
            }
            else
            {
                var auxiliaryTemplateTimeout = previousAuxiliaryTemplate == null
                    ? AuxiliaryStartupTimeout
                    : GetAuxiliaryTemplatePollTimeout();
                var auxiliaryRequest = await GetAuxBlockTemplateAsync(ct,
                    auxiliaryTemplateTimeout, AuxiliaryTemplateRpcPhase.Refresh);
                ct.ThrowIfCancellationRequested();

                var hasAuxiliaryTemplate = TryResolveAuxiliaryTemplate(
                    previousAuxiliaryTemplate, auxiliaryRequest,
                    out auxiliaryTemplate, out var usedCachedAuxiliaryTemplate);

                if(!hasAuxiliaryTemplate)
                {
                    var error = DescribeAuxiliaryTemplateRpcFailure(auxiliaryRequest);
                    // With no usable template there cannot be an earlier cached
                    // fallback episode; publish the level and deliberately ignore
                    // the transition result.
                    _ = SetAndPublishAuxiliaryTemplateState(false, false);
                    logger.Warn(() => $"Unable to create initial auxiliary job: {error}");
                    return (false, forceUpdate);
                }

                if(usedCachedAuxiliaryTemplate)
                {
                    var error = DescribeAuxiliaryTemplateRpcFailure(auxiliaryRequest);
                    if(previousJob?.AuxiliaryBlockTemplate != null)
                        PublishAuxiliaryTemplateFallback(auxiliaryTemplate, error);
                    else
                    {
                        // startupAuxiliaryTemplate has not yet powered an active job.
                        // Defer availability and the fallback episode until successful
                        // construction proves that the cached response is usable.
                        cachedAuxiliaryTemplatePendingCommit = true;
                        cachedAuxiliaryTemplateFailure = error;
                        startupAuxiliaryTemplateFallbackFailure = error;
                    }
                }
                else
                {
                    // Receiving a fresh response does not prove that it can produce a
                    // usable merged-mining job. Commit recovery only after the fresh
                    // template is installed, or after proving that the active job
                    // already contains the identical template.
                    freshAuxiliaryTemplatePendingCommit = true;

                    if(previousJob == null && StartupAuxiliaryTemplate != null &&
                        ClassifyAuxiliaryTemplateChange(StartupAuxiliaryTemplate,
                            auxiliaryTemplate) == AuxiliaryTemplateChange.None)
                    {
                        // A successful refresh has proven the startup identity current
                        // again. Do not retain older fallback provenance if this combined
                        // parent/auxiliary job attempt subsequently fails to initialize.
                        startupAuxiliaryTemplateFallbackFailure = null;
                    }
                }
            }

            var parentIsNew = IsNewParentTemplate(previousJob?.BlockTemplate, blockTemplate);

            var auxiliaryChange = ClassifyAuxiliaryTemplateChange(
                previousJob?.AuxiliaryBlockTemplate, auxiliaryTemplate);
            var auxiliaryIsNew = auxiliaryChange != AuxiliaryTemplateChange.None;
            var installsStartupAuxiliaryTemplate = previousJob == null &&
                ReferenceEquals(auxiliaryTemplate, StartupAuxiliaryTemplate);
            var freshTemplateDiffersFromStartup = previousJob == null &&
                freshAuxiliaryTemplatePendingCommit &&
                StartupAuxiliaryTemplate != null &&
                ClassifyAuxiliaryTemplateChange(StartupAuxiliaryTemplate,
                    auxiliaryTemplate) != AuxiliaryTemplateChange.None;

            if(parentIsNew)
                messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Height, poolConfig.Template);

            if(auxiliaryChange == AuxiliaryTemplateChange.ChainTip)
                messageBus.NotifyChainHeight(auxiliaryPoolConfig.Id, auxiliaryTemplate.Height,
                    auxiliaryPoolConfig.Template);

            if(parentIsNew || auxiliaryIsNew || forceUpdate)
            {
                MergedMiningBitcoinJob job;

                try
                {
                    job = CreateMergedMiningJob(blockTemplate, auxiliaryTemplate);
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    if(freshAuxiliaryTemplatePendingCommit &&
                        previousJob?.AuxiliaryBlockTemplate != null)
                    {
                        if(auxiliaryIsNew)
                        {
                            // A changed fresh template exists, but the only installed job
                            // still uses the previous identity. That is cached fallback even
                            // when the refresh RPC itself succeeded. Reasserting this state
                            // while already degraded does not start another episode.
                            PublishAuxiliaryTemplateFallback(
                                previousJob.AuxiliaryBlockTemplate,
                                "replacement job initialization failed");
                        }
                        else
                        {
                            // The fresh RPC reconfirmed the identity already installed in
                            // currentJob. An unrelated parent-job initialization failure must
                            // not keep that auxiliary identity marked degraded.
                            PublishAuxiliaryTemplateRecovery(
                                previousJob.AuxiliaryBlockTemplate);
                        }
                    }
                    else if((freshAuxiliaryTemplatePendingCommit ||
                        cachedAuxiliaryTemplatePendingCommit) &&
                        previousJob?.AuxiliaryBlockTemplate == null)
                    {
                        if(freshTemplateDiffersFromStartup)
                        {
                            // A newer fresh identity could not replace the uninstalled
                            // startup cache. If a parent-only stream event subsequently
                            // installs that older cache, it is fallback rather than healthy.
                            startupAuxiliaryTemplateFallbackFailure =
                                "fresh auxiliary template job initialization failed before the first job";
                        }

                        // With no active auxiliary job, advertise failed initialization
                        // as unavailable until a usable merged-mining job is installed.
                        _ = SetAndPublishAuxiliaryTemplateState(false, false);
                    }

                    throw;
                }

                if(parentIsNew)
                {
                    logger.Info(() => via != null
                        ? $"Detected new parent block {blockTemplate.Height} [{via}]"
                        : $"Detected new parent block {blockTemplate.Height}");

                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }
                else if(auxiliaryChange == AuxiliaryTemplateChange.ChainTip)
                {
                    logger.Info(() => $"Detected new auxiliary block {auxiliaryTemplate.Height} [{auxiliaryTemplate.Hash}]");
                }
                else if(auxiliaryChange == AuxiliaryTemplateChange.Template)
                {
                    logger.Debug(() => $"Detected auxiliary template update {auxiliaryTemplate.Height} [{auxiliaryTemplate.Hash}]");
                }
                else
                {
                    logger.Debug(() => via != null
                        ? $"Merged template update {blockTemplate.Height} [{via}]"
                        : $"Merged template update {blockTemplate.Height}");
                }

                currentJob = job;

                if(freshAuxiliaryTemplatePendingCommit)
                    PublishAuxiliaryTemplateRecovery(auxiliaryTemplate);
                else if(cachedAuxiliaryTemplatePendingCommit)
                    PublishAuxiliaryTemplateFallback(auxiliaryTemplate,
                        cachedAuxiliaryTemplateFailure);
                else if(installsStartupAuxiliaryTemplate)
                {
                    // A parent Template Stream update can install the first usable job
                    // without refreshing DOGE. This first commit proves the startup
                    // template usable; later parent-only updates still cannot prove
                    // recovery from an established degraded state.
                    if(startupAuxiliaryTemplateFallbackFailure != null)
                    {
                        PublishAuxiliaryTemplateFallback(auxiliaryTemplate,
                            startupAuxiliaryTemplateFallbackFailure);
                    }
                    else
                        PublishAuxiliaryTemplateRecovery(auxiliaryTemplate);
                }

                if(previousJob == null)
                    ClearStartupAuxiliaryTemplate();
            }
            else if(freshAuxiliaryTemplatePendingCommit)
            {
                // The freshly fetched identity is already installed in currentJob, so
                // no replacement is necessary and recovery can be committed immediately.
                PublishAuxiliaryTemplateRecovery(auxiliaryTemplate);
            }

            return (parentIsNew || auxiliaryIsNew, forceUpdate);
        }
        catch(OperationCanceledException)
        {
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    private void PublishAuxiliaryTemplateRecovery(AuxBlockTemplate auxiliaryTemplate)
    {
        var transition = SetAndPublishAuxiliaryTemplateState(true, false);
        if(transition.Recovered)
            logger.Info(() => $"Auxiliary template updates recovered at block {auxiliaryTemplate.Height} [{auxiliaryTemplate.Hash}]");
    }

    internal void CacheStartupAuxiliaryTemplate(AuxBlockTemplate auxiliaryTemplate)
    {
        StartupAuxiliaryTemplate = auxiliaryTemplate;
        startupAuxiliaryTemplateFallbackFailure = null;
    }

    private void ClearStartupAuxiliaryTemplate()
    {
        StartupAuxiliaryTemplate = null;
        startupAuxiliaryTemplateFallbackFailure = null;
    }

    private void PublishAuxiliaryTemplateFallback(AuxBlockTemplate auxiliaryTemplate,
        string error)
    {
        var transition = SetAndPublishAuxiliaryTemplateState(true, true);
        if(transition.FallbackStarted)
            logger.Warn(() => $"Auxiliary template update failed; continuing parent mining with cached auxiliary template {auxiliaryTemplate.Height} [{auxiliaryTemplate.Hash}]: {error}");
        else
            logger.Debug(() => $"Auxiliary template remains degraded: {error}");
    }

    protected virtual MergedMiningBitcoinJob CreateMergedMiningJob(BlockTemplate blockTemplate,
        AuxBlockTemplate auxiliaryTemplate)
    {
        var job = new MergedMiningBitcoinJob();
        job.InitMerged(blockTemplate, auxiliaryTemplate, NextJobId(), poolConfig,
            extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
            ShareMultiplier, parentCoin.CoinbaseHasherValue, parentCoin.HeaderHasherValue,
            !isPoS ? parentCoin.BlockHasherValue :
                parentCoin.PoSBlockHasherValue ?? parentCoin.BlockHasherValue);
        return job;
    }

    private TimeSpan GetAuxiliaryTemplatePollTimeout()
    {
        return mergedMiningConfig?.AuxiliaryTemplatePollTimeoutMs > 0
            ? TimeSpan.FromMilliseconds(mergedMiningConfig.AuxiliaryTemplatePollTimeoutMs)
            : DefaultAuxiliaryTemplatePollTimeout;
    }

    internal static bool IsNewParentTemplate(BlockTemplate previous, BlockTemplate current)
    {
        if(current == null)
            return false;

        if(previous == null)
            return true;

        return !string.Equals(previous.PreviousBlockhash, current.PreviousBlockhash,
                StringComparison.OrdinalIgnoreCase) ||
            current.Height > previous.Height;
    }

    internal static bool ShouldRefreshAuxiliaryTemplate(string via, bool hasCachedTemplate)
    {
        if(!hasCachedTemplate)
            return true;

        return via is not JobRefreshBy.BlockTemplateStream and
            not JobRefreshBy.BlockTemplateStreamRefresh;
    }

    internal static bool TryResolveAuxiliaryTemplate(AuxBlockTemplate previous,
        AuxiliaryTemplateRpcResult result, out AuxBlockTemplate template,
        out bool usedCached)
    {
        var freshTemplate = result?.Outcome == AuxiliaryTemplateRpcOutcome.Success
            ? result.Response?.Response
            : null;
        if(freshTemplate != null)
        {
            template = freshTemplate;
            usedCached = false;
            return true;
        }

        template = previous;
        usedCached = previous != null;
        return previous != null;
    }

    internal async Task<AuxiliaryAddressValidation> ValidateAuxiliaryAddressAsync(string address,
        CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return AuxiliaryAddressValidation.Valid;

        if(string.IsNullOrWhiteSpace(address))
            return mergedMiningConfig.RequireAuxAddress
                ? AuxiliaryAddressValidation.Invalid
                : AuxiliaryAddressValidation.Valid;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AuxiliaryAddressValidationTimeout);
        var result = await auxiliaryRpc.ExecuteAsync<ValidateAddressResponse>(logger,
            BitcoinCommands.ValidateAddress, cts.Token, new[] { address });
        var validation = ClassifyAuxiliaryAddressValidation(result);

        if(validation == AuxiliaryAddressValidation.Valid)
            validatedAuxiliaryAddresses.Add(address);
        else if(validation == AuxiliaryAddressValidation.Invalid)
            validatedAuxiliaryAddresses.Remove(address);
        else if(ResolveAuxiliaryAddressValidation(validation,
                    validatedAuxiliaryAddresses.Contains(address)) ==
                AuxiliaryAddressValidation.Valid)
        {
            logger.Debug(() => $"Reusing previously validated auxiliary payout address while '{auxiliaryPoolConfig.Id}' address validation is unavailable");
            validation = AuxiliaryAddressValidation.Valid;
        }

        return validation;
    }

    internal static AuxiliaryAddressValidation ResolveAuxiliaryAddressValidation(
        AuxiliaryAddressValidation validation, bool previouslyValidated)
    {
        return validation == AuxiliaryAddressValidation.Unavailable && previouslyValidated
            ? AuxiliaryAddressValidation.Valid
            : validation;
    }

    internal static AuxiliaryAddressValidation ClassifyAuxiliaryAddressValidation(
        RpcResponse<ValidateAddressResponse> result)
    {
        if(result?.Error != null || result?.Response == null)
            return AuxiliaryAddressValidation.Unavailable;

        return result.Response.IsValid
            ? AuxiliaryAddressValidation.Valid
            : AuxiliaryAddressValidation.Invalid;
    }

    public override async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission,
        CancellationToken ct)
    {
        if(!MergedMiningEnabled)
            return await base.SubmitShareAsync(worker, submission, ct);

        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams || submitParams.Length < 5)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<MergedMiningBitcoinWorkerContext>();
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue && submitParams.Length > 5
            ? submitParams[5] as string
            : null;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        MergedMiningBitcoinJob job;
        lock(context)
        {
            job = context.GetJob(jobId) as MergedMiningBitcoinJob;
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        using var candidatePreparation = BeginCandidatePreparation();
        var result = ProcessMergedShare(job, worker, extraNonce2, nTime, nonce, versionBits);
        var share = result.Share;

        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint?.Address?.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        var now = clock.Now;
        EnsureStatisticalShareSession(context, share, poolConfig.Id,
            share.IpAddress, now);
        share.Created = now;

        var hasCandidate = share.IsBlockCandidate || !string.IsNullOrEmpty(result.AuxPowHex);
        TaskCompletionSource<bool> candidateStart = null;
        Task<bool[]> candidateOperation = null;

        if(!hasCandidate)
        {
            // Validation is complete and this proof cannot produce a block. Shutdown does not
            // need to wait for the remaining ordinary statistical-share publication.
            candidatePreparation.Dispose();
        }
        else
        {
            // Reserve manager ownership synchronously as soon as proof validation identifies a
            // candidate. The start gate preserves statistical-share ordering without leaving a
            // host-shutdown gap before the operation becomes visible to the drain coordinator.
            candidateStart = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            candidateOperation = StartCandidateOperationAsync(operationToken =>
                SubmitCandidatePathsAsync(worker, context, result, share, operationToken),
                candidatePreparation, candidateStart.Task);
        }

        // The proof has passed all share validation. Publish a cleared statistical copy before
        // either daemon submission begins so a slow or failed peer-chain path cannot suppress
        // the ordinary share or move it past the parent block's effort boundary. BitcoinPool
        // observes the runtime-only guard and does not publish the returned object a second time.
        try
        {
            var statisticalShare = CreateStatisticalShare(share);
            messageBus.SendMessage(statisticalShare);
            share.SetPersistenceAdmission(statisticalShare.PersistenceAdmission);
            share.StatisticalRecordEmitted = true;
        }
        finally
        {
            // A synchronous telemetry subscriber failure must not strand a validated candidate.
            candidateStart?.TrySetResult(true);
        }

        if(candidateOperation != null)
            await candidateOperation;

        return share;
    }

    protected virtual MergedMiningShareResult ProcessMergedShare(
        MergedMiningBitcoinJob job, StratumConnection worker, string extraNonce2,
        string nTime, string nonce, string versionBits) =>
        job.ProcessShareMerged(worker, extraNonce2, nTime, nonce, versionBits);

    internal CandidatePreparationLease BeginCandidatePreparation()
    {
        lock(candidateOperationsLock)
        {
            if(candidateOperationsQuiescing)
                throw new OperationCanceledException(
                    "Merged-mining share processing is quiescing for shutdown");

            candidatePreparations++;
            return new CandidatePreparationLease(this);
        }
    }

    internal Task<bool[]> StartCandidateOperationAsync(
        Func<CancellationToken, Task<bool[]>> operation,
        CandidatePreparationLease preparation, Task startSignal = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(preparation);

        var operationId = Interlocked.Increment(ref nextCandidateOperationId);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock(candidateOperationsLock)
        {
            if(preparation.Owner != this || preparation.Completed)
                throw new InvalidOperationException(
                    "Candidate preparation lease is not active for this manager");

            // Candidate registration and preparation release are one atomic handoff. A shutdown
            // drain can therefore never observe both counters at zero between validation and
            // ownership transfer.
            candidateOperations.Add(operationId, completion);
            preparation.Completed = true;
            candidatePreparations--;
        }

        var task = ExecuteCandidateOperationAsync(operation, startSignal);
        _ = ObserveCandidateOperationAsync(operationId, completion, task);
        return task;
    }

    private static async Task<bool[]> ExecuteCandidateOperationAsync(
        Func<CancellationToken, Task<bool[]>> operation, Task startSignal)
    {
        if(startSignal != null)
            await startSignal;

        using var deadline = new CancellationTokenSource(BlockSubmissionTimeout);
        return await operation(deadline.Token);
    }

    protected virtual async Task<bool[]> SubmitCandidatePathsAsync(StratumConnection worker,
        MergedMiningBitcoinWorkerContext context, MergedMiningShareResult result,
        Share share, CancellationToken operationToken)
    {
        Task<bool> parentSubmission = null;
        Task<bool> auxiliarySubmission = null;

        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting parent block {share.BlockHeight} [{share.BlockHash}]");
            parentSubmission = SubmitAndPersistParentBlockAsync(share,
                result.ParentBlockHex, operationToken);
        }

        if(!string.IsNullOrEmpty(result.AuxPowHex))
            auxiliarySubmission = SubmitAuxiliaryBlockAsync(worker, context, result,
                operationToken);

        var blockFoundSignaled = 0;
        async Task<bool> TrackAcceptedSubmissionAsync(Task<bool> submission)
        {
            var accepted = await submission;
            if(accepted && Interlocked.CompareExchange(ref blockFoundSignaled, 1, 0) == 0)
                OnBlockFound();

            return accepted;
        }

        var submissions = new Task<bool>[] { parentSubmission, auxiliarySubmission }
            .Where(x => x != null)
            .Select(TrackAcceptedSubmissionAsync)
            .ToArray();

        // The internal deadline bounds daemon RPC and attribution only. Persistence
        // deliberately ignores miner and RPC cancellation and follows the database
        // retry/recovery-journal policy, so the complete drain can exceed ten seconds.
        // Drain both chains before propagating an error so an accepted peer result is
        // never left unobserved or unpersisted.
        return await DrainSubmissionTasksAsync(submissions);
    }

    private async Task ObserveCandidateOperationAsync(long operationId,
        TaskCompletionSource<bool> completion, Task operation)
    {
        try
        {
            await operation;
        }
        catch(Exception ex)
        {
            // The Stratum request path normally observes this exception too. The manager-level
            // observer is required for EOF races where DispatchAsync has already stopped
            // awaiting the request processor.
            logger.Error(ex, () => $"Merged-mining candidate operation {operationId} failed");
        }
        finally
        {
            lock(candidateOperationsLock)
            {
                candidateOperations.Remove(operationId);
                TryCompleteCandidateQuiescenceLocked();
            }

            completion.TrySetResult(true);
        }
    }

    public async Task DrainCandidateOperationsAsync()
    {
        Task quiescence;
        blockCandidateRecorder.BeginShutdown();

        lock(candidateOperationsLock)
        {
            candidateOperationsQuiescing = true;
            if(candidatePreparations == 0 && candidateOperations.Count == 0)
                return;

            candidateQuiescence ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            quiescence = candidateQuiescence.Task;
            logger.Info(() => $"Waiting for {candidatePreparations} merged-mining proof validation(s) and {candidateOperations.Count} candidate operation(s)");
        }

        await quiescence;
    }

    private void ReleaseCandidatePreparation(CandidatePreparationLease preparation)
    {
        lock(candidateOperationsLock)
        {
            if(preparation.Owner != this || preparation.Completed)
                return;

            preparation.Completed = true;
            candidatePreparations--;
            TryCompleteCandidateQuiescenceLocked();
        }
    }

    private void TryCompleteCandidateQuiescenceLocked()
    {
        if(candidateOperationsQuiescing && candidatePreparations == 0 &&
            candidateOperations.Count == 0)
            candidateQuiescence?.TrySetResult(true);
    }

    internal sealed class CandidatePreparationLease : IDisposable
    {
        internal CandidatePreparationLease(MergedMiningBitcoinJobManager owner)
        {
            Owner = owner;
        }

        internal MergedMiningBitcoinJobManager Owner { get; }
        internal bool Completed { get; set; }

        public void Dispose() => Owner.ReleaseCandidatePreparation(this);
    }

    internal async Task<bool> SubmitAndPersistParentBlockAsync(Share share,
        string blockHex, CancellationToken ct)
    {
        SubmitResult acceptResponse;

        try
        {
            acceptResponse = await SubmitParentBlockWithReconciliationAsync(share,
                blockHex, ct);
        }
        catch(StratumException)
        {
            throw;
        }
        catch(Exception ex)
        {
            // Local proof validation has already succeeded. A malformed/missing JSON-RPC batch,
            // transport exception or operation timeout cannot prove that litecoind rejected the
            // candidate, so persist an exact-hash marker for normal active-chain reconciliation.
            logger.Error(ex, () => $"Parent submission outcome for block {share.BlockHeight} [{share.BlockHash}] could not be classified; durable reconciliation queued");
            acceptResponse = new SubmitResult(false, null, true);
        }
        share.IsBlockCandidate = acceptResponse.Accepted;

        if(share.IsBlockCandidate)
        {
            share.BlockType = "merged-parent";
            share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
            logger.Info(() => $"Parent daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {share.Miner}");
        }
        else if(acceptResponse.Ambiguous)
        {
            share.IsBlockCandidate = true;
            share.BlockType = "merged-parent-uncertain";
            share.TransactionConfirmationData =
                AuxPowBlockConfirmation.CreateParentUncertain(share.BlockHash);
            logger.Warn(() => $"Parent submission outcome for block {share.BlockHeight} [{share.BlockHash}] is uncertain; durable reconciliation queued");
        }
        else
            share.TransactionConfirmationData = null;

        if(share.IsBlockCandidate)
        {
            await blockCandidateRecorder.PersistBlockCandidateAsync(
                CreateParentBlockOnlyShare(share));
            MarkParentBlockRecordEmitted(share);
        }

        return acceptResponse.Accepted;
    }

    internal static async Task<bool[]> DrainSubmissionTasksAsync(
        IEnumerable<Task<bool>> submissions)
    {
        ArgumentNullException.ThrowIfNull(submissions);

        var all = Task.WhenAll(submissions);

        try
        {
            // Task.WhenAll does not fail until every supplied task has reached a terminal state
            // and observes every task exception, which is the independent-chain drain contract.
            return await all;
        }

        catch
        {
            if(all.Exception?.InnerExceptions.Count > 1)
                throw new AggregateException(
                    "Multiple merged-mining submission paths failed",
                    all.Exception.InnerExceptions);

            throw;
        }
    }

    internal static void MarkParentBlockRecordEmitted(Share share)
    {
        ArgumentNullException.ThrowIfNull(share);

        // The durable block-only copy is now the sole candidate record. Return the original
        // proof as an ordinary statistical share so both current and pre-merged-mining relay
        // receivers cannot attempt a second block insert during a rolling deployment.
        share.BlockRecordEmitted = true;
        share.IsBlockCandidate = false;
        share.BlockType = null;
        share.TransactionConfirmationData = null;
    }

    internal static Share CreateStatisticalShare(Share share)
    {
        ArgumentNullException.ThrowIfNull(share);

        return new Share
        {
            PoolId = share.PoolId,
            Miner = share.Miner,
            Worker = share.Worker,
            UserAgent = share.UserAgent,
            IpAddress = share.IpAddress,
            Source = share.Source,
            Difficulty = share.Difficulty,
            ShareDifficulty = share.ShareDifficulty,
            ActualDifficulty = share.ActualDifficulty,
            SessionId = share.SessionId,
            PreserveCreated = share.IsBlockCandidate,
            BlockHeight = share.BlockHeight,
            BlockReward = share.BlockReward,
            BlockRewardDouble = share.BlockRewardDouble,
            NetworkDifficulty = share.NetworkDifficulty,
            Created = share.Created,
        };
    }

    internal static string EnsureStatisticalShareSession(
        MergedMiningBitcoinWorkerContext context, Share share, string poolId,
        string ipAddress, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(share);

        lock(context)
        {
            if(string.IsNullOrEmpty(context.SessionId))
            {
                context.SessionId = WorkerSessionTracker.GetOrCreateSessionId(
                    poolId, context.Miner, context.Worker, ipAddress, nowUtc);
            }

            share.SessionId = context.SessionId;
            return context.SessionId;
        }
    }

    private static Share CreateParentBlockOnlyShare(Share share)
    {
        return new Share
        {
            PoolId = share.PoolId,
            Miner = share.Miner,
            Worker = share.Worker,
            UserAgent = share.UserAgent,
            IpAddress = share.IpAddress,
            Source = share.Source,
            Difficulty = share.Difficulty,
            ShareDifficulty = share.ShareDifficulty,
            ActualDifficulty = share.ActualDifficulty,
            SessionId = share.SessionId,
            BlockOnly = true,
            BlockHeight = share.BlockHeight,
            BlockReward = share.BlockReward,
            BlockRewardDouble = share.BlockRewardDouble,
            BlockHash = share.BlockHash,
            BlockType = share.BlockType,
            IsBlockCandidate = true,
            TransactionConfirmationData = share.TransactionConfirmationData,
            NetworkDifficulty = share.NetworkDifficulty,
            Created = share.Created,
        };
    }

    protected virtual async Task<SubmitResult> SubmitParentBlockWithReconciliationAsync(Share share,
        string blockHex, CancellationToken ct)
    {
        var result = await SubmitBlockAsync(share, blockHex, ct, false);
        if(result.Duplicate && !string.IsNullOrEmpty(result.CoinbaseTx))
        {
            logger.Warn(() => $"Parent submission returned duplicate, but block {share.BlockHeight} [{share.BlockHash}] is active on the daemon");
            return new SubmitResult(true, result.CoinbaseTx);
        }

        if(result.Accepted || !result.Ambiguous)
            return result;

        using var cts = CreateAmbiguousLookupCancellationTokenSource(ct);
        var response = await rpc.ExecuteAsync<DaemonBlock>(logger, BitcoinCommands.GetBlock,
            cts.Token, new object[] { share.BlockHash });
        var lookupResult = ClassifyParentBlockLookup(share.BlockHash, response,
            out var coinbaseTransaction);

        if(lookupResult == ParentBlockLookupResult.Accepted)
        {
            logger.Warn(() => $"Parent submission response was ambiguous, but block {share.BlockHeight} [{share.BlockHash}] is present on the daemon");
            return new SubmitResult(true, coinbaseTransaction);
        }

        if(lookupResult == ParentBlockLookupResult.MissingCoinbase)
        {
            logger.Warn(() => $"Parent block {share.BlockHeight} [{share.BlockHash}] is active but its coinbase transaction is unavailable; durable reconciliation queued");
            return new SubmitResult(false, null, true, result.Duplicate);
        }

        if(lookupResult == ParentBlockLookupResult.KnownInactive)
        {
            logger.Warn(() => $"Parent submission response was ambiguous, but block {share.BlockHeight} [{share.BlockHash}] is known only outside the active chain");
            return new SubmitResult(false, null, false, result.Duplicate);
        }

        return result;
    }

    private async Task<bool> SubmitAuxiliaryBlockAsync(StratumConnection worker,
        MergedMiningBitcoinWorkerContext context, MergedMiningShareResult result, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(context.AuxiliaryMiner))
        {
            logger.Warn(() => $"Skipping auxiliary block {result.AuxiliaryBlockTemplate.Height}: worker supplied no auxiliary address");
            return false;
        }

        var template = result.AuxiliaryBlockTemplate;
        logger.Info(() => $"Submitting auxiliary block {template.Height} [{template.Hash}]");

        var submitResponse = await auxiliaryRpc.ExecuteAsync<JToken>(logger, SubmitAuxBlock, ct,
            new object[] { template.Hash, result.AuxPowHex });

        var submissionResult = ClassifyAuxiliarySubmissionResponse(submitResponse);
        var accepted = false;
        var uncertain = false;

        if(RequiresAuxiliaryProofLookup(submissionResult))
        {
            var lookupResult = await GetAuxiliaryBlockLookupResultAsync(template.Hash,
                result.ParentHeaderHex, ct);
            (accepted, uncertain) = ClassifyAuxiliarySubmissionOutcome(
                submissionResult, lookupResult);

            if(accepted)
                logger.Info(() => $"Auxiliary block {template.Height} [{template.Hash}] is active with the submitted parent proof");
            else if(lookupResult == AuxiliaryBlockLookupResult.LostToDifferentProof)
                logger.Warn(() => $"Auxiliary block {template.Height} [{template.Hash}] is active, but was accepted with a different parent proof");
            else if(lookupResult == AuxiliaryBlockLookupResult.Orphaned)
                logger.Warn(() => $"Auxiliary block {template.Height} [{template.Hash}] is known by the daemon but is not active");
        }

        if(!accepted && !uncertain)
        {
            var error = submitResponse.Error?.Message ?? submitResponse.Response?.ToString() ?? "rejected";
            logger.Warn(() => $"Auxiliary block {template.Height} [{template.Hash}] was not accepted: {error}");
            return false;
        }

        var auxiliaryShare = new Share
        {
            PoolId = auxiliaryPoolConfig.Id,
            Miner = context.AuxiliaryMiner,
            Worker = context.Worker,
            UserAgent = context.UserAgent,
            IpAddress = worker.RemoteEndpoint?.Address?.ToString(),
            Source = clusterConfig.ClusterName,
            Difficulty = result.Share.Difficulty,
            ShareDifficulty = result.Share.ShareDifficulty,
            ActualDifficulty = result.Share.ActualDifficulty,
            NetworkDifficulty = result.AuxiliaryDifficulty,
            BlockHeight = template.Height,
            BlockHash = template.Hash,
            BlockType = uncertain ? "auxpow-claim" : "auxpow",
            IsBlockCandidate = true,
            BlockOnly = true,
            // submitauxblock only confirms acceptance. Persist the accepted block immediately;
            // the payout handler resolves this marker to the coinbase txid via getblock.
            TransactionConfirmationData = uncertain
                ? AuxPowBlockConfirmation.CreateClaim(template.Hash, result.ParentHeaderHex)
                : AuxPowBlockConfirmation.CreatePending(template.Hash),
            SessionId = context.SessionId,
            Created = clock.Now,
        };

        await blockCandidateRecorder.PersistBlockCandidateAsync(auxiliaryShare);
        if(uncertain)
            logger.Warn(() => $"Auxiliary submission outcome for block {template.Height} [{template.Hash}] is uncertain; durable reconciliation queued for {context.AuxiliaryMiner}");
        else
            logger.Info(() => $"Auxiliary daemon accepted block {template.Height} [{template.Hash}] submitted by {context.AuxiliaryMiner}; coinbase reconciliation queued");

        return accepted;
    }

    internal static AuxiliarySubmissionResult ClassifyAuxiliarySubmissionResponse(
        RpcResponse<JToken> response)
    {
        if(response == null)
            return AuxiliarySubmissionResult.Ambiguous;

        if(response.Error != null)
            return response.Error.Code == -500
                ? AuxiliarySubmissionResult.Ambiguous
                : AuxiliarySubmissionResult.Rejected;

        if(response.Response?.Type != JTokenType.Boolean)
            return AuxiliarySubmissionResult.Ambiguous;

        return response.Response.Value<bool>()
            ? AuxiliarySubmissionResult.Accepted
            : AuxiliarySubmissionResult.Rejected;
    }

    internal static (bool Accepted, bool Uncertain) ClassifyAuxiliarySubmissionOutcome(
        AuxiliarySubmissionResult submissionResult, AuxiliaryBlockLookupResult lookupResult)
    {
        if(!RequiresAuxiliaryProofLookup(submissionResult))
            return (false, false);

        return lookupResult switch
        {
            AuxiliaryBlockLookupResult.Accepted => (true, false),
            AuxiliaryBlockLookupResult.Unavailable => (false, true),
            AuxiliaryBlockLookupResult.MissingProof => (false, true),
            AuxiliaryBlockLookupResult.LostToDifferentProof => (false, false),
            AuxiliaryBlockLookupResult.Orphaned => (false, false),
            _ => (false, false),
        };
    }

    internal static bool RequiresAuxiliaryProofLookup(
        AuxiliarySubmissionResult submissionResult)
    {
        return submissionResult is AuxiliarySubmissionResult.Accepted or
            AuxiliarySubmissionResult.Ambiguous;
    }

    private async Task<AuxiliaryBlockLookupResult> GetAuxiliaryBlockLookupResultAsync(
        string blockHash, string parentHeaderHex, CancellationToken ct)
    {
        using var cts = CreateAmbiguousLookupCancellationTokenSource(ct);
        var response = await auxiliaryRpc.ExecuteAsync<DaemonBlock>(logger,
            BitcoinCommands.GetBlock, cts.Token, new object[] { blockHash });

        return ClassifyAuxiliaryBlockLookup(blockHash, parentHeaderHex, response);
    }

    internal static CancellationTokenSource CreateAmbiguousLookupCancellationTokenSource(
        CancellationToken operationToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        result.CancelAfter(AmbiguousSubmissionLookupTimeout);
        return result;
    }

    internal static AuxiliaryBlockLookupResult ClassifyAuxiliaryBlockLookup(
        string blockHash, string parentHeaderHex, RpcResponse<DaemonBlock> response)
    {
        if(response?.Error != null || response.Response == null ||
            !string.Equals(response.Response.Hash, blockHash, StringComparison.OrdinalIgnoreCase))
            return AuxiliaryBlockLookupResult.Unavailable;

        if(response.Response.Confirmations < 0)
            return AuxiliaryBlockLookupResult.Orphaned;

        if(response.Response.Confirmations == 0)
            return AuxiliaryBlockLookupResult.Unavailable;

        var acceptedParentBlock = response.Response.AuxPow?.ParentBlock;
        if(string.IsNullOrWhiteSpace(acceptedParentBlock))
            return AuxiliaryBlockLookupResult.MissingProof;

        return string.Equals(acceptedParentBlock, parentHeaderHex,
            StringComparison.OrdinalIgnoreCase)
            ? AuxiliaryBlockLookupResult.Accepted
            : AuxiliaryBlockLookupResult.LostToDifferentProof;
    }

    internal static ParentBlockLookupResult ClassifyParentBlockLookup(
        string blockHash, RpcResponse<DaemonBlock> response, out string coinbaseTransaction)
    {
        coinbaseTransaction = null;

        if(response?.Error != null || response.Response == null ||
            !string.Equals(response.Response.Hash, blockHash, StringComparison.OrdinalIgnoreCase))
            return ParentBlockLookupResult.Unavailable;

        if(response.Response.Confirmations < 0)
            return ParentBlockLookupResult.KnownInactive;

        if(response.Response.Confirmations == 0)
            return ParentBlockLookupResult.Unavailable;

        coinbaseTransaction = response.Response.Transactions?.FirstOrDefault();
        return string.IsNullOrEmpty(coinbaseTransaction)
            ? ParentBlockLookupResult.MissingCoinbase
            : ParentBlockLookupResult.Accepted;
    }

    private readonly record struct AuxiliaryTemplateStateTransition(
        bool FallbackStarted,
        bool Recovered);
}
