using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJob
{
    internal const long BitcoinConsensusMaxBlockWeight = 4_000_000;
    protected IHashAlgorithm blockHasher;
    protected IMasterClock clock;
    protected IHashAlgorithm coinbaseHasher;
    protected double shareMultiplier;
    protected int extraNoncePlaceHolderLength;
    protected IHashAlgorithm headerHasher;
    protected bool isPoS;
    protected string txComment;
    protected PayeeBlockTemplateExtra payeeParameters;
    protected byte[] mwebPayload;

    protected Network network;
    protected IDestination poolAddressDestination;
    protected BitcoinTemplate coin;
    private BitcoinTemplate.BitcoinNetworkParams networkParams;
    protected readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    protected uint256 blockTargetValue;
    protected byte[] coinbaseFinal;
    protected string coinbaseFinalHex;
    protected byte[] coinbaseInitial;
    protected string coinbaseInitialHex;
    protected string[] merkleBranchesHex;
    protected MerkleTree mt;
    protected string[] merkleSegwitBranchesHex;
    protected MerkleTree mtSegwit;

    ///////////////////////////////////////////
    // GetJobParams related properties

    protected object[] jobParams;
    protected string previousBlockHashReversedHex;
    protected Money rewardToPool;
    protected Transaction txOut;
    private BitcoinDirectCoinbaseTemplate directCoinbaseTemplate;

    // serialization constants
    protected byte[] scriptSigFinalBytes;

    protected static byte[] sha256Empty = new byte[32];
    protected uint txVersion = 1u; // transaction version (currently 1) - see https://en.bitcoin.it/wiki/Transaction

    protected static uint txInputCount = 1u;
    protected static uint txInPrevOutIndex = (uint) (Math.Pow(2, 32) - 1);
    protected uint txInSequence;
    protected uint txLockTime;
    private bool emitBip54CoinbaseFields;
    private bool witnessCommitmentLast;

    // CKPool and AxeOS use 0xfffffffe for their BIP 54-compatible shape.
    private const uint Bip54CoinbaseSequence = uint.MaxValue - 1;

    protected virtual void BuildMerkleBranches()
    {
        var transactionHashes = BlockTemplate.Transactions
            .Select(tx => (tx.TxId ?? tx.Hash)
                .HexToByteArray()
                .ReverseInPlace())
            .ToArray();

        mt = new MerkleTree(transactionHashes);

        merkleBranchesHex = mt.Steps
            .Select(x => x.ToHexString())
            .ToArray();
    }

    protected virtual MerkleTree BuildSegwitMerkleBranches()
    {
        var segwitTransactionHashes = BlockTemplate.Transactions
            .Where(tx => IsSegWitTransaction(tx))
            .Select(tx => (tx.TxId ?? tx.Hash)
                .HexToByteArray()
                .ReverseInPlace())
            .ToArray();
        // Build Merkle Tree with SegWit transactions
        return new MerkleTree(segwitTransactionHashes);
    }

    protected virtual bool IsSegWitTransaction(BitcoinBlockTransaction tx)
    {
        // Convert hex string to byte array
        byte[] txBytes = tx.Data.HexToByteArray();
        // Convert byte array to hex string
        string hexString = txBytes.ToHexString();
        // Parse the transaction using NBitcoin
        var transaction = Transaction.Parse(hexString, network);
        return transaction.HasWitness;
    }

    protected virtual void BuildCoinbase()
    {
        // generate script parts
        var sigScriptInitial = GenerateScriptSigInitial();
        var sigScriptInitialBytes = sigScriptInitial.ToBytes();

        var sigScriptLength = (uint) (
            sigScriptInitial.Length +
            extraNoncePlaceHolderLength +
            scriptSigFinalBytes.Length);

        // output transaction
        txOut = CreateOutputTransaction();

        // build coinbase initial
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // version
            bs.ReadWrite(ref txVersion);

            // timestamp for POS coins
            if(isPoS)
            {
                var timestamp = BlockTemplate.CurTime;
                bs.ReadWrite(ref timestamp);
            }

            // serialize (simulated) input transaction
            bs.ReadWriteAsVarInt(ref txInputCount);
            bs.ReadWrite(sha256Empty);
            bs.ReadWrite(ref txInPrevOutIndex);

            // signature script initial part
            bs.ReadWriteAsVarInt(ref sigScriptLength);
            bs.ReadWrite(sigScriptInitialBytes);

            // done
            coinbaseInitial = stream.ToArray();
            coinbaseInitialHex = coinbaseInitial.ToHexString();
        }

        // build coinbase final
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // signature script final part
            bs.ReadWrite(scriptSigFinalBytes);

            // tx in sequence
            bs.ReadWrite(ref txInSequence);

            // serialize output transaction
            var txOutBytes = SerializeOutputTransaction(txOut);
            bs.ReadWrite(txOutBytes);

            // misc
            bs.ReadWrite(ref txLockTime);

            // Extension point
            AppendCoinbaseFinal(bs);

            // done
            coinbaseFinal = stream.ToArray();
            coinbaseFinalHex = coinbaseFinal.ToHexString();
        }
    }

    protected virtual void AppendCoinbaseFinal(BitcoinStream bs)
    {
        if(!string.IsNullOrEmpty(txComment))
        {
            var data = Encoding.ASCII.GetBytes(txComment);
            bs.ReadWriteAsVarString(ref data);
        }

        if(coin.HasMasterNodes && !string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
        {
            var data = masterNodeParameters.CoinbasePayload.HexToByteArray();
            bs.ReadWriteAsVarString(ref data);
        }
    }

    protected virtual byte[] SerializeOutputTransaction(Transaction tx)
    {
        var withDefaultWitnessCommitment = !string.IsNullOrEmpty(BlockTemplate.DefaultWitnessCommitment);

        var outputCount = (uint) tx.Outputs.Count;
        if(withDefaultWitnessCommitment)
            outputCount++;

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // write output count
            bs.ReadWriteAsVarInt(ref outputCount);

            // Preserve the established layout for other Bitcoin-family chains.
            // Canonical Bitcoin puts value-bearing outputs first and the BIP141
            // witness commitment last, matching CKPool's operator-facing layout.
            if(withDefaultWitnessCommitment && !witnessCommitmentLast)
                SerializeDefaultWitnessCommitment(bs);

            // serialize outputs
            foreach(var output in tx.Outputs)
            {
                var amount = output.Value.Satoshi;
                var outScript = output.ScriptPubKey;
                var raw = outScript.ToBytes(true);
                var rawLength = (uint) raw.Length;

                bs.ReadWrite(ref amount);
                bs.ReadWriteAsVarInt(ref rawLength);
                bs.ReadWrite(raw);
            }

            if(withDefaultWitnessCommitment && witnessCommitmentLast)
                SerializeDefaultWitnessCommitment(bs);

            return stream.ToArray();
        }
    }

    private void SerializeDefaultWitnessCommitment(BitcoinStream bs)
    {
        long amount = 0;
        var raw = BlockTemplate.DefaultWitnessCommitment.HexToByteArray();

        if(coin.Symbol == "ANOK" || coin.Symbol == "RVH")
        {
            // Compute witness commitment
            byte[] witnessRoot = raw;
            byte[] witnessNonce = new byte[32];

            // Concatenate witness root and nonce
            Span<byte> witnessRootAndNonce = stackalloc byte[witnessRoot.Length + witnessNonce.Length];
            witnessRoot.CopyTo(witnessRootAndNonce);
            witnessNonce.CopyTo(witnessRootAndNonce[witnessRoot.Length..]);

            // Generate SHA256^2 hash
            Sha256D sha256DHasher = new Sha256D();
            byte[] hash = new byte[32];
            sha256DHasher.Digest(witnessRootAndNonce, hash);

            // Create scriptPubKey
            byte[] magic = { 0xaa, 0x21, 0xa9, 0xed };
            Span<byte> scriptPubKey = stackalloc byte[magic.Length + hash.Length];
            magic.CopyTo(scriptPubKey);
            hash.CopyTo(scriptPubKey[magic.Length..]);

            raw = scriptPubKey.ToArray();
        }

        var rawLength = (uint) raw.Length;
        bs.ReadWrite(ref amount);
        bs.ReadWriteAsVarInt(ref rawLength);
        bs.ReadWrite(raw);
    }

    protected virtual Script GenerateScriptSigInitial()
    {
        var now = ((DateTimeOffset) clock.Now).ToUnixTimeSeconds();

        // script ops
        var ops = new List<Op>();

        // push block height
        ops.Add(Op.GetPushOp(BlockTemplate.Height));

        // optionally push aux-flags
        if(!coin.CoinbaseIgnoreAuxFlags && !string.IsNullOrEmpty(BlockTemplate.CoinbaseAux?.Flags))
            ops.Add(Op.GetPushOp(BlockTemplate.CoinbaseAux.Flags.HexToByteArray()));

        // push timestamp
        ops.Add(Op.GetPushOp(now));

        // push placeholder
        ops.Add(Op.GetPushOp(0));

        return new Script(ops);
    }

    protected virtual Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        if(coin.HasPayee)
            rewardToPool = CreatePayeeOutput(tx, rewardToPool);

        if(coin.HasMasterNodes)
            rewardToPool = CreateMasternodeOutputs(tx, rewardToPool);

        if (coin.HasFounderFee)
            rewardToPool = CreateFounderOutputs(tx, rewardToPool);

        if(coin.HasFortuneReward)
            rewardToPool = CreateFortuneOutputs(tx, rewardToPool);

        if (coin.HasMinerFund)
            rewardToPool = CreateMinerFundOutputs(tx, rewardToPool);

        if(coin.HasCommunityAddress)
            rewardToPool = CreateCommunityAddressOutputs(tx, rewardToPool);

        if(coin.HasCoinbaseDevReward)
            rewardToPool = CreateCoinbaseDevRewardOutputs(tx, rewardToPool);

        if(coin.HasCoinbaseStakingReward)
            rewardToPool = CreateCoinbaseStakingRewardOutputs(tx, rewardToPool);

        if(coin.HasCommunity)
            rewardToPool = CreateCommunityOutputs(tx, rewardToPool);

        if(coin.HasDataMining)
            rewardToPool = CreateDataMiningOutputs(tx, rewardToPool);

        if(coin.HasDeveloper)
            rewardToPool = CreateDeveloperOutputs(tx, rewardToPool);

        if(directCoinbaseTemplate == null)
        {
            // Remaining amount goes to pool
            tx.Outputs.Add(rewardToPool, poolAddressDestination);
        }
        else
        {
            if(rewardToPool.Satoshi != BlockTemplate.CoinbaseValue)
                throw new InvalidDataException(
                    "Direct SOLO coinbase payout is restricted to canonical Bitcoin templates without additional consensus-owned value outputs");
            DirectCoinbaseSettlement = BitcoinDirectCoinbase.Split(
                rewardToPool.Satoshi, directCoinbaseTemplate);

            // Miner first, followed by a canonical script-sorted recipient list.
            // Canonical Bitcoin serialization appends the BIP141 commitment afterward.
            tx.Outputs.Add(new Money(
                    DirectCoinbaseSettlement.MinerRewardSatoshis,
                    MoneyUnit.Satoshi),
                directCoinbaseTemplate.MinerDestination);

            foreach(var output in DirectCoinbaseSettlement.RecipientOutputs)
            {
                var recipient = directCoinbaseTemplate.Recipients.Single(x =>
                    string.Equals(x.ScriptPubKey, output.ScriptPubKey,
                        StringComparison.OrdinalIgnoreCase));
                tx.Outputs.Add(new Money(output.AmountSatoshis,
                        MoneyUnit.Satoshi),
                    recipient.Destination);
            }
        }

        return tx;
    }

    protected virtual Money CreatePayeeOutput(Transaction tx, Money reward)
    {
        if(payeeParameters?.PayeeAmount != null && payeeParameters.PayeeAmount.Value > 0)
        {
            var payeeReward = new Money(payeeParameters.PayeeAmount.Value, MoneyUnit.Satoshi);
            reward -= payeeReward;

            tx.Outputs.Add(payeeReward, BitcoinUtils.AddressToDestination(payeeParameters.Payee, network));
        }

        return reward;
    }

    protected bool RegisterSubmit(string extraNonce1, string extraNonce2, string nTime,
        string nonce, uint? versionBits)
    {
        var key = new StringBuilder()
            .Append(extraNonce1)
            .Append(':')
            .Append(extraNonce2.ToLowerInvariant())
            .Append(':')
            .Append(nTime.ToLowerInvariant())
            .Append(':')
            .Append(nonce.ToLowerInvariant())
            .Append(':')
            .Append(versionBits?.ToString("x8", CultureInfo.InvariantCulture) ?? "-")
            .ToString();

        return submissions.TryAdd(key, true);
    }

    protected static uint? ParseVersionBits(uint? versionMask, string versionBits)
    {
        // BIP310 makes version_bits part of mining.submit whenever version-rolling was
        // negotiated. Keeping null distinct from an explicit zero is important: null keeps
        // every template-version bit, while 00000000 deliberately clears the negotiated bits.
        if(!versionMask.HasValue)
            return null;

        if(string.IsNullOrEmpty(versionBits))
            throw new StratumException(StratumError.Other, "missing version_bits");

        if(versionBits.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of version_bits");

        if(!uint.TryParse(versionBits, NumberStyles.AllowHexSpecifier,
               CultureInfo.InvariantCulture, out var result))
            throw new StratumException(StratumError.Other, "invalid version_bits");

        if((result & ~versionMask.Value) != 0)
            throw new StratumException(StratumError.Other, "rolling-version mask violation");

        return result;
    }

    protected byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version
        var version = ApplyVersionRolling(BlockTemplate.Version, versionMask,
            versionBits);

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(nTime),
            Nonce = nonce
        };

            return blockHeader.ToBytes();
    }

    internal static uint ApplyVersionRolling(uint templateVersion,
        uint? versionMask, uint? versionBits)
    {
        if(!versionMask.HasValue || !versionBits.HasValue)
            return templateVersion;

        return (templateVersion & ~versionMask.Value) |
            (versionBits.Value & versionMask.Value);
    }

    protected virtual (Share Share, string BlockHex) ProcessShareInternal(
        StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // hash block-header
        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash, (ulong) nTime, BlockTemplate, coin, networkParams);
        var headerValue = new uint256(headerHash);

        // calc share-diff
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = headerValue <= blockTargetValue;

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var actualShareDifficulty = shareDiff / shareMultiplier;

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
            ShareDifficulty = shareDiff,
            ActualDifficulty = actualShareDifficulty,
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            if(DirectCoinbaseSettlement != null)
            {
                result.SettlementMode =
                    BitcoinDirectCoinbaseSettlement.Mode;
                result.GrossRewardSatoshis =
                    DirectCoinbaseSettlement.GrossRewardSatoshis;
                result.DirectMinerRewardSatoshis =
                    DirectCoinbaseSettlement.MinerRewardSatoshis;
                result.DirectMinerScriptPubKey =
                    DirectCoinbaseSettlement.MinerScriptPubKey;
                result.DirectRecipientOutputs =
                    DirectCoinbaseSettlement.SerializeRecipientOutputs();
                result.TransactionConfirmationData =
                    new uint256(coinbaseHash).ToString();
            }

            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, nTime);
            result.BlockHash = blockHash.ToHexString();

            var blockBytes = SerializeBlock(headerBytes, coinbase);
            var blockHex = blockBytes.ToHexString();

            return (result, blockHex);
        }

        return (result, null);
    }

    protected virtual byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var extraNonce2Bytes = extraNonce2.HexToByteArray();

        using(var stream = new MemoryStream())
        {
            stream.Write(coinbaseInitial);
            stream.Write(extraNonce1Bytes);
            stream.Write(extraNonce2Bytes);
            stream.Write(coinbaseFinal);

            return stream.ToArray();
        }
    }

    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for prepended coinbase tx

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            bs.ReadWrite(header);
            bs.ReadWriteAsVarInt(ref transactionCount);

            bs.ReadWrite(coinbase);
            bs.ReadWrite(rawTransactionBuffer);

            // POS coins require a zero byte appended to block which the daemon replaces with the signature
            if(isPoS)
                bs.ReadWrite((byte) 0);

            // MWEB-capable daemons require the client rule before activation but only
            // return extension bytes for templates that must serialize them.
            // https://github.com/litecoin-project/litecoin/blob/0.21/doc/mweb/mining-changes.md
            if(mwebPayload != null)
            {
                var separator = new byte[] { 0x01 };

                bs.ReadWrite(separator);
                bs.ReadWrite(mwebPayload);
            }

            return stream.ToArray();
        }
    }

    protected virtual byte[] BuildRawTransactionBuffer()
    {
        using(var stream = new MemoryStream())
        {
            foreach(var tx in BlockTemplate.Transactions)
            {
                var txRaw = tx.Data.HexToByteArray();
                stream.Write(txRaw);
            }

            return stream.ToArray();
        }
    }

    #region Masternodes

    protected MasterNodeBlockTemplateExtra masterNodeParameters;

    protected virtual Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        if(masterNodeParameters.Masternode != null)
        {
            Masternode[] masternodes;

            // Dash v13 Multi-Master-Nodes
            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var masterNode in masternodes)
                {
                    if(!string.IsNullOrEmpty(masterNode.Payee))
                    {
                        var payeeDestination = BitcoinUtils.AddressToDestination(masterNode.Payee, network);
                        var payeeReward = masterNode.Amount;

                        tx.Outputs.Add(payeeReward, payeeDestination);
                        reward -= payeeReward;
                    }
                }
            }
        }

        if(masterNodeParameters.SuperBlocks is { Length: > 0 })
        {
            foreach(var superBlock in masterNodeParameters.SuperBlocks)
            {
                var payeeAddress = BitcoinUtils.AddressToDestination(superBlock.Payee, network);
                var payeeReward = superBlock.Amount;

                tx.Outputs.Add(payeeReward, payeeAddress);
                reward -= payeeReward;
            }
        }

        if(!coin.HasPayee && !string.IsNullOrEmpty(masterNodeParameters.Payee))
        {
            var payeeAddress = BitcoinUtils.AddressToDestination(masterNodeParameters.Payee, network);
            var payeeReward = masterNodeParameters.PayeeAmount;

            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // Masternodes

    #region Fortune

    protected FortuneBlockTemplateExtra fortuneParameters;

    protected virtual Money CreateFortuneOutputs(Transaction tx, Money reward)
    {
        if(fortuneParameters.Fortune != null)
        {
            Fortune[] fortunes;
            if(fortuneParameters.Fortune.Type == JTokenType.Array)
                fortunes = fortuneParameters.Fortune.ToObject<Fortune[]>();
            else
                fortunes = new[] { fortuneParameters.Fortune.ToObject<Fortune>() };

            if(fortunes != null)
            {
                foreach(var Fortune in fortunes)
                {
                    if(!string.IsNullOrEmpty(Fortune.Payee))
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(Fortune.Payee, network);
                        var payeeReward = Fortune.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Fortune

    #region Founder

    protected FounderBlockTemplateExtra founderParameters;

    protected virtual Money CreateFounderOutputs(Transaction tx, Money reward)
    {
        if (founderParameters.Founder != null)
        {
            Founder[] founders;
            if (founderParameters.Founder.Type == JTokenType.Array)
                founders = founderParameters.Founder.ToObject<Founder[]>();
            else
                founders = new[] { founderParameters.Founder.ToObject<Founder>() };

            if(founders != null)
            {
                foreach(var Founder in founders)
                {
                    if(!string.IsNullOrEmpty(Founder.Payee))
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(Founder.Payee, network);
                        var payeeReward = Founder.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Founder

    #region Minerfund

    protected MinerFundTemplateExtra minerFundParameters;

    protected virtual Money CreateMinerFundOutputs(Transaction tx, Money reward)
    {
        if (!string.IsNullOrEmpty(minerFundParameters.Addresses?.FirstOrDefault()))
        {
            var payeeReward = minerFundParameters.MinimumValue;

            var payeeAddress = BitcoinUtils.AddressToDestination(minerFundParameters.Addresses[0], network);
            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // Founder

    #region CommunityAddress

    protected virtual Money CreateCommunityAddressOutputs(Transaction tx, Money reward)
    {
        if(BlockTemplate.CommunityAutonomousValue > 0)
        {
            var payeeReward = BlockTemplate.CommunityAutonomousValue;
            var payeeAddress = BitcoinUtils.AddressToDestination(BlockTemplate.CommunityAutonomousAddress, network);
            tx.Outputs.Add(payeeReward, payeeAddress);
        }
        return reward;
    }
    #endregion // CommunityAddres

    #region CoinbaseDevReward

    protected CoinbaseDevRewardTemplateExtra CoinbaseDevRewardParams;

    protected virtual Money CreateCoinbaseDevRewardOutputs(Transaction tx, Money reward)
    {
        if(CoinbaseDevRewardParams.CoinbaseDevReward != null)
        {
            CoinbaseDevReward[] CBRewards;
            CBRewards = new[] { CoinbaseDevRewardParams.CoinbaseDevReward.ToObject<CoinbaseDevReward>() };

            foreach(var CBReward in CBRewards)
            {
                if(!string.IsNullOrEmpty(CBReward.ScriptPubkey))
                {
                    Script payeeAddress = new Script(CBReward.ScriptPubkey.HexToByteArray());
                    var payeeReward = CBReward.Value;
                    tx.Outputs.Add(payeeReward, payeeAddress);
                }
            }
        }
        return reward;
    }

    #endregion // CoinbaseDevReward

    #region CoinbaseStakingReward

    protected CoinbaseStakingRewardTemplateExtra coinbaseStakingRewardParameters;

    protected virtual Money CreateCoinbaseStakingRewardOutputs(Transaction tx, Money reward)
    {
        if(!string.IsNullOrEmpty(coinbaseStakingRewardParameters.PayoutScript?.ScriptPubkey))
        {
            Script payeeAddress = new Script(coinbaseStakingRewardParameters.PayoutScript.ScriptPubkey.HexToByteArray());
            var payeeReward = coinbaseStakingRewardParameters.MinimumValue;
            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }
        return reward;
    }

    #endregion // CoinbaseStakingReward

    #region Community

    protected CommunityBlockTemplateExtra communityParameters;

    protected virtual Money CreateCommunityOutputs(Transaction tx, Money reward)
    {
        if (communityParameters.Community != null)
        {
            Community[] communitys;
            if (communityParameters.Community.Type == JTokenType.Array)
                communitys = communityParameters.Community.ToObject<Community[]>();
            else
                communitys = new[] { communityParameters.Community.ToObject<Community>() };

            if(communitys != null)
            {
                foreach(var Community in communitys)
                {
                    if(!string.IsNullOrEmpty(Community.Script))
                    {
                        Script payeeAddress = new (Community.Script.HexToByteArray());
                        var payeeReward = Community.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //Community

    #region DataMining

    protected DataMiningBlockTemplateExtra dataminingParameters;

    protected virtual Money CreateDataMiningOutputs(Transaction tx, Money reward)
    {
        if (dataminingParameters.DataMining != null)
        {
            DataMining[] dataminings;
            if (dataminingParameters.DataMining.Type == JTokenType.Array)
                dataminings = dataminingParameters.DataMining.ToObject<DataMining[]>();
            else
                dataminings = new[] { dataminingParameters.DataMining.ToObject<DataMining>() };

            if(dataminings != null)
            {
                foreach(var DataMining in dataminings)
                {
                    if(!string.IsNullOrEmpty(DataMining.Script))
                    {
                        Script payeeAddress = new (DataMining.Script.HexToByteArray());
                        var payeeReward = DataMining.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        //reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //DataMining

    #region Developer

    protected DeveloperBlockTemplateExtra developerParameters;

    protected virtual Money CreateDeveloperOutputs(Transaction tx, Money reward)
    {
        if (developerParameters.Developer != null)
        {
            Developer[] developers;
            if (developerParameters.Developer.Type == JTokenType.Array)
                developers = developerParameters.Developer.ToObject<Developer[]>();
            else
                developers = new[] { developerParameters.Developer.ToObject<Developer>() };

            if(developers != null)
            {
                foreach(var Developer in developers)
                {
                    if(!string.IsNullOrEmpty(Developer.Script))
                    {
                        Script payeeAddress = new (Developer.Script.HexToByteArray());
                        var payeeReward = Developer.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion //Developer

    #region API-Surface

    public BlockTemplate BlockTemplate { get; protected set; }
    public double Difficulty { get; protected set; }
    public long RewardBasisSatoshis => rewardToPool.Satoshi;
    internal BitcoinDirectCoinbaseSettlement DirectCoinbaseSettlement { get;
        private set; }
    internal long? DirectBlockWeight { get; private set; }
    internal string DirectPayoutAddress => directCoinbaseTemplate?.MinerAddress;
    internal long? DirectPayoutGeneration =>
        directCoinbaseTemplate?.AuthorizationGeneration;

    public string JobId { get; protected set; }

    public void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher) =>
        InitDirect(blockTemplate, jobId, pc, extraPoolConfig, cc, clock,
            poolAddressDestination, network, isPoS, shareMultiplier,
            coinbaseHasher, headerHasher, blockHasher, null);

    internal void InitDirect(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher,
        BitcoinDirectCoinbaseTemplate directCoinbaseTemplate = null)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        coin = pc.Template.As<BitcoinTemplate>();
        networkParams = coin.GetNetwork(network.ChainName);
        txVersion = coin.CoinbaseTxVersion;
        var isCanonicalBitcoin = IsCanonicalBitcoin(pc, coin);
        emitBip54CoinbaseFields = isCanonicalBitcoin &&
            (extraPoolConfig?.Bip54Coinbase ?? true);
        witnessCommitmentLast = isCanonicalBitcoin;
        txInSequence = emitBip54CoinbaseFields ? Bip54CoinbaseSequence : 0;
        if(emitBip54CoinbaseFields)
        {
            if(blockTemplate.Height == 0)
                throw new InvalidDataException(
                    "Canonical Bitcoin BIP54 coinbase construction requires a positive block height");

            txLockTime = blockTemplate.Height - 1;
        }
        else
            txLockTime = 0;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        this.directCoinbaseTemplate = directCoinbaseTemplate;
        BlockTemplate = blockTemplate;
        JobId = jobId;
        if(directCoinbaseTemplate != null)
            ValidateBlockTemplateTransactionWeights(blockTemplate, network);
        if(headerHasher is OdoCrypt)
            OdoCrypt.ValidateJobContract(blockTemplate, networkParams);

        mwebPayload = ParseMwebPayload(coin, blockTemplate);

        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString) ?
            cc.PaymentProcessing?.CoinbaseString.Trim() : "Miningcore";

        scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        extraNoncePlaceHolderLength = BitcoinConstants.ExtranoncePlaceHolderLength;
        this.isPoS = isPoS;
        this.shareMultiplier = shareMultiplier;

        txComment = !string.IsNullOrEmpty(extraPoolConfig?.CoinbaseTxComment) ?
            extraPoolConfig.CoinbaseTxComment : coin.CoinbaseTxComment;

        if(coin.HasMasterNodes)
        {
            masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();

            if(coin.HasSmartNodes)
            {
                if(masterNodeParameters.Extra?.ContainsKey("smartnode") == true)
                {
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["smartnode"]);
                }
            }

            if(!string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
            {
                txVersion = 3;
                const uint txType = 5;
                txVersion += txType << 16;
            }
        }

        if(coin.HasPayee)
            payeeParameters = BlockTemplate.Extra.SafeExtensionDataAs<PayeeBlockTemplateExtra>();

        if (coin.HasFounderFee)
            founderParameters = BlockTemplate.Extra.SafeExtensionDataAs<FounderBlockTemplateExtra>();

        if(coin.HasFortuneReward)
            fortuneParameters = BlockTemplate.Extra.SafeExtensionDataAs<FortuneBlockTemplateExtra>();

        if (coin.HasMinerFund)
            minerFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerFundTemplateExtra>("coinbasetxn", "minerfund");

        if(coin.HasCoinbaseDevReward)
            CoinbaseDevRewardParams = BlockTemplate.Extra.SafeExtensionDataAs<CoinbaseDevRewardTemplateExtra>();

        if (coin.HasCoinbaseStakingReward)
            coinbaseStakingRewardParameters = BlockTemplate.Extra.SafeExtensionDataAs<CoinbaseStakingRewardTemplateExtra>("coinbasetxn", "stakingrewards");

        if(coin.HasCommunity)
            communityParameters = BlockTemplate.Extra.SafeExtensionDataAs<CommunityBlockTemplateExtra>();

        if(coin.HasDataMining)
            dataminingParameters = BlockTemplate.Extra.SafeExtensionDataAs<DataMiningBlockTemplateExtra>();

        if(coin.HasDeveloper)
            developerParameters = BlockTemplate.Extra.SafeExtensionDataAs<DeveloperBlockTemplateExtra>();

        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;

        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            blockTargetValue = tmp.ToUInt256();
        }

        previousBlockHashReversedHex = BlockTemplate.PreviousBlockhash
            .HexToByteArray()
            .ReverseByteOrder()
            .ToHexString();

        BuildMerkleBranches();
        BuildCoinbase();
        if(directCoinbaseTemplate != null)
            DirectBlockWeight = CalculateDirectBlockWeight();

        jobParams = new object[]
        {
            JobId,
            previousBlockHashReversedHex,
            coinbaseInitialHex,
            coinbaseFinalHex,
            merkleBranchesHex,
            BlockTemplate.Version.ToStringHex8(),
            BlockTemplate.Bits,
            BlockTemplate.CurTime.ToStringHex8(),
            false
        };
    }

    internal static bool IsCanonicalBitcoin(PoolConfig pc,
        BitcoinTemplate template) =>
        string.Equals(pc.Coin, "bitcoin", StringComparison.Ordinal) &&
        template.Family == CoinFamily.Bitcoin &&
        string.Equals(template.Symbol, "BTC", StringComparison.Ordinal) &&
        string.Equals(template.CanonicalName, "Bitcoin", StringComparison.Ordinal);

    private long CalculateDirectBlockWeight()
    {
        long weight;

        try
        {
            var transactionCount = checked(
                (ulong) BlockTemplate.Transactions.Length + 1);
            var coinbaseLength = checked((long) coinbaseInitial.Length +
                extraNoncePlaceHolderLength + coinbaseFinal.Length);
            // This is the exact non-witness coinbase byte sequence written by
            // SerializeBlock. A default witness-commitment output is already
            // inside coinbaseFinal; Miningcore does not append a separate
            // coinbase witness serialization outside these bytes. Do not add
            // hypothetical witness-stack weight unless SerializeBlock starts
            // emitting those bytes as well.
            weight = checked((80L + CompactSizeLength(transactionCount) +
                coinbaseLength) * 4L);

            var transactionWeight = BlockTemplate.ValidatedTransactionWeight >= 0
                ? BlockTemplate.ValidatedTransactionWeight
                : ValidateBlockTemplateTransactionWeights(BlockTemplate,
                    network);
            weight = checked(weight + transactionWeight);
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                "Direct SOLO block-weight calculation overflowed", ex);
        }

        if(weight > BitcoinConsensusMaxBlockWeight)
        {
            throw new InvalidDataException(
                $"Direct SOLO block weight {weight} exceeds Bitcoin's " +
                $"{BitcoinConsensusMaxBlockWeight}-weight-unit consensus limit");
        }

        return weight;
    }

    internal static long ValidateBlockTemplateTransactionWeights(
        BlockTemplate blockTemplate, Network network)
    {
        ArgumentNullException.ThrowIfNull(blockTemplate);
        ArgumentNullException.ThrowIfNull(network);
        if(blockTemplate.ValidatedTransactionWeight >= 0)
            return blockTemplate.ValidatedTransactionWeight;

        long result = 0;
        var transactions = blockTemplate.Transactions ??
            throw new InvalidDataException(
                "Direct SOLO requires a getblocktemplate transaction array");

        try
        {
            foreach(var templateTransaction in transactions)
            {
                if(templateTransaction.Weight is not > 0)
                {
                    throw new InvalidDataException(
                        "Direct SOLO requires a positive daemon-reported weight " +
                        "for every getblocktemplate transaction");
                }
                if(string.IsNullOrWhiteSpace(templateTransaction.Data))
                {
                    throw new InvalidDataException(
                        "Direct SOLO requires serialized data for every getblocktemplate transaction");
                }

                Transaction transaction;
                try
                {
                    transaction = Transaction.Parse(templateTransaction.Data,
                        network);
                }
                catch(Exception ex)
                {
                    throw new InvalidDataException(
                        "Direct SOLO could not parse a getblocktemplate transaction",
                        ex);
                }

                var totalSize = transaction
                    .WithOptions(TransactionOptions.Witness).ToBytes().Length;
                var strippedSize = transaction
                    .WithOptions(TransactionOptions.None).ToBytes().Length;
                var actualWeight = checked(strippedSize * 3L + totalSize);
                if(templateTransaction.Weight.Value != actualWeight)
                {
                    throw new InvalidDataException(
                        $"Direct SOLO daemon-reported transaction weight " +
                        $"{templateTransaction.Weight.Value} does not match " +
                        $"serialized weight {actualWeight}");
                }

                result = checked(result + actualWeight);
            }
        }
        catch(OverflowException ex)
        {
            throw new InvalidDataException(
                "Direct SOLO template transaction-weight calculation overflowed",
                ex);
        }

        blockTemplate.ValidatedTransactionWeight = result;
        return result;
    }

    private static int CompactSizeLength(ulong value) => value switch
    {
        < 253 => 1,
        <= ushort.MaxValue => 3,
        <= uint.MaxValue => 5,
        _ => 9,
    };

    internal static byte[] ParseMwebPayload(BitcoinTemplate coin, BlockTemplate blockTemplate)
    {
        if(!coin.HasMWEB || blockTemplate.Extra?.TryGetValue("mweb", out var value) != true)
            return null;

        var mweb = value switch
        {
            string text => text,
            JValue { Type: JTokenType.String } token => token.Value<string>(),
            _ => throw new InvalidDataException("Block template field 'mweb' must be a hexadecimal string")
        };

        if(string.IsNullOrWhiteSpace(mweb))
            throw new InvalidDataException("Block template field 'mweb' must not be empty");

        try
        {
            return Convert.FromHexString(mweb);
        }
        catch(FormatException ex)
        {
            throw new InvalidDataException("Block template field 'mweb' must be valid hexadecimal", ex);
        }
    }

    public object GetJobParams(bool isNew)
    {
        jobParams[^1] = isNew;
        return jobParams;
    }

    public virtual (Share Share, string BlockHex) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce, string versionBits = null)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // validate nTime
        if(nTime.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of ntime");

        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        // validate nonce
        if(nonce.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

        var versionBitsInt = ParseVersionBits(context.VersionRollingMask, versionBits);

        // dupe check
        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce,
               versionBitsInt))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt, versionBitsInt);
    }

    #endregion // API-Surface
}
