using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.Numerics;
using System.Reactive;
using System.Security.Cryptography;
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
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Blockchain.Pearl;

[CoinFamily(CoinFamily.Pearl)]
public sealed class PearlPool(IComponentContext ctx, JsonSerializerSettings settings, IConnectionFactory cf,
    IStatsRepository stats, IMapper mapper, IMasterClock clock, IMessageBus bus,
    RecyclableMemoryStreamManager streams, NicehashService nicehash) :
    PoolBase(ctx, settings, cf, stats, mapper, clock, bus, streams, nicehash)
{
    private static readonly SemaphoreSlim decodeSlots = new(2, 2);
    private PearlPoolConfig config;
    private PearlNetwork network;
    private PearlJobManager manager;
    private PearlProofVerifier verifier;
    private PearlCandidateStore candidates;
    private Task background;
    private readonly PearlMetrics metrics = PearlMetrics.Default;
    private long lastHealthLog = Stopwatch.GetTimestamp();
    private protected override StratumInputPolicy InputPolicy => StratumInputPolicy.Pearl;
    protected override WorkerContextBase CreateWorkerContext() => new PearlWorkerContext(VarDiffTimeProvider);
    public override double HashrateFromShares(double shares, double interval) => interval > 0 ? shares * PearlWork.HashesPerDifficulty / interval : 0;
    protected override void NotifyPoolOnline() {UpdateStats(); base.NotifyPoolOnline();}

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        config = poolConfig.Extra.SafeExtensionDataAs<PearlPoolConfig>();
        network = config.Validate(poolConfig);
        verifier = new PearlProofVerifier(config.ProofWorker);
        candidates = new PearlCandidateStore(cf, shareRepo);
        if(!await cf.Run(con => shareRepo.HasShareAccountingSchemaAsync(con, ct)))
            throw new InvalidOperationException("Pearl requires the share accounting schema migrations");
        await candidates.CheckSchemaAsync(ct);
        await verifier.CheckVersionAsync(ct);
        manager = new PearlJobManager(poolConfig, network, serializerSettings, messageBus, logger);
        if(!await manager.RefreshAsync(ct)) throw new InvalidOperationException("No compatible synchronized Pearl node");
        UpdateStats();
        background = Task.WhenAll(PollAsync(ct), ProcessCandidatesAsync(ct));
    }

    public override async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try { await base.RunAsync(stop.Token); }
        finally
        {
            stop.Cancel();
            if(background != null)
            {
                try { await background; }
                catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            }
        }
    }

    private void UpdateStats()
    {
        var current = manager.Current;
        blockchainStats ??= new BlockchainStats();
        blockchainStats.NetworkType = config.Network;
        blockchainStats.RewardType = "POW";
        if(current != null)
        {
            blockchainStats.BlockHeight = current.Height - 1;
            blockchainStats.NetworkDifficulty = current.Difficulty;
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(config.TemplatePollSeconds), ct);
            foreach(var connection in connections.Values)
            {
                var worker = connection.Context;
                if(worker != null && (!worker.IsAuthorized && clock.Now - worker.Created > TimeSpan.FromSeconds(10) ||
                    clock.Now - worker.LastActivity > TimeSpan.FromSeconds(Math.Clamp(poolConfig.ClientConnectionTimeout, 30, 600))))
                    Disconnect(connection);
            }
            var wasReady = manager.Ready;
            var changed = await manager.RefreshAsync(ct);
            UpdateStats();
            var jobCounts = connections.Values.Where(connection => connection.ContextAs<PearlWorkerContext>() != null &&
                connection.LocalEndpoint != null && poolConfig.Ports.ContainsKey(connection.LocalEndpoint.Port))
                .Select(connection => connection.ContextAs<PearlWorkerContext>()
                .JobCounts(manager.Current?.Tip, config.JobLifetimeSeconds, poolConfig.Ports[connection.LocalEndpoint.Port].DifficultyGraceSeconds)).ToArray();
            metrics.Set(poolConfig.Id, PearlMeasure.Height, blockchainStats.BlockHeight);
            metrics.Set(poolConfig.Id, PearlMeasure.CertificateVersion, manager.Ready ? 3 : 0);
            metrics.Set(poolConfig.Id, PearlMeasure.MinimumRank, 128);
            metrics.Set(poolConfig.Id, PearlMeasure.TemplateAgeSeconds, manager.TemplateAgeSeconds);
            metrics.Set(poolConfig.Id, PearlMeasure.ActiveJobs, jobCounts.Sum(x => x.Active));
            metrics.Set(poolConfig.Id, PearlMeasure.StaleJobs, jobCounts.Sum(x => x.Stale));
            metrics.Set(poolConfig.Id, PearlMeasure.ActiveVerifiers, PearlProofVerifier.ActiveVerifiers);
            metrics.Set(poolConfig.Id, PearlMeasure.ActiveProvers, PearlProofVerifier.ActiveProvers);
            metrics.Set(poolConfig.Id, PearlMeasure.CircuitCaches, PearlProofVerifier.ActiveProvers);
            if(Stopwatch.GetElapsedTime(lastHealthLog).TotalSeconds >= 60)
            {
                lastHealthLog = Stopwatch.GetTimestamp();
                try
                {
                    var pending = await candidates.CountAsync(poolConfig.Id, ct);
                    metrics.Set(poolConfig.Id, PearlMeasure.CandidateQueue, pending);
                    logger.Info(() => $"Pearl health: ready {manager.Ready}, height {manager.Current?.Height}, certificate 3, minimum rank 128, connections {connections.Count}/16, pending candidates {pending}/16");
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
                catch(Exception ex) {RpcConsumerDiagnostics.Write(logger, LogLevel.Warn, "PearlPool.Telemetry", failure: ex);}
            }
            if(changed)
                await ForEachMinerAsync((connection, token) => RunAssignmentAsync(connection,
                    () => SendJobAsync(connection), token), ct);
            else if(wasReady && !manager.Ready)
                await ForEachMinerAsync((connection, _) =>
                {
                    Disconnect(connection); // forces miners to discard work on sync loss/unsupported upgrades
                    return Task.CompletedTask;
                }, ct);
        }
    }

    private Task SendJobAsync(StratumConnection connection)
    {
        var worker = connection.ContextAs<PearlWorkerContext>();
        if(!worker.IsAuthorized || !manager.Ready) return Task.CompletedTask;
        worker.ApplyPendingDifficulty();
        var job = worker.Issue(manager.CreateJob(), config.MaximumJobsPerWorker);
        var encoded = job.Target.ToByteArray(true, true);
        var target = new byte[32];
        encoded.CopyTo(target, 32 - encoded.Length);
        return connection.NotifyAsync("mining.notify", new
        {
            job_id = job.Id, header = Convert.ToHexString(job.Block.Header.Span).ToLowerInvariant(),
            target = Convert.ToHexString(target).ToLowerInvariant(), height = job.Block.Height, cert_version = 3
        });
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, Math.Clamp(newDiff, PearlWork.MinimumDifficulty, PearlWork.MaximumDifficulty), ct);
        await SendJobAsync(connection); // caller already owns the assignment gate
    }

    protected override async Task OnRequestAsync(StratumConnection connection, Timestamped<JsonRpcRequest> request, CancellationToken ct)
    {
        var message = request.Value;
        var worker = connection.ContextAs<PearlWorkerContext>();
        if(message.Id == null || message.Id is string id && id.Length > 64 || message.Method?.Length > 64)
        {
            Disconnect(connection);
            return;
        }
        try
        {
            if(message.Method == "mining.authorize")
            {
                if(worker.IsAuthorized) throw new StratumException(StratumError.Other, "Session is already authorized");
                var parameters = message.ParamsAs<JObject>();
                var wallet = parameters?.Value<string>("wallet");
                var agent = parameters?.Value<string>("agent");
                if(wallet == null || wallet.Length > 160 || agent?.Length > 128 || parameters.Value<string>("type") != "v2")
                    throw new StratumException(StratumError.UnauthorizedWorker, "Unsupported Pearl authorization");
                (worker.Miner, worker.Worker) = PearlWorkerIdentity.Parse(wallet, parameters["worker"], network);
                worker.UserAgent = agent;
                if(!manager.Ready) throw new StratumException(StratumError.Other, "Pearl work is paused");
                worker.IsAuthorized = true;
                worker.IsSubscribed = true;
                await RunAssignmentAsync(connection, () => SendJobAsync(connection), ct);
                await connection.RespondAsync(new JsonRpcResponse<bool>(true, message.Id) {Extra = new Dictionary<string, object> {{"error", null}}});
            }
            else if(message.Method == "mining.submit")
            {
                if(!worker.IsAuthorized) throw new StratumException(StratumError.UnauthorizedWorker, "Authorization required");
                await SubmitAsync(connection, message, ct);
            }
            else throw new StratumException(StratumError.Other, "Unsupported Pearl method");
        }
        catch(PearlWorkerException)
        {
            metrics.Count(poolConfig.Id, PearlEvent.NativeFailure);
            await connection.RespondErrorAsync(StratumError.Other, "Pearl proof service unavailable", message.Id, false);
        }
        catch(StratumException ex)
        {
            metrics.Count(poolConfig.Id, ex.Code switch
            {
                StratumError.JobNotFound => PearlEvent.ShareStale,
                StratumError.DuplicateShare => PearlEvent.ShareDuplicate,
                StratumError.UnauthorizedWorker => PearlEvent.Unauthorized,
                _ => PearlEvent.RequestRejected
            });
            worker.Stats.InvalidShares++;
            await connection.RespondErrorAsync(ex.Code, ex.Message, message.Id, false);
        }
        catch(Exception ex) when(ex is PearlInvalidProofException or InvalidDataException or FormatException or ArgumentException or JsonException)
        {
            worker.Stats.InvalidShares++;
            await connection.RespondErrorAsync(StratumError.Other, "Invalid Pearl request or proof", message.Id, false);
            metrics.Count(poolConfig.Id, PearlEvent.ShareInvalid);
            ConsiderBan(connection, worker, poolConfig.Banning);
        }
    }

    private async Task SubmitAsync(StratumConnection connection, JsonRpcRequest request, CancellationToken ct)
    {
        var worker = connection.ContextAs<PearlWorkerContext>();
        if(!manager.Ready) throw new StratumException(StratumError.JobNotFound, "Pearl work is paused");
        var parameters = request.ParamsAs<JObject>();
        var id = parameters?.Value<string>("job_id");
        if(id?.Length != 32) throw new StratumException(StratumError.JobNotFound, "Unknown Pearl job");
        var job = worker.Find(id, manager.Current.Tip, config.JobLifetimeSeconds,
            poolConfig.Ports[connection.LocalEndpoint.Port].DifficultyGraceSeconds);
        if(!worker.TakeProofBudget() || !await decodeSlots.WaitAsync(0, ct))
        {
            metrics.Count(poolConfig.Id, PearlEvent.ShareBusy);
            throw new StratumException(StratumError.Other, "Pearl verifier busy; retry later");
        }
        byte[] proof;
        PearlProofVerifier.Result verified;
        try
        {
            proof = PearlProofEncoding.Decode(parameters.Value<string>("plain_proof"));
            PearlPlainProof.RequirePoolProfile(PearlPlainProof.Inspect(proof));
            var attempt = Convert.ToHexString(SHA256.HashData(proof));
            worker.RememberAttempt(job, attempt);
            var started = Stopwatch.GetTimestamp();
            try {verified = await verifier.VerifyAsync(job.Block.Header.ToArray(), job.Target, proof, false, ct);}
            catch(PearlWorkerException) {worker.ForgetAttempt(job, attempt); throw;}
            finally {metrics.Proof(poolConfig.Id, PearlProofPhase.Verify, proof.Length, Stopwatch.GetElapsedTime(started));}
            var elapsed = Stopwatch.GetElapsedTime(started);
            ReportProofTime("PearlHash.verify", elapsed);
            logger.Debug(() => $"Pearl proof verified: bytes {proof.Length}, elapsed ms {elapsed.TotalMilliseconds:F1}, candidate {verified.IsBlockCandidate}");
        }
        finally { decodeSlots.Release(); }
        var identity = PearlWork.Identity(verified.Header, verified.PublicData);
        worker.Remember(job, identity);
        var dimensions = PearlPlainProof.Inspect(proof);
        var jackpot = new BigInteger(verified.PublicData.AsSpan(116, 32), true, false);
        var achieved = (double) PearlWork.DifficultyOne * dimensions.RankAdjustedWorkFactor / (double) BigInteger.Max(BigInteger.One, jackpot);
        var networkDifficulty = PearlWork.Difficulty(PearlConsensus.DecodeTarget(BinaryPrimitives.ReadUInt32LittleEndian(verified.Header.AsSpan(72))));
        var share = new Share
        {
            PoolId = poolConfig.Id, Miner = worker.Miner, Worker = worker.Worker, UserAgent = worker.UserAgent,
            IpAddress = connection.RemoteEndpoint.Address.ToString(), SessionId = connection.ConnectionId,
            Created = clock.Now, BlockHeight = job.Block.Height, RewardBasisSatoshis = job.Block.RewardGrains,
            AccountingId = identity[..32], AccountingRole = ShareAccountingRole.Single,
            Difficulty = Math.Min(job.Difficulty, networkDifficulty), ShareDifficulty = achieved,
            ActualDifficulty = achieved, NetworkDifficulty = networkDifficulty,
            BlockReward = job.Block.RewardGrains / 100_000_000m, TransactionConfirmationData = job.Block.CoinbaseId,
            PreserveCreated = true
        };
        ShareAccounting.ValidateAndFlatten(share, new Dictionary<string, PoolConfig> {{poolConfig.Id, poolConfig}});
        // The receipt and candidate intent commit together. Never acknowledge a memory-only queue entry.
        await candidates.CommitAsync(share, identity, job, proof, verified.IsBlockCandidate, ct);
        await PublishShareAndAcknowledgeAsync(share, () => connection.RespondAsync(
            new JsonRpcResponse<bool>(true, request.Id) {Extra = new Dictionary<string, object> {{"error", null}}}),
            onAdmitted: () => {metrics.Count(poolConfig.Id, PearlEvent.ShareAccepted); worker.Stats.ValidShares++; worker.LastActivity = clock.Now;});
        await UpdateVarDiffAsync(connection, false, ct);
    }

    private void ReportProofTime(string operation, TimeSpan elapsed)
    {
        try {messageBus.SendMessage(new TelemetryEvent(operation, TelemetryCategory.Hash, elapsed, true));}
        catch(Exception ex) {RpcConsumerDiagnostics.Write(logger, LogLevel.Warn, "PearlPool.Telemetry", failure: ex);}
    }

    private async Task ProcessCandidatesAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(manager.Ready)
                {
                    var candidate = await candidates.NextAsync(poolConfig.Id, ct);
                    if(candidate != null)
                    {
                        await candidates.MarkAttemptAsync(candidate, ct);
                        await ProcessCandidateAsync(candidate, ct);
                    }
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) { metrics.Count(poolConfig.Id, PearlEvent.CandidateUncertain); RpcConsumerDiagnostics.Write(logger, LogLevel.Error, "PearlPool.Candidate", failure: ex); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task ProcessCandidateAsync(PearlCandidateStore.Candidate candidate, CancellationToken ct)
    {
        var share = JsonConvert.DeserializeObject<Share>(candidate.Share);
        if(candidate.Block == null)
        {
            var previous = candidate.Header.AsSpan(4, 32).ToArray();
            Array.Reverse(previous);
            if(manager.Current?.Tip != Convert.ToHexString(previous).ToLowerInvariant())
            {
                await candidates.CompleteAsync(candidate, null, blocksRepo, ct);
                metrics.Count(poolConfig.Id, PearlEvent.CandidateRetired);
                logger.Info("Discarded obsolete, never-submitted Pearl candidate");
                return;
            }
            using var proving = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var started = Stopwatch.GetTimestamp();
            var verification = verifier.VerifyAsync(candidate.Header, new BigInteger(candidate.Target, true, true), candidate.Proof, true, proving.Token);
            // Tip/synchronization retirement also cancels an already running prover.
            // No block has been serialized or submitted at this boundary.
            while(!verification.IsCompleted)
            {
                await Task.WhenAny(verification, Task.Delay(TimeSpan.FromSeconds(1), ct));
                if(ct.IsCancellationRequested || !manager.Ready || manager.Current?.Tip != Convert.ToHexString(previous).ToLowerInvariant())
                {
                    proving.Cancel();
                    try { await verification; }
                    catch(OperationCanceledException) when(proving.IsCancellationRequested) { }
                    ct.ThrowIfCancellationRequested();
                    await candidates.CompleteAsync(candidate, null, blocksRepo, ct);
                    return;
                }
            }
            var verified = await verification;
            var elapsed = Stopwatch.GetElapsedTime(started);
            ReportProofTime("PearlHash.prove", elapsed);
            metrics.Proof(poolConfig.Id, PearlProofPhase.Prove, verified.Certificate.Length, elapsed);
            logger.Info(() => $"Pearl certificate generated: elapsed ms {elapsed.TotalMilliseconds:F1}, bytes {verified.Certificate.Length}");
            if(PearlWork.Identity(verified.Header, verified.PublicData) != candidate.Identity)
                throw new InvalidDataException("Pearl candidate identity mismatch");
            var certified = PearlConsensus.SerializeCertifiedHeader(verified.Header, 3, verified.PublicData, verified.Certificate);
            var hashBytes = SHA256.HashData(SHA256.HashData(certified.AsSpan(certified.Length - PearlConsensus.HeaderSize)));
            Array.Reverse(hashBytes);
            var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
            var block = PearlConsensus.SerializeBlock(verified.Header, 3, verified.PublicData,
                verified.Certificate, candidate.Transactions);
            await candidates.SaveBlockAsync(candidate, block, hash, ct);
            // Read the committed bytes. Concurrent recovery must submit exactly the same block.
            candidate = await candidates.ReadAsync(candidate, ct);
            if(candidate?.Block == null) return;
            share = JsonConvert.DeserializeObject<Share>(candidate.Share);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var header = await manager.Node.ExecuteBoundedAsync<JObject>(logger, "getblockheader", deadline.Token,
            new RpcResponseLimits(262144, 64), new object[] {candidate.BlockHash, true});
        var rejected = false;
        if(header.Error != null)
        {
            if(header.Error.Code != -5) throw new InvalidDataException("Pearl candidate lookup unavailable");
            metrics.Count(poolConfig.Id, PearlEvent.CandidateSubmitted);
            var submission = await manager.Node.ExecuteBoundedAsync<JToken>(logger, "submitblock", deadline.Token,
                new RpcResponseLimits(4096, 64), new object[] {Convert.ToHexString(candidate.Block).ToLowerInvariant()});
            rejected = submission.Error == null && submission.Response?.Type == JTokenType.String;
            // Even a successful or duplicate response is reconciled by exact block identity.
            header = await manager.Node.ExecuteBoundedAsync<JObject>(logger, "getblockheader", deadline.Token,
                new RpcResponseLimits(262144, 64), new object[] {candidate.BlockHash, true});
        }
        var accepted = header.Error == null && header.Response?.Value<string>("hash") == candidate.BlockHash;
        if(!accepted && !rejected)
        {
            metrics.Count(poolConfig.Id, PearlEvent.CandidateUncertain);
            // A competing canonical block makes this no-longer-extendable submission an
            // orphan. RPC failures alone never justify discarding uncertain submission.
            if(header.Error?.Code != -5 || manager.Current?.Height <= share.BlockHeight) return;
            var canonical = await manager.CallAsync<string>(manager.Node, "getblockhash", new object[] {share.BlockHeight}, deadline.Token);
            if(canonical == candidate.BlockHash) return;
        }
        var blockRecord = new Block
        {
            PoolId = poolConfig.Id, BlockHeight = (ulong) share.BlockHeight, Hash = candidate.BlockHash,
            Miner = share.Miner, Created = share.Created, Reward = share.BlockReward,
            NetworkDifficulty = share.NetworkDifficulty, TransactionConfirmationData = share.TransactionConfirmationData,
            Status = accepted ? BlockStatus.Pending : BlockStatus.Orphaned
        };
        if(rejected && !accepted) metrics.Count(poolConfig.Id, PearlEvent.CandidateRejected);
        if(rejected && !accepted) logger.Warn("Pearl node rejected candidate; recorded as orphaned without reward credit");
        if(await candidates.CompleteAsync(candidate, blockRecord, blocksRepo, ct) && accepted)
        {
            metrics.Count(poolConfig.Id, PearlEvent.CandidateAccepted);
            poolStats.LastPoolBlockTime = clock.Now;
            messageBus.NotifyBlockFound(poolConfig.Id, blockRecord, poolConfig.Template);
            logger.Info(() => $"Pearl block accepted: {candidate.BlockHash}");
        }
    }
}
