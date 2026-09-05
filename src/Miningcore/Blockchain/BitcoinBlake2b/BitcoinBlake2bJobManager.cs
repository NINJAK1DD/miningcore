using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;
using Miningcore.Stratum;
using Miningcore.Rpc;
using Miningcore.JsonRpc;
using NBitcoin;
using System.Text;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.BitcoinBlake2b;

public class BitcoinBlake2bJobManager : BitcoinJobManager
{
    public BitcoinBlake2bJobManager(IComponentContext ctx, IMasterClock clock,
        IMessageBus messageBus, IExtraNonceProvider extraNonceProvider,
        IBlockCandidateRecorder blockCandidateRecorder = null) :
        base(ctx, clock, messageBus, extraNonceProvider,
            blockCandidateRecorder)
    {
    }

    private BitcoinBlake2bTemplate blake2bCoin;
    // Startup completes before polling; the inherited Jobs observable's
    // Concat serializes runtime updates, including backoff/attestation state.
    private int activationRpcFailures;
    private DateTime nextActivationRpcAttempt;
    private JsonRpcError activationRpcError;
    private DateTime daemonAttestationExpires;
    private string attestedChain;
    internal static readonly TimeSpan DaemonAttestationLifetime = TimeSpan.FromSeconds(30);

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        var response = await rpc.ExecuteAsync<JObject>(logger,
            BitcoinCommands.GetNetworkInfo, ct);
        ValidateDaemonIdentity(response.Error == null ? response.Response : null,
            poolConfig.Id);
        await base.EnsureDaemonsSynchedAsync(ct);
        var chain = await rpc.ExecuteAsync<JObject>(logger,
            BitcoinCommands.GetBlockchainInfo, ct);
        var chainName = chain.Response?["chain"]?.Type == JTokenType.String
            ? chain.Response["chain"].Value<string>() : null;
        if(chain.Error != null || chainName is not ("main" or "regtest"))
            throw new PoolStartupException($"Pool '{poolConfig.Id}' requires Bitcoin BLAKE2b mainnet or isolated regtest", poolConfig.Id);
        var deployment = await rpc.ExecuteAsync<JObject>(logger, "getdeploymentinfo", ct);
        ValidateDeployment(deployment.Error == null ? deployment.Response : null,
            blake2bCoin.Networks[chainName].Blake2bActivationHeight!.Value,
            poolConfig.Id);
        attestedChain = chainName;
        // Re-attest the first runtime template after chain-specific setup.
        daemonAttestationExpires = DateTime.MinValue;
    }

    internal static void ValidateDeployment(JObject info, uint expectedHeight, string poolId)
    {
        var deployment = info?["blake2b"] as JObject;
        if(deployment?["height"]?.Type != JTokenType.Integer ||
           !uint.TryParse(deployment["height"].ToString(), out var height) || height != expectedHeight ||
           deployment["active"]?.Type != JTokenType.Boolean || !deployment["active"].Value<bool>())
            throw new PoolStartupException($"Pool '{poolId}' requires active Bitcoin BLAKE2b deployment at reviewed height {expectedHeight}", poolId);
    }

    internal static void ValidateDaemonIdentity(JObject info, string poolId)
    {
        // This is a compatibility gate, not cryptographic authentication.
        // Operators must verify the downloaded binary independently.
        var version = info?["version"];
        var subversion = info?["subversion"];
        if(version?.Type != JTokenType.Integer || !long.TryParse(version.ToString(), out var number) || number != 290401 ||
           subversion?.Type != JTokenType.String ||
           !subversion.Value<string>().Contains("/Knots:20260508/", StringComparison.Ordinal))
            throw new PoolStartupException(
                $"Pool '{poolId}' requires reviewed Bitcoin Knots 29.4.1.knots20260508; daemon protocol upgrades require an explicit compatibility review", poolId);
    }

    protected override async Task<RpcResponse<BlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        RpcResponse<BlockTemplate> response;
        try
        {
            response = await FetchBlockTemplateAsync(ct);
        }
        catch
        {
            daemonAttestationExpires = DateTime.MinValue;
            throw;
        }
        if(response.Error != null)
            daemonAttestationExpires = DateTime.MinValue;
        if(response.Error == null)
        {
            if(network != null)
            {
                var attestationError = await AttestDaemonAsync(ct);
                if(attestationError != null)
                    return new(null, attestationError);
            }
            if(response.Response?.Rules?.Contains("!blake2b", StringComparer.Ordinal) != true)
                throw new PoolStartupException($"Pool '{poolConfig.Id}' requires an active mandatory '!blake2b' GBT rule", poolConfig.Id);
            if(network != null)
            {
                ValidateHeaderV2Template(response.Response);
                var contract = blake2bCoin.GetNetwork(network.ChainName);
                if(response.Response.Height == contract.Blake2bActivationHeight)
                    return await VerifyActivationParentAsync(response.Response, contract, ct);
            }
        }
        return response;
    }

    protected virtual Task<RpcResponse<BlockTemplate>> FetchBlockTemplateAsync(CancellationToken ct) =>
        base.GetBlockTemplateAsync(ct);

    protected virtual Task<RpcResponse<JObject>> ReadDaemonAttestationAsync(string method, CancellationToken ct) =>
        rpc.ExecuteAsync<JObject>(logger, method, ct);

    private async Task<JsonRpcError> AttestDaemonAsync(CancellationToken ct)
    {
        if(clock.Now < daemonAttestationExpires)
            return null;

        // A successful cache entry is installed only after every response
        // validates. Transport errors keep the cache expired and publish no
        // fresh work; successful contradictions take the terminal path.
        daemonAttestationExpires = DateTime.MinValue;
        var info = await ReadDaemonAttestationAsync(BitcoinCommands.GetNetworkInfo, ct);
        if(info.Error != null) return info.Error;
        ValidateDaemonIdentity(info.Response, poolConfig.Id);

        var chain = await ReadDaemonAttestationAsync(BitcoinCommands.GetBlockchainInfo, ct);
        if(chain.Error != null) return chain.Error;
        var expectedChain = attestedChain ?? (network == Network.Main ? "main" : "regtest");
        if(chain.Response?["chain"]?.Type != JTokenType.String ||
           !string.Equals(chain.Response["chain"].Value<string>(), expectedChain, StringComparison.Ordinal))
            throw new PoolStartupException($"Pool '{poolConfig.Id}' Bitcoin BLAKE2b daemon chain changed; expected '{expectedChain}'", poolConfig.Id);

        var deployment = await ReadDaemonAttestationAsync("getdeploymentinfo", ct);
        if(deployment.Error != null) return deployment.Error;
        ValidateDeployment(deployment.Response,
            blake2bCoin.Networks[expectedChain].Blake2bActivationHeight!.Value, poolConfig.Id);
        attestedChain = expectedChain;
        daemonAttestationExpires = clock.Now.Add(DaemonAttestationLifetime);
        return null;
    }

    protected virtual Task<RpcResponse<JObject>> GetActivationParentAsync(string hash, CancellationToken ct) =>
        rpc.ExecuteAsync<JObject>(logger, "getblockheader", ct, new object[] { hash });

    internal async Task<RpcResponse<BlockTemplate>> VerifyActivationParentAsync(BlockTemplate template,
        BitcoinTemplate.BitcoinNetworkParams contract, CancellationToken ct)
    {
        if(activationRpcError != null && clock.Now < nextActivationRpcAttempt)
            return new(null, activationRpcError);

        var parent = await GetActivationParentAsync(template.PreviousBlockhash, ct);
        if(parent.Error != null)
        {
            daemonAttestationExpires = DateTime.MinValue;
            // No answer is not evidence of a consensus violation. Return the
            // ordinary RPC error so the shared loop retains its last verified
            // job (or waits before initial publication), with bounded backoff.
            activationRpcError = parent.Error;
            activationRpcFailures = Math.Min(activationRpcFailures + 1, 6);
            nextActivationRpcAttempt = clock.Now.AddSeconds(Math.Min(30, 1 << (activationRpcFailures - 1)));
            return new(null, parent.Error);
        }

        activationRpcFailures = 0;
        activationRpcError = null;
        if(parent.Response?["bits"]?.Type != JTokenType.String)
            throw new PoolStartupException($"Pool '{poolConfig.Id}' received malformed activation parent metadata", poolConfig.Id);
        ValidateActivationTarget(parent.Response["bits"].Value<string>(), template.Bits,
            contract.Blake2bTargetShift!.Value, network == Network.Main ? 0x1d00ffffU : 0x207fffffU,
            poolConfig.Id);
        return new(template);
    }

    internal static BigInteger ValidateDifficultyForNetwork(double difficulty, Network selectedNetwork)
    {
        var target = BitcoinBlake2bHeader.TargetForDifficulty(difficulty);
        if(selectedNetwork != Network.RegTest && difficulty < 1)
            throw new ArgumentOutOfRangeException(nameof(difficulty),
                "Bitcoin BLAKE2b mainnet requires difficulty >= 1; easier targets are isolated-regtest only");
        return target;
    }

    internal BigInteger ValidateWorkerDifficulty(double difficulty) => ValidateDifficultyForNetwork(difficulty, network);

    internal static void ValidateActivationCoinbaseSize(PoolConfig pc, ClusterConfig cc, BitcoinBlake2bTemplate template)
    {
        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString)
            ? cc.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";
        var markerLength = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).Length;
        foreach(var contract in template.Networks.Values)
        {
            // Reserve the largest uint32 height and signed timestamp pushes,
            // OP_0, fixed extranonce bytes and the 128-bit job discriminator.
            // Pinned Knots 29.4.1 emits an empty coinbaseaux object. The exact
            // runtime guard also checks any unexpected daemon-supplied flags.
            var maximum = new Script(Op.GetPushOp((long) uint.MaxValue), Op.GetPushOp(long.MaxValue),
                Op.GetPushOp(0)).Length + BitcoinConstants.ExtranoncePlaceHolderLength +
                new Script(Op.GetPushOp(new byte[16])).Length + markerLength;
            if(!string.IsNullOrEmpty(contract.Blake2bActivationHeadline))
                maximum += new Script(Op.GetPushOp(Encoding.ASCII.GetBytes(contract.Blake2bActivationHeadline))).Length;
            if(maximum > 100)
                throw new PoolStartupException($"Pool '{pc.Id}' coinbaseString exceeds the Bitcoin BLAKE2b activation scriptSig budget ({maximum}/100 bytes); shorten it before startup", pc.Id);
        }
    }

    internal static void ValidateActivationTarget(string parentBits, string nextBits,
        byte shift, uint powLimitBits, string poolId)
    {
        try
        {
            var expected = BitcoinBlake2bHeader.ActivationTarget(
                BitcoinBlake2bHeader.ParseCompactBits(parentBits), shift, powLimitBits);
            if(expected != BitcoinBlake2bHeader.ParseCompactBits(nextBits))
                throw new InvalidDataException("activation target differs from the reviewed target-shift contract");
        }
        catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException)
        {
            // The shared update loop retries ordinary errors. A malformed
            // consensus contract instead must propagate to fail-stop and
            // invalidate previously issued work.
            throw new PoolStartupException($"Pool '{poolId}' rejected Bitcoin BLAKE2b activation metadata: {ex.Message}", poolId, ex);
        }
    }

    protected override void PostChainIdentifyConfigure()
    {
        if(network != NBitcoin.Network.Main && network != NBitcoin.Network.RegTest)
            throw new PoolStartupException($"Pool '{poolConfig.Id}' Bitcoin BLAKE2b supports only the reviewed mainnet and isolated regtest contracts", poolConfig.Id);
        try
        {
            foreach(var endpoint in poolConfig.Ports?.Values ?? Enumerable.Empty<PoolEndpoint>())
            {
                ValidateWorkerDifficulty(endpoint.Difficulty);
                if(endpoint.VarDiff != null)
                {
                    ValidateWorkerDifficulty(endpoint.VarDiff.MinDiff);
                    if(endpoint.VarDiff.MaxDiff.HasValue)
                        ValidateWorkerDifficulty(endpoint.VarDiff.MaxDiff.Value);
                }
            }
        }
        catch(ArgumentOutOfRangeException ex)
        {
            throw new PoolStartupException($"Pool '{poolConfig.Id}' has an unsupported Bitcoin BLAKE2b mainnet difficulty: {ex.Message}", poolConfig.Id, ex);
        }
        base.PostChainIdentifyConfigure();
    }

    public override ValueTask<Share> SubmitShareAsync(StratumConnection worker,
        object submission, CancellationToken ct)
    {
        if(submission is not object[] values || values.Length != 5 ||
           values.Any(x => x is not string))
            throw new StratumException(StratumError.Other,
                "Bitcoin BLAKE2b mining.submit requires exactly five string parameters");
        return base.SubmitShareAsync(worker, submission, ct);
    }

    protected override object[] GetBlockTemplateParams() =>
        new object[]
        {
            new
            {
                rules = new[] {"segwit", "blake2b"},
            },
        };

    protected override BitcoinJob CreateJob() => new BitcoinBlake2bJob();

    public override object[] GetSubscriberData(Stratum.StratumConnection worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();
        context.ExtraNonce1 = extraNonceProvider.Next();

        // Header-v2 keeps the four-byte connection discriminator but moves
        // the miner-controlled extranonce out of the coinbase and expands it
        // to the eight bytes required by the reviewed Sia-style wire contract.
        return new object[]
        {
            context.ExtraNonce1,
            BitcoinBlake2bHeader.MinerExtraNonceSize,
        };
    }

    protected override void InitializeJob(BitcoinJob job,
        BlockTemplate blockTemplate)
    {
        ValidateHeaderV2Template(blockTemplate);
        if(job is not BitcoinBlake2bJob blake2bJob)
            throw new InvalidOperationException(
                "Bitcoin BLAKE2b manager created an incompatible job type");

        try
        {
            blake2bJob.InitBlake2b(blockTemplate, NextJobId(), poolConfig,
            extraPoolConfig, clusterConfig, clock, poolAddressDestination,
            network, ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue);
        }
        catch(Exception ex) when(ex is not (OperationCanceledException or PoolStartupException))
        {
            // Parsing and serialization failures (including FormatException
            // and truncated-stream errors) must not enter the shared loop's
            // retry-and-retain-old-work path.
            throw new PoolStartupException($"Pool '{poolConfig.Id}' rejected Bitcoin BLAKE2b work: {ex.Message}", poolConfig.Id, ex);
        }
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        if(pc.Template is not BitcoinBlake2bTemplate template ||
           template.Family != CoinFamily.BitcoinBlake2b)
            throw new PoolStartupException(
                $"Pool '{pc.Id}' Bitcoin BLAKE2b runtime requires its isolated typed template",
                pc.Id);
        if(pc.PaymentProcessing != null && pc.PaymentProcessing.PayoutScheme is not
            (PayoutScheme.SOLO or PayoutScheme.PPS or PayoutScheme.PROP or PayoutScheme.PPLNS))
            throw new PoolStartupException(
                $"Pool '{pc.Id}' Bitcoin BLAKE2b supports only SOLO, PPS, PROP and PPLNS accounting",
                pc.Id);
        ValidateActivationCoinbaseSize(pc, cc, template);
        var unsupported = new[] { "gbtArgs", "btStream", "mergedMining", "hasLegacyDaemon", "coinbaseTxComment", "soloCoinbasePayout", "bip54Coinbase" };
        if(pc.Extra?.Keys.Any(x => unsupported.Contains(x,
               StringComparer.OrdinalIgnoreCase)) == true)
            throw new PoolStartupException(
                $"Pool '{pc.Id}' Bitcoin BLAKE2b requires its reviewed GBT/coinbase contract; custom GBT, streams, merged mining, transaction comments and canonical-Bitcoin coinbase options are unsupported",
                pc.Id);

        // Early representability check before RPC/network identification.
        // PostChainIdentifyConfigure additionally applies the mainnet floor.
        foreach(var endpoint in pc.Ports?.Values ?? Enumerable.Empty<PoolEndpoint>() )
        {
            try
            {
                BitcoinBlake2bHeader.TargetForDifficulty(endpoint.Difficulty);
                if(endpoint.VarDiff != null)
                {
                    BitcoinBlake2bHeader.TargetForDifficulty(endpoint.VarDiff.MinDiff);
                    if(endpoint.VarDiff.MaxDiff.HasValue)
                        BitcoinBlake2bHeader.TargetForDifficulty(endpoint.VarDiff.MaxDiff.Value);
                }
            }
            catch(ArgumentOutOfRangeException ex)
            {
                throw new PoolStartupException($"Pool '{pc.Id}' has an unrepresentable Bitcoin BLAKE2b share target", pc.Id, ex);
            }
        }

        blake2bCoin = template;
        base.Configure(pc, cc);

        if(DirectCoinbasePayoutEnabled)
            throw new PoolStartupException(
                $"Pool '{pc.Id}' Bitcoin BLAKE2b does not implement Bitcoin direct-coinbase SOLO settlement; omit soloCoinbasePayout",
                pc.Id);

        logger.Info(() => $"Bitcoin BLAKE2b protocol " +
            $"'{blake2bCoin.Blake2bProtocol}' enabled; BIP310 version " +
            "rolling and hasher time rolling are disabled");
    }

    private void ValidateHeaderV2Template(BlockTemplate template)
    {
        if(template == null)
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' Bitcoin BLAKE2b daemon returned an empty block template",
                poolConfig.Id);

        if(template.Rules == null ||
           !template.Rules.Contains("!blake2b", StringComparer.Ordinal))
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' requires an active compatible Bitcoin Knots daemon advertising mandatory GBT rule '!blake2b'",
                poolConfig.Id);

        if((template.Version & BitcoinBlake2bHeader.HeaderV2Flag) == 0)
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' daemon advertised '!blake2b' without the header-v2 version flag",
                poolConfig.Id);

        BitcoinTemplate.BitcoinNetworkParams networkContract;
        try
        {
            networkContract = blake2bCoin.GetNetwork(network.ChainName);
        }
        catch(Exception ex)
        {
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' does not support Bitcoin BLAKE2b daemon network '{network.ChainName}'",
                poolConfig.Id, ex);
        }

        if(networkContract?.Blake2bActivationHeight is not uint activation ||
           template.Height < activation)
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' refuses pre-activation Bitcoin BLAKE2b work at height {template.Height}",
                poolConfig.Id);

        if(template.Height == activation &&
           network.ChainName == NBitcoin.ChainName.Mainnet &&
           !string.Equals(networkContract.Blake2bActivationHeadline,
               "8-30 NYPost Deride And Conquer", StringComparison.Ordinal))
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' activation headline does not match the reviewed mainnet consensus value",
                poolConfig.Id);

        if(template.Transactions == null)
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' Bitcoin BLAKE2b daemon omitted the GBT transaction array",
                poolConfig.Id);

        try
        {
            var bits = BitcoinBlake2bHeader.ParseCompactBits(template.Bits);
            var compactTarget = BitcoinBlake2bHeader.DecodeCompactTarget(bits);
            var target = BitcoinBlake2bHeader.ParseDisplayTarget(template.Target);
            if(compactTarget != target)
                throw new InvalidDataException(
                    "GBT target does not equal its compact bits");
        }
        catch(Exception ex) when(ex is InvalidDataException or
            OverflowException or ArgumentException)
        {
            throw new PoolStartupException(
                $"Pool '{poolConfig.Id}' received malformed Bitcoin BLAKE2b target metadata: {ex.Message}",
                poolConfig.Id, ex);
        }
    }
}
