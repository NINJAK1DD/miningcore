from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def patch(path: str, old: str, new: str, count: int = 1) -> None:
    file = ROOT / path
    text = file.read_text()
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f"{path}: expected {count} occurrence(s), found {actual}: {old[:80]!r}")
    file.write_text(text.replace(old, new, count))


# Configuration wiring
patch(
    "src/Miningcore/Blockchain/Bitcoin/Configuration/BitcoinPoolConfigExtra.cs",
    "    public JToken GBTArgs { get; set; }\n}",
    "    public JToken GBTArgs { get; set; }\n\n"
    "    /// <summary>\n"
    "    /// Optional auxiliary proof-of-work configuration.\n"
    "    /// </summary>\n"
    "    public MergedMiningConfig MergedMining { get; set; }\n}"
)

# Per-connection auxiliary payout address
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinWorkerContext.cs",
    "    public string SessionId { get; set; }\n",
    "    public string SessionId { get; set; }\n\n"
    "    /// <summary>\n"
    "    /// Payout address on the auxiliary chain for merged-mined SOLO blocks.\n"
    "    /// </summary>\n"
    "    public string AuxAddress { get; set; }\n"
)

# Transient auxiliary candidate/submission data. These fields never enter protobuf share transport.
patch(
    "src/Miningcore/Blockchain/Share.cs",
    "    [ProtoMember(15)]\n    public DateTime Created { get; set; }\n}",
    "    [ProtoMember(15)]\n    public DateTime Created { get; set; }\n\n"
    "    [ProtoIgnore]\n    public bool IsAuxBlockCandidate { get; set; }\n\n"
    "    [ProtoIgnore]\n    public string AuxBlockHash { get; set; }\n\n"
    "    [ProtoIgnore]\n    public long AuxBlockHeight { get; set; }\n\n"
    "    [ProtoIgnore]\n    public double AuxNetworkDifficulty { get; set; }\n\n"
    "    [ProtoIgnore]\n    public string AuxPow { get; set; }\n\n"
    "    [ProtoIgnore]\n    public Share AuxiliaryShare { get; set; }\n}"
)

# Parse/validate doge= in mining.authorize and publish the accepted DOGE block record.
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinPool.cs",
    "        context.Miner = minerName;\n        context.Worker = workerName;\n\n        if(context.IsAuthorized)\n",
    "        context.Miner = minerName;\n        context.Worker = workerName;\n\n"
    "        if(context.IsAuthorized && manager.MergedMiningEnabled)\n"
    "        {\n"
    "            context.AuxAddress = manager.GetAuxAddress(passParts);\n\n"
    "            if(string.IsNullOrEmpty(context.AuxAddress))\n"
    "                context.IsAuthorized = !manager.RequireAuxAddress;\n"
    "            else\n"
    "                context.IsAuthorized = await manager.ValidateAuxAddressAsync(context.AuxAddress, ct);\n"
    "        }\n\n"
    "        if(context.IsAuthorized)\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinPool.cs",
    "            // publish\n            messageBus.SendMessage(share);\n\n            // telemetry\n",
    "            // publish parent-chain share\n            messageBus.SendMessage(share);\n\n"
    "            // A successful auxiliary submission is persisted as a block-only share under\n"
    "            // the auxiliary pool id. This reuses its existing SOLO classification/payout path.\n"
    "            if(share.AuxiliaryShare != null)\n"
    "            {\n"
    "                share.AuxiliaryShare.SessionId = context.SessionId;\n"
    "                messageBus.SendMessage(share.AuxiliaryShare);\n"
    "            }\n\n"
    "            // telemetry\n"
)

# Job manager: auxiliary RPC lifecycle, template refresh, submission and address validation.
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "    private BitcoinTemplate coin;\n",
    "    private BitcoinTemplate coin;\n"
    "    private MergedMiningConfig mergedMiningConfig;\n"
    "    private PoolConfig auxPoolConfig;\n"
    "    private RpcClient auxRpc;\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "    private BitcoinJob CreateJob()\n    {\n        return new();\n    }\n",
    "    private BitcoinJob CreateJob()\n    {\n        return MergedMiningEnabled ? new MergedMiningBitcoinJob() : new BitcoinJob();\n    }\n\n"
    "    private async Task<RpcResponse<AuxBlockTemplate>> GetAuxBlockTemplateAsync(CancellationToken ct)\n"
    "    {\n"
    "        return await auxRpc.ExecuteAsync<AuxBlockTemplate>(logger, \"createauxblock\", ct,\n"
    "            new[] { auxPoolConfig.Address });\n"
    "    }\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "            var blockTemplate = response.Response;\n            var job = currentJob;\n\n            var isNew = job == null ||\n",
    "            var blockTemplate = response.Response;\n            var job = currentJob;\n            AuxBlockTemplate auxBlockTemplate = null;\n\n"
    "            if(MergedMiningEnabled)\n"
    "            {\n"
    "                var auxResponse = await GetAuxBlockTemplateAsync(ct);\n"
    "                if(auxResponse.Error != null || auxResponse.Response == null)\n"
    "                {\n"
    "                    logger.Warn(() => $\"Unable to update auxiliary job. Daemon responded with: {auxResponse.Error?.Message}\");\n"
    "                    return (false, forceUpdate);\n"
    "                }\n\n"
    "                auxBlockTemplate = auxResponse.Response;\n"
    "            }\n\n"
    "            var isNew = job == null ||\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "                        blockTemplate.Height > job.BlockTemplate?.Height));\n",
    "                        blockTemplate.Height > job.BlockTemplate?.Height ||\n"
    "                        job is MergedMiningBitcoinJob mergedJob &&\n"
    "                        mergedJob.AuxBlockTemplate?.Hash != auxBlockTemplate?.Hash));\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "                job.Init(blockTemplate, NextJobId(),\n                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,\n                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,\n                    !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);\n",
    "                if(job is MergedMiningBitcoinJob mergedJob)\n"
    "                {\n"
    "                    mergedJob.InitMerged(blockTemplate, auxBlockTemplate, NextJobId(),\n"
    "                        poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,\n"
    "                        ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,\n"
    "                        !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);\n"
    "                }\n"
    "                else\n"
    "                {\n"
    "                    job.Init(blockTemplate, NextJobId(),\n"
    "                        poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,\n"
    "                        ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue,\n"
    "                        !isPoS ? coin.BlockHasherValue : coin.PoSBlockHasherValue ?? coin.BlockHasherValue);\n"
    "                }\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();\n\n        if(extraPoolConfig?.MaxActiveJobs.HasValue == true)\n",
    "        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<BitcoinPoolPaymentProcessingConfigExtra>();\n"
    "        mergedMiningConfig = extraPoolConfig?.MergedMining;\n\n"
    "        if(mergedMiningConfig?.Enabled == true)\n"
    "        {\n"
    "            if(string.IsNullOrWhiteSpace(mergedMiningConfig.AuxPoolId))\n"
    "                throw new PoolStartupException(\"Merged mining requires extra.mergedMining.auxPoolId\", pc.Id);\n\n"
    "            auxPoolConfig = cc.Pools.FirstOrDefault(x => x.Id == mergedMiningConfig.AuxPoolId);\n"
    "            if(auxPoolConfig == null)\n"
    "                throw new PoolStartupException($\"Auxiliary pool '{mergedMiningConfig.AuxPoolId}' was not found\", pc.Id);\n"
    "            if(auxPoolConfig.Daemons == null || auxPoolConfig.Daemons.Length == 0)\n"
    "                throw new PoolStartupException($\"Auxiliary pool '{mergedMiningConfig.AuxPoolId}' has no daemon configured\", pc.Id);\n"
    "            if(string.IsNullOrWhiteSpace(auxPoolConfig.Address))\n"
    "                throw new PoolStartupException($\"Auxiliary pool '{mergedMiningConfig.AuxPoolId}' has no pool address configured\", pc.Id);\n\n"
    "            var serializerSettings = ctx.Resolve<JsonSerializerSettings>();\n"
    "            auxRpc = new RpcClient(auxPoolConfig.Daemons.First(), serializerSettings, messageBus, auxPoolConfig.Id);\n"
    "        }\n\n"
    "        if(extraPoolConfig?.MaxActiveJobs.HasValue == true)\n"
)
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJobManager.cs",
    "        return share;\n    }\n\n    public double ShareMultiplier => coin.ShareMultiplier;\n",
    "        if(MergedMiningEnabled && share.IsAuxBlockCandidate && !string.IsNullOrEmpty(context.AuxAddress))\n"
    "        {\n"
    "            logger.Info(() => $\"Submitting auxiliary block {share.AuxBlockHeight} [{share.AuxBlockHash}]\");\n"
    "            var auxResponse = await auxRpc.ExecuteAsync<JToken>(logger, \"submitauxblock\", ct,\n"
    "                new[] { share.AuxBlockHash, share.AuxPow });\n"
    "            var accepted = auxResponse.Error == null && auxResponse.Response?.Value<bool>() == true;\n\n"
    "            if(accepted)\n"
    "            {\n"
    "                logger.Info(() => $\"Auxiliary daemon accepted block {share.AuxBlockHeight} [{share.AuxBlockHash}] submitted by {context.AuxAddress}\");\n"
    "                share.AuxiliaryShare = new Share\n"
    "                {\n"
    "                    PoolId = auxPoolConfig.Id,\n"
    "                    Miner = context.AuxAddress,\n"
    "                    Worker = context.Worker,\n"
    "                    UserAgent = context.UserAgent,\n"
    "                    IpAddress = worker.RemoteEndpoint.Address.ToString(),\n"
    "                    Source = clusterConfig.ClusterName,\n"
    "                    Difficulty = share.Difficulty,\n"
    "                    ShareDifficulty = share.ShareDifficulty,\n"
    "                    ActualDifficulty = share.ActualDifficulty,\n"
    "                    BlockHeight = share.AuxBlockHeight,\n"
    "                    BlockHash = share.AuxBlockHash,\n"
    "                    IsBlockCandidate = true,\n"
    "                    NetworkDifficulty = share.AuxNetworkDifficulty,\n"
    "                    Created = share.Created\n"
    "                };\n"
    "            }\n"
    "            else\n"
    "                logger.Warn(() => $\"Auxiliary block {share.AuxBlockHeight} submission failed: {auxResponse.Error?.Message ?? auxResponse.Response?.ToString()}\");\n"
    "        }\n\n"
    "        return share;\n"
    "    }\n\n"
    "    public bool MergedMiningEnabled => mergedMiningConfig?.Enabled == true;\n"
    "    public bool RequireAuxAddress => mergedMiningConfig?.RequireAuxAddress != false;\n\n"
    "    public string GetAuxAddress(IEnumerable<string> passParts)\n"
    "    {\n"
    "        if(!MergedMiningEnabled || passParts == null)\n"
    "            return null;\n\n"
    "        var key = string.IsNullOrWhiteSpace(mergedMiningConfig.AddressParameter) ? \"doge\" : mergedMiningConfig.AddressParameter.Trim();\n"
    "        var prefix = key + \"=\";\n"
    "        return passParts.FirstOrDefault(x => x?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)?[prefix.Length..].Trim();\n"
    "    }\n\n"
    "    public async Task<bool> ValidateAuxAddressAsync(string address, CancellationToken ct)\n"
    "    {\n"
    "        if(!MergedMiningEnabled || string.IsNullOrWhiteSpace(address))\n"
    "            return false;\n\n"
    "        var result = await auxRpc.ExecuteAsync<ValidateAddressResponse>(logger, BitcoinCommands.ValidateAddress, ct, new[] { address });\n"
    "        return result.Response is { IsValid: true };\n"
    "    }\n\n"
    "    public double ShareMultiplier => coin.ShareMultiplier;\n"
)

# New AuxPoW model and implementation files.
(ROOT / "src/Miningcore/Blockchain/Bitcoin/DaemonResponses/AuxBlockTemplate.cs").write_text('''using Newtonsoft.Json;\n\nnamespace Miningcore.Blockchain.Bitcoin.DaemonResponses;\n\npublic class AuxBlockTemplate\n{\n    public string Hash { get; set; }\n    public int ChainId { get; set; }\n    public string PreviousBlockHash { get; set; }\n    public long CoinbaseValue { get; set; }\n    public string Bits { get; set; }\n    public long Height { get; set; }\n\n    [JsonProperty("target")]\n    public string Target { get; set; }\n\n    [JsonProperty("_target")]\n    private string LegacyTarget { set { if(string.IsNullOrEmpty(Target)) Target = value; } }\n}\n''')

(ROOT / "src/Miningcore/Blockchain/Bitcoin/MergedMiningAuxPow.cs").write_text('''using System.Buffers.Binary;\nusing Miningcore.Extensions;\n\nnamespace Miningcore.Blockchain.Bitcoin;\n\ninternal static class MergedMiningAuxPow\n{\n    private static readonly byte[] Header = { 0xfa, 0xbe, 0x6d, 0x6d };\n\n    public static byte[] BuildCoinbaseCommitment(string auxBlockHash)\n    {\n        var hash = auxBlockHash.HexToByteArray();\n        if(hash.Length != 32)\n            throw new ArgumentException("Auxiliary block hash must be 32 bytes", nameof(auxBlockHash));\n\n        var result = new byte[44];\n        Header.CopyTo(result, 0);\n        hash.CopyTo(result, 4);\n        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);\n        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), 0);\n        return result;\n    }\n\n    public static byte[] Serialize(byte[] parentCoinbase, byte[] parentHeader, IReadOnlyList<byte[]> coinbaseMerkleBranch)\n    {\n        using var stream = new MemoryStream();\n        stream.Write(parentCoinbase);\n\n        // CMerkleTx::hashBlock is not consulted by CAuxPow::check. Keep it null.\n        stream.Write(new byte[32]);\n        WriteCompactSize(stream, (ulong) coinbaseMerkleBranch.Count);\n        foreach(var hash in coinbaseMerkleBranch)\n        {\n            if(hash.Length != 32)\n                throw new ArgumentException("Merkle branch hashes must be 32 bytes", nameof(coinbaseMerkleBranch));\n            stream.Write(hash);\n        }\n\n        WriteInt32(stream, 0);     // coinbase index in parent block\n        WriteCompactSize(stream, 0); // single auxiliary chain: empty chain-merkle branch\n        WriteInt32(stream, 0);     // chain index\n        stream.Write(parentHeader);\n        return stream.ToArray();\n    }\n\n    private static void WriteInt32(Stream stream, int value)\n    {\n        Span<byte> buffer = stackalloc byte[4];\n        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);\n        stream.Write(buffer);\n    }\n\n    private static void WriteCompactSize(Stream stream, ulong value)\n    {\n        Span<byte> buffer = stackalloc byte[9];\n        int length;\n        if(value < 253)\n        {\n            buffer[0] = (byte) value;\n            length = 1;\n        }\n        else if(value <= ushort.MaxValue)\n        {\n            buffer[0] = 253;\n            BinaryPrimitives.WriteUInt16LittleEndian(buffer[1..], (ushort) value);\n            length = 3;\n        }\n        else if(value <= uint.MaxValue)\n        {\n            buffer[0] = 254;\n            BinaryPrimitives.WriteUInt32LittleEndian(buffer[1..], (uint) value);\n            length = 5;\n        }\n        else\n        {\n            buffer[0] = 255;\n            BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], value);\n            length = 9;\n        }\n\n        stream.Write(buffer[..length]);\n    }\n}\n''')

(ROOT / "src/Miningcore/Blockchain/Bitcoin/MergedMiningBitcoinJob.cs").write_text('''using Miningcore.Blockchain.Bitcoin.Configuration;\nusing Miningcore.Blockchain.Bitcoin.DaemonResponses;\nusing Miningcore.Configuration;\nusing Miningcore.Crypto;\nusing Miningcore.Extensions;\nusing Miningcore.Stratum;\nusing Miningcore.Time;\nusing Miningcore.Util;\nusing NBitcoin;\n\nnamespace Miningcore.Blockchain.Bitcoin;\n\npublic class MergedMiningBitcoinJob : BitcoinJob\n{\n    private uint256 auxTargetValue;\n\n    public AuxBlockTemplate AuxBlockTemplate { get; private set; }\n\n    public void InitMerged(BlockTemplate blockTemplate, AuxBlockTemplate auxBlockTemplate, string jobId,\n        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig, ClusterConfig cc, IMasterClock clock,\n        IDestination poolAddressDestination, Network network, bool isPoS, double shareMultiplier,\n        IHashAlgorithm coinbaseHasher, IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)\n    {\n        AuxBlockTemplate = auxBlockTemplate ?? throw new ArgumentNullException(nameof(auxBlockTemplate));\n        if(string.IsNullOrWhiteSpace(auxBlockTemplate.Target))\n            throw new ArgumentException("Auxiliary template did not provide a target", nameof(auxBlockTemplate));\n\n        // Dogecoin createauxblock returns target bytes in little-endian order.\n        auxTargetValue = new uint256(auxBlockTemplate.Target.HexToByteArray());\n        base.Init(blockTemplate, jobId, pc, extraPoolConfig, cc, clock, poolAddressDestination, network,\n            isPoS, shareMultiplier, coinbaseHasher, headerHasher, blockHasher);\n    }\n\n    protected override Script GenerateScriptSigInitial()\n    {\n        var parent = base.GenerateScriptSigInitial().ToBytes();\n        var commitment = Op.GetPushOp(MergedMiningAuxPow.BuildCoinbaseCommitment(AuxBlockTemplate.Hash)).ToBytes();\n        return new Script(parent.Concat(commitment).ToArray());\n    }\n\n    protected override (Share Share, string BlockHex) ProcessShareInternal(\n        StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)\n    {\n        var context = worker.ContextAs<BitcoinWorkerContext>();\n        var coinbase = SerializeCoinbase(context.ExtraNonce1, extraNonce2);\n        Span<byte> coinbaseHash = stackalloc byte[32];\n        coinbaseHasher.Digest(coinbase, coinbaseHash);\n\n        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);\n        Span<byte> headerHash = stackalloc byte[32];\n        headerHasher.Digest(headerBytes, headerHash, (ulong) nTime, BlockTemplate, coin, networkParams);\n        var headerValue = new uint256(headerHash);\n\n        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;\n        var stratumDifficulty = context.Difficulty;\n        var ratio = shareDiff / stratumDifficulty;\n        var isBlockCandidate = headerValue <= blockTargetValue;\n\n        if(!isBlockCandidate && ratio < 0.99)\n        {\n            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)\n            {\n                ratio = shareDiff / context.PreviousDifficulty.Value;\n                if(ratio < 0.99)\n                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");\n                stratumDifficulty = context.PreviousDifficulty.Value;\n            }\n            else\n                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");\n        }\n\n        var result = new Share\n        {\n            BlockHeight = BlockTemplate.Height,\n            NetworkDifficulty = Difficulty,\n            Difficulty = stratumDifficulty / shareMultiplier,\n            ShareDifficulty = shareDiff,\n            ActualDifficulty = shareDiff / shareMultiplier,\n        };\n\n        if(headerValue <= auxTargetValue)\n        {\n            result.IsAuxBlockCandidate = true;\n            result.AuxBlockHash = AuxBlockTemplate.Hash;\n            result.AuxBlockHeight = AuxBlockTemplate.Height;\n            result.AuxNetworkDifficulty = new Target(AuxBlockTemplate.Bits.HexToByteArray()).Difficulty;\n            result.AuxPow = MergedMiningAuxPow.Serialize(coinbase, headerBytes, mt.Steps).ToHexString();\n        }\n\n        if(isBlockCandidate)\n        {\n            result.IsBlockCandidate = true;\n            Span<byte> blockHash = stackalloc byte[32];\n            blockHasher.Digest(headerBytes, blockHash, nTime);\n            result.BlockHash = blockHash.ToHexString();\n            return (result, SerializeBlock(headerBytes, coinbase).ToHexString());\n        }\n\n        return (result, null);\n    }\n}\n''')

# networkParams is required by the subclass and is immutable after Init.
patch(
    "src/Miningcore/Blockchain/Bitcoin/BitcoinJob.cs",
    "    private BitcoinTemplate.BitcoinNetworkParams networkParams;\n",
    "    protected BitcoinTemplate.BitcoinNetworkParams networkParams;\n"
)

# Unit tests for commitment byte order and AuxPoW framing.
(ROOT / "src/Miningcore.Tests/Blockchain/Bitcoin/MergedMiningAuxPowTests.cs").write_text('''using Miningcore.Blockchain.Bitcoin;\nusing Miningcore.Extensions;\nusing Xunit;\n\nnamespace Miningcore.Tests.Blockchain.Bitcoin;\n\npublic class MergedMiningAuxPowTests\n{\n    [Fact]\n    public void CoinbaseCommitmentUsesDogecoinWireFormat()\n    {\n        const string hash = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";\n        var result = MergedMiningAuxPow.BuildCoinbaseCommitment(hash).ToHexString();\n        Assert.Equal("fabe6d6d" + hash + "0100000000000000", result);\n    }\n\n    [Fact]\n    public void SerializesSingleChainAuxPow()\n    {\n        var coinbase = new byte[] { 1, 2, 3 };\n        var branch = Enumerable.Repeat((byte) 0x11, 32).ToArray();\n        var header = Enumerable.Repeat((byte) 0x22, 80).ToArray();\n        var result = MergedMiningAuxPow.Serialize(coinbase, header, new[] { branch });\n\n        Assert.Equal(3 + 32 + 1 + 32 + 4 + 1 + 4 + 80, result.Length);\n        Assert.Equal(1, result[35]);\n        Assert.Equal(branch, result.Skip(36).Take(32));\n        Assert.Equal(header, result.TakeLast(80));\n    }\n}\n''')

print("Merged-mining patch applied successfully")
