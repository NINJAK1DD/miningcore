using System.IO;
using Autofac;
using AutoMapper;
using Dapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Block = Miningcore.Persistence.Model.Block;
using IBlockRepository = Miningcore.Persistence.Repositories.IBlockRepository;

namespace Miningcore.Blockchain.Pearl;

[CoinFamily(CoinFamily.Pearl)]
public sealed class PearlPayoutHandler(IConnectionFactory cf, IMapper mapper, IShareRepository shares,
    IBlockRepository blocks, IBalanceRepository balances, IPaymentRepository payments,
    IMasterClock clock, IMessageBus bus, JsonSerializerSettings settings) :
    PayoutHandlerBase(cf, mapper, shares, blocks, balances, payments, clock, bus), IPayoutHandler
{
    internal sealed class Intent
    {
        public string PoolId { get; set; }
        public string Marker { get; set; }
        public string Recipients { get; set; }
        public string TransactionId { get; set; }
        public DateTime Created { get; set; }
    }
    internal const decimal MarkerAmount = 0.01m;
    private readonly PearlMetrics metrics = PearlMetrics.Default;
    private RpcClient wallet;
    private RpcClient node;
    private RpcClient[] nodes;
    private PearlNetwork network;
    private PearlPaymentProcessingConfigExtra config;
    private bool classificationStarted;
    private string classifiedTip;
    protected override string LogCategory => "Pearl Payout Handler";
    public double AdjustBlockEffort(double effort) => effort;

    public async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        clusterConfig = cc;
        poolConfig = pc;
        logger = LogManager.GetLogger("Pearl Payments");
        config = pc.PaymentProcessing.Extra.SafeExtensionDataAs<PearlPaymentProcessingConfigExtra>() ?? new();
        var networkName = (pc.Extra.SafeExtensionDataAs<PearlPoolConfig>() ?? new()).Network;
        network = networkName switch {"mainnet" => PearlNetwork.Mainnet, "regtest" => PearlNetwork.Regtest,
            _ => throw new InvalidDataException("Unsupported Pearl payout network")};
        if(config.FeeRateGrainsPerKb is null or < 1000 or > 100_000_000 || config.MinimumConfirmations is null or < 101 or > 100000)
            throw new InvalidDataException("Invalid Pearl payout fee or confirmation policy");
        if(pc.PaymentProcessing.MinimumPayment < MarkerAmount)
            throw new InvalidDataException("Pearl minimum payment must be at least 0.01 PRL to avoid dust outputs");
        nodes = pc.Daemons.Where(x => string.IsNullOrEmpty(x.Category) || x.Category == "node")
            .Select(x => new RpcClient(x, settings, messageBus, pc.Id)).ToArray();
        if(nodes.Length is < 1 or > 4) throw new InvalidDataException("Pearl requires one to four payout nodes");
        wallet = new RpcClient(pc.Daemons.Single(x => x.Category == "wallet"), settings, messageBus, pc.Id);
        if(await RpcAsync<string>(wallet, "getblockhash", new object[] {0}, ct) != PearlJobManager.Genesis(network))
            throw new InvalidDataException("Pearl wallet/node network mismatch");
        await SelectNodeAsync(null, ct);
        var reward = await RpcAsync<JObject>(wallet, "validateaddress", new object[] {pc.Address}, ct);
        if(reward.Value<bool?>("ismine") != true) throw new InvalidDataException("Oyster must own the pool reward address");
        await cf.Run(async con => await con.ExecuteAsync(new CommandDefinition(
            "SELECT poolid, marker, recipients, transactionid, created FROM pearl_payout_intents LIMIT 0", cancellationToken: ct)));
        await cf.Run(async con => await con.ExecuteAsync(new CommandDefinition(
            "SELECT poolid, blockid, blockhash FROM pearl_payout_holds LIMIT 0", cancellationToken: ct)));
    }

    private async Task<T> RpcAsync<T>(RpcClient rpc, string method, object parameters, CancellationToken ct) where T : class
    {
        var response = await rpc.ExecuteBoundedAsync<T>(logger, method, ct, new RpcResponseLimits(2_000_000, 64), parameters);
        if(response.Error != null) throw new InvalidDataException($"Pearl {method} failed ({response.Error.Code})");
        return response.Response;
    }

    private async Task SelectNodeAsync(string expectedTip, CancellationToken ct, bool allowFallback = false)
    {
        foreach(var candidate in nodes)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                if(await RpcAsync<string>(candidate, "getblockhash", new object[] {0}, deadline.Token) != PearlJobManager.Genesis(network)) continue;
                var chain = await RpcAsync<JObject>(candidate, "getblockchaininfo", null, deadline.Token);
                var tip = chain?.Value<string>("bestblockhash");
                if(chain?.Value<long?>("blocks") is null or < 0 || chain.Value<bool?>("initialblockdownload") == true ||
                    !IsTransactionId(tip) || expectedTip != null && expectedTip != tip) continue;
                node = candidate; // Pin one validated endpoint for the entire operation.
                return;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
            catch(Exception ex) { RpcConsumerDiagnostics.Write(logger, LogLevel.Warn, "PearlPayout.NodeSelection", failure: ex); }
        }
        if(allowFallback && expectedTip != null) { await SelectNodeAsync(null, ct); return; }
        throw new InvalidDataException("No compatible synchronized Pearl payout node");
    }

    public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        classificationStarted = true;
        classifiedTip = null;
        var synced = (await RpcAsync<JToken>(wallet, "chainsynced", null, ct))?.Value<bool>() == true;
        var walletTip = synced ? await RpcAsync<string>(wallet, "getbestblockhash", null, ct) : null;
        if(synced && !IsTransactionId(walletTip)) throw new InvalidDataException("Missing Pearl wallet chain identity");
        // Prefer the same chain used by new payouts. While Oyster rescans, still
        // observe reorgs, but do not authorize fresh reward allocations.
        await SelectNodeAsync(walletTip, ct);
        var chain = await RpcAsync<JObject>(node, "getblockchaininfo", null, ct);
        var tip = chain?.Value<ulong?>("blocks") ?? throw new InvalidDataException("Missing Pearl chain height");
        var bestHash = chain.Value<string>("bestblockhash");
        if(!IsTransactionId(bestHash)) throw new InvalidDataException("Missing Pearl chain identity");
        // Record all affected credited rewards, including those outside this cycle's
        // bounded reconciliation batch. A later height increase is not canonical proof.
        await cf.Run(con => con.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pearl_payout_holds(poolid, blockid, blockhash)
            SELECT poolid, id, hash FROM blocks
            WHERE poolid=@Id AND status='confirmed' AND blockheight>@tip
            ON CONFLICT (poolid, blockid) DO NOTHING
            """, new {poolConfig.Id, tip = checked((long) tip)}, cancellationToken: ct)));
        var result = new List<Block>();
        var verifiedConfirmed = new List<Block>();
        foreach(var block in blocks)
        {
            block.PearlCustodialEvidenceVerified = false;
            // A node recovering below this height cannot establish a competing chain.
            // Leave the reward eligible for reconciliation once the node catches up.
            if(block.BlockHeight > tip) continue;
            var canonical = await RpcAsync<string>(node, "getblockhash", new object[] {block.BlockHeight}, ct);
            if(!IsTransactionId(canonical))
                throw new InvalidDataException("Missing Pearl canonical block identity");
            if(canonical != block.Hash)
            {
                if(block.Status == BlockStatus.Orphaned) continue;
                // A successful canonical lookup proves a competing block; an RPC outage does not.
                // A post-settlement reorg cannot authorize reversing or re-paying booked funds.
                block.Status = block.Status == BlockStatus.Confirmed ? BlockStatus.Quarantined : BlockStatus.Orphaned;
                block.ConfirmationProgress = 0;
                block.NotifyBlockUnlockedOnUpdate = true;
                result.Add(block);
                continue;
            }
            // Reorg observation does not depend on a wallet finishing its rescan.
            // Fresh reward credit always does.
            if(block.Status == BlockStatus.Confirmed)
            {
                verifiedConfirmed.Add(block);
                continue;
            }
            if(!synced) continue;
            var header = await RpcAsync<JObject>(node, "getblockheader", new object[] {block.Hash, true}, ct);
            var transaction = await RpcAsync<JObject>(wallet, "gettransaction", new object[] {block.TransactionConfirmationData}, ct);
            if(transaction?.Value<string>("blockhash") != block.Hash || header?.Value<string>("hash") != block.Hash)
                continue;
            var hex = transaction.Value<string>("hex");
            if(hex == null || hex.Length > 1_000_000) throw new InvalidDataException("Invalid Pearl coinbase evidence");
            var coinbase = Transaction.Load(Convert.FromHexString(hex), Network.Main);
            if(!coinbase.IsCoinBase || coinbase.GetHash().ToString() != block.TransactionConfirmationData)
                throw new InvalidDataException("Pearl coinbase identity mismatch");
            var script = PearlAddress.RewardScript(poolConfig.Address, PearlNetworkRules.AddressPrefix(network));
            var reward = coinbase.Outputs.Where(x => x.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(script.ToBytes()))
                .Sum(x => x.Value.Satoshi) / 100_000_000m;
            if(reward <= 0 || reward != block.Reward) throw new InvalidDataException("Pearl coinbase reward mismatch");
            var confirmations = Math.Min(header.Value<long>("confirmations"), transaction.Value<long>("confirmations"));
            block.PearlCustodialEvidenceVerified = true;
            block.Status = BlockStatus.Pending;
            block.ConfirmationProgress = Math.Clamp((double) confirmations / config.MinimumConfirmations.Value, 0, 1);
            block.NotifyBlockConfirmationProgressOnUpdate = true;
            if(confirmations >= config.MinimumConfirmations)
            {
                block.Status = BlockStatus.Confirmed;
                block.NotifyBlockUnlockedOnUpdate = true;
            }
            result.Add(block);
        }
        if(await RpcAsync<string>(node, "getbestblockhash", null, ct) != bestHash) return Array.Empty<Block>();
        // Release only the exact stored identities checked on a stable chain. Failure,
        // tip changes, competing hashes and unvisited batches retain durable holds.
        if(verifiedConfirmed.Count > 0)
            await cf.RunTx(async (con, tx) =>
            {
                foreach(var verified in verifiedConfirmed)
                    await con.ExecuteAsync(new CommandDefinition("""
                        DELETE FROM pearl_payout_holds h USING blocks b
                        WHERE h.poolid=@PoolId AND h.blockid=@Id AND h.blockhash=@Hash
                        AND b.id=h.blockid AND b.poolid=h.poolid AND b.hash=h.blockhash
                        AND b.blockheight=@BlockHeight AND b.status='confirmed'
                        """, new {verified.PoolId, verified.Id, verified.Hash,
                            BlockHeight = checked((long) verified.BlockHeight)}, tx, cancellationToken: ct));
            }, ct: ct);
        classifiedTip = bestHash;
        foreach(var observed in result)
            metrics.Count(poolConfig.Id, observed.Status switch
            {
                BlockStatus.Confirmed => PearlEvent.RewardMature,
                BlockStatus.Quarantined => PearlEvent.RewardQuarantined,
                BlockStatus.Orphaned => PearlEvent.RewardOrphaned,
                _ => PearlEvent.RewardPending
            });
        return result.ToArray();
    }

    internal async Task<Block[]> ReconciliationBlocksAsync(ulong tip, long cursor, CancellationToken ct)
    {
        var minimum = tip > 4032 ? checked((long) (tip - 4032)) : 0;
        async Task<Block[]> Read(long cursor) => await cf.Run(async con => (await con.QueryAsync<Block>(new CommandDefinition("""
            SELECT b.* FROM blocks b LEFT JOIN pearl_payout_holds h
                ON h.poolid=b.poolid AND h.blockid=b.id
            WHERE b.poolid=@Id AND b.status IN ('orphaned','confirmed')
            AND (h.blockid IS NOT NULL OR (b.blockheight>=@minimum AND b.id>@cursor))
            ORDER BY (h.blockid IS NOT NULL) DESC, b.id LIMIT 64
            """, new {poolConfig.Id, minimum, cursor}, cancellationToken: ct))).ToArray());
        var result = await Read(cursor);
        if(result.Length == 0 && cursor != 0) result = await Read(0);
        return result;
    }

    internal static bool CanApplyClassification(Block persisted, Block classified)
    {
        if(persisted == null || classified == null || persisted.Id != classified.Id || persisted.PoolId != classified.PoolId ||
            persisted.Hash != classified.Hash || persisted.TransactionConfirmationData != classified.TransactionConfirmationData ||
            persisted.BlockHeight != classified.BlockHeight || persisted.Miner != classified.Miner || persisted.Created != classified.Created ||
            persisted.NetworkDifficulty != classified.NetworkDifficulty || persisted.Type != classified.Type ||
            persisted.Reward != classified.Reward || persisted.SettlementMode != null || classified.SettlementMode != null)
            return false;
        if(persisted.Status == BlockStatus.Confirmed) return classified.Status == BlockStatus.Quarantined;
        if(persisted.Status is not (BlockStatus.Pending or BlockStatus.Orphaned)) return false;
        return classified.Status == BlockStatus.Orphaned ||
            classified.PearlCustodialEvidenceVerified && classified.Status is BlockStatus.Pending or BlockStatus.Confirmed;
    }

    public Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct) => TrackPayoutAsync(balances, async () =>
    {
        try {await ExecutePayoutAsync(balances, ct);}
        catch(PayoutOutcomeUncertainException)
        {
            metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, 1);
            throw;
        }
        catch
        {
            metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, -1);
            metrics.Count(poolConfig.Id, PearlEvent.PayoutHeld);
            throw;
        }
    });

    private async Task ExecutePayoutAsync(Balance[] balances, CancellationToken ct)
    {
        var pending = await ReadIntentAsync(ct);
        metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, pending == null ? 0 : 1);
        if(pending != null)
        {
            await ReconcileAsync(pending, ct);
            return;
        }
        if(await cf.Run(con => con.ExecuteScalarAsync<bool>(new CommandDefinition(
               "SELECT EXISTS(SELECT 1 FROM blocks WHERE poolid=@Id AND status='quarantined')", new {poolConfig.Id}, cancellationToken: ct))))
            throw new InvalidDataException("Pearl payments are held pending review of a quarantined block");
        if(await cf.Run(con => con.ExecuteScalarAsync<bool>(new CommandDefinition(
               "SELECT EXISTS(SELECT 1 FROM pearl_payout_holds WHERE poolid=@Id)", new {poolConfig.Id}, cancellationToken: ct))))
            throw new InvalidDataException("Pearl payments await canonical verification of deferred confirmed blocks");
        if((await RpcAsync<JToken>(wallet, "chainsynced", null, ct))?.Value<bool>() != true)
            throw new InvalidDataException("Oyster is not synchronized with the payout node");
        var walletTip = await RpcAsync<string>(wallet, "getbestblockhash", null, ct);
        if(!IsTransactionId(walletTip)) throw new InvalidDataException("Invalid Oyster chain tip");
        await SelectNodeAsync(walletTip, ct);
        var nodeTip = await RpcAsync<string>(node, "getbestblockhash", null, ct);
        if(!IsTransactionId(walletTip) || !IsTransactionId(nodeTip) || walletTip != nodeTip)
            throw new InvalidDataException("Oyster is not synchronized with the payout node");
        if(classificationStarted && classifiedTip != nodeTip)
            throw new InvalidDataException("Pearl payments require classification on the current chain tip");
        var chain = await RpcAsync<JObject>(node, "getblockchaininfo", null, ct);
        var tip = chain?.Value<long?>("blocks");
        if(tip is null or < 0 || chain.Value<string>("bestblockhash") != nodeTip)
            throw new InvalidDataException("Missing or changed Pearl payout chain evidence");
        // Agreement on a shorter chain does not establish previously credited rewards.
        // This temporary hold must not obstruct reconciliation of an existing intent above.
        if(await cf.Run(con => con.ExecuteScalarAsync<bool>(new CommandDefinition(
               "SELECT EXISTS(SELECT 1 FROM blocks WHERE poolid=@Id AND status='confirmed' AND blockheight>@tip)",
               new {poolConfig.Id, tip = tip.Value}, cancellationToken: ct))))
            throw new InvalidDataException("Pearl payments are held while a confirmed block is above the node tip");
        var selected = balances.Take(100).Select(x => x with { Amount = decimal.Floor(x.Amount * 100_000_000m) / 100_000_000m })
            .Where(x => x.Amount >= poolConfig.PaymentProcessing.MinimumPayment).ToArray();
        if(selected.Length == 0) return;
        var amounts = selected.ToDictionary(x => x.Address, x => x.Amount, StringComparer.Ordinal);
        _ = PearlWalletRequest.SendMany(amounts, network, config.FeeRateGrainsPerKb.Value);
        var available = await RpcAsync<JToken>(wallet, "getbalance", new object[] {"default", 1}, ct);
        var inventory = await wallet.ExecuteBoundedAsync<JArray>(logger, "listunspent", ct,
            new RpcResponseLimits(PearlWalletRequest.UnspentResponseBytes, 64), new object[] {1, 9999999});
        if(inventory.Error != null || inventory.Response == null) throw new InvalidDataException("Pearl wallet funding inventory unavailable");
        var unspent = inventory.Response;
        metrics.Set(poolConfig.Id, PearlMeasure.WalletUtxos, unspent.Count);
        if(unspent.Count >= 5000)
            logger.Warn("Pearl wallet has at least 5000 unspent outputs; schedule audited wallet maintenance before the 65536-output inventory limit");
        var feeReserve = PearlWalletRequest.FeeReserve(unspent, selected.Sum(x => x.Amount) + MarkerAmount,
            selected.Length, config.FeeRateGrainsPerKb.Value);
        if(available == null || available.Value<decimal>() < selected.Sum(x => x.Amount) + MarkerAmount + feeReserve)
            throw new InvalidDataException("Oyster has insufficient spendable funds including the payout marker and fee");
        try
        {
            if(!string.IsNullOrEmpty(config.WalletPassword))
                await RpcAsync<JToken>(wallet, "walletpassphrase", new object[] {config.WalletPassword, 60}, ct);
            if((await RpcAsync<JToken>(wallet, "walletislocked", null, ct))?.Value<bool>() != false)
                throw new InvalidDataException("Oyster wallet is locked");
            var marker = await RpcAsync<string>(wallet, "getnewaddress", new object[] {"default"}, ct);
            PearlAddress.RewardScript(marker, PearlNetworkRules.AddressPrefix(network));
            var ownership = await RpcAsync<JObject>(wallet, "validateaddress", new object[] {marker}, ct);
            if(ownership?.Value<bool?>("ismine") != true || !amounts.TryAdd(marker, MarkerAmount))
                throw new InvalidDataException("Oyster payout marker must be a fresh wallet-owned address");
            var prior = await RpcAsync<JArray>(wallet, "listaddresstransactions", new object[] {new[] {marker}, "*"}, ct);
            if(prior == null || prior.Count != 0) throw new InvalidDataException("Oyster payout marker is already used");
            var request = PearlWalletRequest.SendMany(amounts, network, config.FeeRateGrainsPerKb.Value);
            pending = new Intent {PoolId = poolConfig.Id, Marker = marker,
                Recipients = JsonConvert.SerializeObject(selected), Created = clock.Now};
            await cf.RunTx(async (con, tx) => await con.ExecuteAsync(new CommandDefinition("""
                INSERT INTO pearl_payout_intents(poolid, marker, recipients, created)
                VALUES(@PoolId, @Marker, @Recipients, @Created)
                """, pending, tx, cancellationToken: ct)), ct: ct, classifyCommitOutcome: true);
            // Once the intent exists, no error path may issue a fresh sendmany for this batch.
            metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, 1);
            metrics.Count(poolConfig.Id, PearlEvent.PayoutAttempt);
            TrackPayoutSubmission(ct, selected);
            try
            {
                pending.TransactionId = await RpcAsync<string>(wallet, "sendmany", request, ct);
                if(!IsTransactionId(pending.TransactionId)) throw new InvalidDataException("Invalid Oyster payout transaction identity");
                TrackPayoutTransaction(selected, pending.TransactionId);
                await cf.Run(async con => await con.ExecuteAsync(new CommandDefinition(
                    "UPDATE pearl_payout_intents SET transactionid=@TransactionId WHERE poolid=@PoolId AND marker=@Marker", pending, cancellationToken: ct)));
                await ReconcileAsync(pending, ct);
            }
            catch(Exception ex) when(ex is not PayoutOutcomeUncertainException)
            {
                metrics.Count(poolConfig.Id, PearlEvent.PayoutUncertain);
                throw new PayoutOutcomeUncertainException("Pearl payout requires reconciliation of its durable marker; do not resend", ex);
            }
        }
        finally
        {
            await RelockPayoutWalletSafelyAsync(async token => await RpcAsync<JToken>(wallet, "walletlock", null, token));
        }
    }

    private Task<Intent> ReadIntentAsync(CancellationToken ct) => cf.Run(con => con.QuerySingleOrDefaultAsync<Intent>(
        new CommandDefinition("SELECT * FROM pearl_payout_intents WHERE poolid=@Id", new {poolConfig.Id}, cancellationToken: ct)));

    internal async Task<Balance[]> PendingRecipientsAsync(CancellationToken ct)
    {
        var intent = await ReadIntentAsync(ct);
        if(intent == null) return null;
        var recipients = JsonConvert.DeserializeObject<Balance[]>(intent.Recipients);
        if(recipients == null || recipients.Length is < 1 or > 100 || recipients.Any(x => x.PoolId != poolConfig.Id))
            throw new InvalidDataException("Invalid persisted Pearl recipients");
        return recipients;
    }

    private async Task ReconcileAsync(Intent intent, CancellationToken ct)
    {
        try
        {
            // Prefer Oyster's chain without requiring catch-up to recover an
            // existing broadcast. Canonical/mempool evidence is still checked
            // against one pinned endpoint; this never authorizes a new send.
            var preferredTip = await RpcAsync<string>(wallet, "getbestblockhash", null, ct);
            await SelectNodeAsync(IsTransactionId(preferredTip) ? preferredTip : null, ct, allowFallback: true);
            var recipients = JsonConvert.DeserializeObject<Balance[]>(intent.Recipients);
            if(recipients == null || recipients.Length is < 1 or > 100 || recipients.Any(x => x.PoolId != poolConfig.Id))
                throw new InvalidDataException("Invalid persisted Pearl recipients");
            if(intent.TransactionId == null)
            {
                var matches = await RpcAsync<JArray>(wallet, "listaddresstransactions", new object[] {new[] {intent.Marker}, "*"}, ct);
                var ids = matches?.Select(x => x.Value<string>("txid")).Distinct(StringComparer.Ordinal).ToArray();
                if(ids?.Length != 1 || !IsTransactionId(ids[0]))
                    throw new InvalidDataException("No unique Oyster transaction for the durable marker; manual reconciliation required");
                intent.TransactionId = ids[0];
            }
            var evidence = await RpcAsync<JObject>(wallet, "gettransaction", new object[] {intent.TransactionId}, ct);
            VerifyPayout(evidence, intent.TransactionId, intent.Marker, recipients, network);
            var transaction = Transaction.Load(Convert.FromHexString(evidence.Value<string>("hex")), Network.Main);
            var expectedScripts = recipients.Select(x => x.Address).Append(intent.Marker)
                .Select(x => PearlAddress.RewardScript(x, PearlNetworkRules.AddressPrefix(network)).ToHex()).ToHashSet(StringComparer.Ordinal);
            foreach(var output in transaction.Outputs.Where(x => !expectedScripts.Contains(x.ScriptPubKey.ToHex())))
            {
                var script = output.ScriptPubKey.ToBytes();
                if(script.Length != 34 || script[0] != 0x51 || script[1] != 0x20)
                    throw new InvalidDataException("Unexpected Oyster payout change script");
                var changeAddress = Encoders.Bech32(PearlNetworkRules.AddressPrefix(network)).Encode(1, script[2..]);
                var ownership = await RpcAsync<JObject>(wallet, "validateaddress", new object[] {changeAddress}, ct);
                if(ownership?.Value<bool?>("ismine") != true)
                    throw new InvalidDataException("Oyster payout contains an unowned extra output");
            }
            var fee = Math.Abs(evidence.Value<decimal>("fee"));
            var feeCeiling = Math.Max(MarkerAmount, transaction.ToBytes().Length * 2m * config.FeeRateGrainsPerKb.Value / 1000 / 100_000_000);
            if(fee > feeCeiling || fee * 100_000_000 != decimal.Truncate(fee * 100_000_000))
                throw new InvalidDataException("Unexpected Oyster payout fee");
            // Wallet possession alone is insufficient: require node mempool or chain evidence.
            var observed = await node.ExecuteBoundedAsync<JObject>(logger, "getrawtransaction", ct,
                new RpcResponseLimits(2_000_000, 64), new object[] {intent.TransactionId, 1});
            var confirmedWithoutIndex = false;
            if(observed.Error?.Code == -5 && evidence.Value<long>("confirmations") > 0)
            {
                confirmedWithoutIndex = await VerifyConfirmedTransactionAsync(evidence.Value<string>("blockhash"), intent.TransactionId, ct);
                if(!confirmedWithoutIndex) throw new InvalidDataException("Missing canonical Pearl payout inclusion evidence");
            }
            else if(observed.Error?.Code == -5)
            {
                // Re-publish only the exact independently checked transaction, never another sendmany.
                await node.ExecuteBoundedAsync<JToken>(logger, "sendrawtransaction", ct,
                    new RpcResponseLimits(4096, 64), new object[] {evidence.Value<string>("hex")});
                observed = await node.ExecuteBoundedAsync<JObject>(logger, "getrawtransaction", ct,
                    new RpcResponseLimits(2_000_000, 64), new object[] {intent.TransactionId, 1});
            }
            var raw = observed.Error == null ? observed.Response : null;
            if(!confirmedWithoutIndex && (raw?.Value<string>("txid") != intent.TransactionId || raw.Value<string>("hex") != evidence.Value<string>("hex")))
                throw new InvalidDataException("Pearl payout is not known to the node");
            await PersistPaymentsAsync(recipients, intent.TransactionId);
            await cf.Run(async con => await con.ExecuteAsync(new CommandDefinition(
                "DELETE FROM pearl_payout_intents WHERE poolid=@PoolId AND marker=@Marker", intent, cancellationToken: ct)));
            metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, 0);
            metrics.Count(poolConfig.Id, PearlEvent.PayoutCompleted);
            NotifyPayoutSuccess(poolConfig.Id, recipients, new[] {intent.TransactionId}, fee);
        }
        catch(Exception ex) when(ex is not PayoutOutcomeUncertainException)
        {
            metrics.Set(poolConfig.Id, PearlMeasure.PayoutState, 1);
            metrics.Count(poolConfig.Id, PearlEvent.PayoutUncertain);
            throw new PayoutOutcomeUncertainException("Pearl payout intent retained for reconciliation; do not resend", ex);
        }
    }

    internal static bool IsTransactionId(string id) => id?.Length == 64 && id.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<bool> VerifyConfirmedTransactionAsync(string blockHash, string transactionId, CancellationToken ct)
    {
        if(!IsTransactionId(blockHash)) return false;
        var tip = await RpcAsync<string>(node, "getbestblockhash", null, ct);
        if(!IsTransactionId(tip)) return false;
        // Unlike getrawtransaction, getblock works without the optional txindex.
        var response = await node.ExecuteBoundedAsync<JObject>(logger, "getblock", ct,
            new RpcResponseLimits(12_000_000, 64), new object[] {blockHash, 1});
        var block = response.Error == null ? response.Response : null;
        var height = block?.Value<long?>("height");
        if(block?.Value<string>("hash") != blockHash || height is null or < 0 ||
            block.Value<long?>("confirmations") is null or <= 0 || block["tx"] is not JArray transactions ||
            transactions.Count(x => x.Type == JTokenType.String && x.Value<string>() == transactionId) != 1)
            return false;
        if(await RpcAsync<string>(node, "getblockhash", new object[] {height.Value}, ct) != blockHash) return false;
        return await RpcAsync<string>(node, "getbestblockhash", null, ct) == tip;
    }

    internal static void VerifyPayout(JObject evidence, string id, string marker, Balance[] recipients, PearlNetwork network)
    {
        var hex = evidence?.Value<string>("hex");
        if(!IsTransactionId(id) || evidence?.Value<string>("txid") != id || hex == null || hex.Length > 1_000_000 ||
            evidence.Value<long>("confirmations") < 0 || evidence["walletconflicts"] is JArray conflicts && conflicts.Count != 0)
            throw new InvalidDataException("Invalid Oyster payout evidence");
        var tx = Transaction.Load(Convert.FromHexString(hex), Network.Main);
        if(tx.IsCoinBase || tx.GetHash().ToString() != id) throw new InvalidDataException("Payout transaction mismatch");
        var expected = recipients.ToDictionary(x => x.Address, x => x.Amount);
        expected.Add(marker, MarkerAmount);
        _ = PearlWalletRequest.SendMany(expected, network, 1000);
        foreach(var (address, amount) in expected)
        {
            var script = PearlAddress.RewardScript(address, PearlNetworkRules.AddressPrefix(network));
            var outputs = tx.Outputs.Where(x => x.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(script.ToBytes())).ToArray();
            if(outputs.Length != 1 || outputs[0].Value.Satoshi != amount * 100_000_000m)
                throw new InvalidDataException("Oyster payout output mismatch");
        }
    }
}
