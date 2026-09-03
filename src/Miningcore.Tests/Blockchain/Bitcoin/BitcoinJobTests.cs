using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Blockchain.Progpow;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using NSubstitute;
using Newtonsoft.Json;
using NLog;
using Xunit;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;
#pragma warning disable 8974

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobTests : TestBase
{
    [Fact]
    public void ResolveAddressDestination_BCashUsesSuppliedAddress()
    {
        const string address =
            "bitcoincash:qzyvaurh8vlj22jvyhpdce6ld4lt3zfc3svyt665de";
        var expected = BitcoinUtils.BCashAddressToDestination(address,
            Network.Main);

        var actual = BitcoinJobManagerBase<BitcoinJob>
            .ResolveAddressDestination(address, BitcoinAddressType.BCash,
                Network.Main);

        Assert.Equal(expected.ScriptPubKey, actual.ScriptPubKey);
    }

    [Theory]
    [InlineData(1, "main", false, true)]
    [InlineData(0, "regtest", true, true)]
    [InlineData(0, "REGTEST", true, true)]
    [InlineData(0, "regtest", false, false)]
    [InlineData(0, "main", true, false)]
    [InlineData(0, null, true, false)]
    public void PeerReadiness_BypassesOnlyExplicitRegtest(int connections, string chain,
        bool allowPeerlessRegtest, bool expected)
    {
        Assert.Equal(expected, BitcoinJobManagerBase<BitcoinJob>.HasRequiredPeerConnection(
            connections, chain, allowPeerlessRegtest));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("DUPLICATE")]
    [InlineData("duplicate-inconclusive")]
    public void DuplicateSubmitResponse_IsLookupAmbiguous(string response)
    {
        Assert.True(BitcoinJobManagerBase<BitcoinJob>
            .IsDuplicateBlockSubmissionResponse(response));
    }

    [Theory]
    [InlineData("inconclusive")]
    [InlineData("INCONCLUSIVE")]
    public void InconclusiveSubmitResponse_IsIndeterminate(string response)
    {
        Assert.True(BitcoinJobManagerBase<BitcoinJob>
            .IsInconclusiveBlockSubmissionResponse(response));
    }

    [Fact]
    public void AcceptedBlockLookup_RequiresExactActiveHash()
    {
        Assert.True(BitcoinJobManagerBase<BitcoinJob>.IsAcceptedBlockLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = 1,
                Transactions = new[] { "coinbase-txid" },
            },
            "BLOCK-HASH"));

        Assert.False(BitcoinJobManagerBase<BitcoinJob>.IsAcceptedBlockLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = 1,
            },
            "block-hash"));

        Assert.False(BitcoinJobManagerBase<BitcoinJob>.IsAcceptedBlockLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = -1,
                Transactions = new[] { "coinbase-txid" },
            },
            "block-hash"));

        Assert.False(BitcoinJobManagerBase<BitcoinJob>.IsAcceptedBlockLookup(
            new JsonRpcError(-5, "not found", null),
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = 1,
                Transactions = new[] { "coinbase-txid" },
            },
            "block-hash"));
    }

    [Fact]
    public void SubmissionBlockLookup_ClassifiesUnavailableAndInactiveSeparately()
    {
        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.Accepted,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                null,
                new DaemonBlock
                {
                    Hash = "block-hash",
                    Confirmations = 1,
                    Transactions = new[] { "coinbase-txid" },
                },
                "block-hash"));

        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.MissingCoinbase,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                null,
                new DaemonBlock
                {
                    Hash = "block-hash",
                    Confirmations = 1,
                },
                "block-hash"));

        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.KnownInactive,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                null,
                new DaemonBlock
                {
                    Hash = "block-hash",
                    Confirmations = -1,
                    Transactions = new[] { "coinbase-txid" },
                },
                "block-hash"));

        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.Unavailable,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                new JsonRpcError(-5, "not found", null),
                null,
                "block-hash"));

        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.Unavailable,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                null,
                new DaemonBlock
                {
                    Hash = "other-block",
                    Confirmations = 1,
                    Transactions = new[] { "coinbase-txid" },
                },
                "block-hash"));

        Assert.Equal(BitcoinJobManagerBase<BitcoinJob>.SubmissionBlockLookupResult.Unavailable,
            BitcoinJobManagerBase<BitcoinJob>.ClassifySubmissionBlockLookup(
                null,
                new DaemonBlock
                {
                    Hash = "block-hash",
                    Confirmations = 0,
                    Transactions = new[] { "coinbase-txid" },
                },
                "block-hash"));
    }

    [Fact]
    public void DuplicateSubmitLookup_NeverAcceptsInSharedBitcoinPath()
    {
        var active = BitcoinJobManagerBase<BitcoinJob>.ClassifyDuplicateSubmissionLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = 1,
                Transactions = new[] { "coinbase-txid" },
            },
            "block-hash");

        Assert.False(active.Accepted);
        Assert.True(active.Ambiguous);
        Assert.True(active.Duplicate);
        Assert.Equal("coinbase-txid", active.CoinbaseTx);

        var sideChain = BitcoinJobManagerBase<BitcoinJob>.ClassifyDuplicateSubmissionLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = -1,
                Transactions = new[] { "coinbase-txid" },
            },
            "block-hash");

        Assert.False(sideChain.Accepted);
        Assert.False(sideChain.Ambiguous);
        Assert.True(sideChain.Duplicate);
        Assert.Null(sideChain.CoinbaseTx);

        var unavailable = BitcoinJobManagerBase<BitcoinJob>.ClassifyDuplicateSubmissionLookup(
            new JsonRpcError(-5, "not found", null),
            null,
            "block-hash");

        Assert.False(unavailable.Accepted);
        Assert.True(unavailable.Ambiguous);
        Assert.True(unavailable.Duplicate);
        Assert.Null(unavailable.CoinbaseTx);

        var missingCoinbase = BitcoinJobManagerBase<BitcoinJob>.ClassifyDuplicateSubmissionLookup(
            null,
            new DaemonBlock
            {
                Hash = "block-hash",
                Confirmations = 1,
            },
            "block-hash");

        Assert.False(missingCoinbase.Accepted);
        Assert.True(missingCoinbase.Ambiguous);
        Assert.True(missingCoinbase.Duplicate);
        Assert.Null(missingCoinbase.CoinbaseTx);
    }

    [Fact]
    public void SubmissionOutcome_PreservesAcceptedAndIndeterminateResultsAsAmbiguous()
    {
        var active = new DaemonBlock
        {
            Hash = "block-hash",
            Confirmations = 1,
            Transactions = new[] { "coinbase-txid" },
        };

        var missingCoinbase = new DaemonBlock
        {
            Hash = "block-hash",
            Confirmations = 1,
        };

        var zeroConfirmations = new DaemonBlock
        {
            Hash = "block-hash",
            Confirmations = 0,
            Transactions = new[] { "coinbase-txid" },
        };

        var inactive = new DaemonBlock
        {
            Hash = "block-hash",
            Confirmations = -1,
            Transactions = new[] { "coinbase-txid" },
        };

        var jsonNullUnavailable = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome(null, null,
                new JsonRpcError(-5, "not found", null), null, "block-hash");
        Assert.False(jsonNullUnavailable.Accepted);
        Assert.True(jsonNullUnavailable.Ambiguous);
        Assert.False(jsonNullUnavailable.Duplicate);

        var jsonNullMissingCoinbase = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome(null, null,
                null, missingCoinbase, "block-hash");
        Assert.False(jsonNullMissingCoinbase.Accepted);
        Assert.True(jsonNullMissingCoinbase.Ambiguous);

        var jsonNullZeroConfirmations = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome(null, null,
                null, zeroConfirmations, "block-hash");
        Assert.False(jsonNullZeroConfirmations.Accepted);
        Assert.True(jsonNullZeroConfirmations.Ambiguous);

        var inconclusiveActive = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome("inconclusive", null,
                null, active, "block-hash");
        Assert.True(inconclusiveActive.Accepted);
        Assert.False(inconclusiveActive.Ambiguous);
        Assert.Equal("coinbase-txid", inconclusiveActive.CoinbaseTx);

        var inconclusiveUnavailable = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome("inconclusive", null,
                new JsonRpcError(-5, "not found", null), null, "block-hash");
        Assert.False(inconclusiveUnavailable.Accepted);
        Assert.True(inconclusiveUnavailable.Ambiguous);

        var duplicateUnavailable = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome("duplicate", null,
                new JsonRpcError(-5, "not found", null), null, "block-hash");
        Assert.False(duplicateUnavailable.Accepted);
        Assert.True(duplicateUnavailable.Ambiguous);
        Assert.True(duplicateUnavailable.Duplicate);

        var duplicateInactive = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome("duplicate", null,
                null, inactive, "block-hash");
        Assert.False(duplicateInactive.Accepted);
        Assert.False(duplicateInactive.Ambiguous);
        Assert.True(duplicateInactive.Duplicate);

        var invalid = BitcoinJobManagerBase<BitcoinJob>
            .ClassifyBlockSubmissionOutcome("duplicate-invalid", null,
                null, active, "block-hash");
        Assert.False(invalid.Accepted);
        Assert.False(invalid.Ambiguous);
        Assert.False(invalid.Duplicate);
    }

    [Fact]
    public void Process_Valid_Block()
    {
        // Use a very low share difficulty so this stale embedded submit vector
        // still exercises the "accepted share" path deterministically on Linux/current native libs.
        var (job, worker) = CreateJob(0.000000000001d);

        var submitParams = JsonConvert.DeserializeObject<object[]>("[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]", jsonSerializerSettings);

        // extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // validate & process
        var (share, blockHex) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.Equal(813750, share.BlockHeight);

        // This old embedded vector is no longer a true block candidate on current dev/native code.
        Assert.False(share.IsBlockCandidate);
        Assert.True(string.IsNullOrEmpty(blockHex));
    }

    [Fact]
    public void Process_Duplicate_Submission()
    {
        // Use the same low share difficulty as Process_Valid_Block so the first submit is accepted.
        var (job, worker) = CreateJob(0.000000000001d);

        var submitParams = JsonConvert.DeserializeObject<object[]>("[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"51036775\"]", jsonSerializerSettings);

        // extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // validate & process
        var (share, _) = job.ProcessShare(worker, extraNonce2, nTime, nonce);

        Assert.NotNull(share);
        Assert.False(share.IsBlockCandidate);

        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Theory]
    [InlineData(null, "missing version_bits")]
    [InlineData("", "missing version_bits")]
    [InlineData("0000200", "incorrect size of version_bits")]
    [InlineData("000020000", "incorrect size of version_bits")]
    [InlineData("00002xyz", "invalid version_bits")]
    [InlineData("80000000", "rolling-version mask violation")]
    public void Process_VersionRollingRejectsMissingMalformedAndUnmaskedBits(
        string versionBits, string expectedMessage)
    {
        var (job, worker) = CreateJob(0.000000000001d);
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x1fffe000;

        var ex = Assert.Throws<StratumException>(() => job.ProcessShare(worker,
            "01000000", "63445774", "51036775", versionBits));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Process_CoinSpecificMaskRejectsClippedConsensusBits()
    {
        var (job, worker) = CreateJob(0.000000000001d);
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x00002000;

        var ex = Assert.Throws<StratumException>(() => job.ProcessShare(worker,
            "01000000", "63445774", "51036775", "00004000"));

        Assert.Contains("rolling-version mask violation", ex.Message);
    }

    [Fact]
    public void Process_VersionRollingTreatsDifferentBitsAsDifferentWork()
    {
        var (job, worker) = CreateJob(0.000000000001d);
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x1fffe000;

        var first = job.ProcessShare(worker, "01000000", "63445774",
            "51036775", "00002000");
        var second = job.ProcessShare(worker, "01000000", "63445774",
            "51036775", "00004000");

        Assert.NotNull(first.Share);
        Assert.NotNull(second.Share);
        var duplicate = Assert.Throws<StratumException>(() => job.ProcessShare(worker,
            "01000000", "63445774", "51036775", "00002000"));
        Assert.Equal(StratumError.DuplicateShare, duplicate.Code);
    }

    [Fact]
    public void Process_VersionRollingAcceptsExplicitZero()
    {
        var (job, worker) = CreateJob(0.000000000001d);
        worker.ContextAs<BitcoinWorkerContext>().VersionRollingMask = 0x1fffe000;

        var result = job.ProcessShare(worker, "01000000", "63445774",
            "51036775", "00000000");

        Assert.NotNull(result.Share);
    }

    [Fact]
    public void SerializeHeader_VersionRollingAppliesMaskToExactSerializedVersion()
    {
        var (baseJob, _) = CreateJob();
        var job = Assert.IsType<VersionSerializationBitcoinJob>(baseJob);
        const uint templateVersion = 0x20002000;
        const uint mask = 0x00006000;
        const uint submittedBits = 0x00004000;
        job.SetTemplateVersion(templateVersion);

        var serializedVersion = job.SerializeVersion(mask, submittedBits);
        var expected = (templateVersion & ~mask) | (submittedBits & mask);

        Assert.Equal(expected, serializedVersion);
        Assert.Equal(templateVersion, job.SerializeVersion(null, null));
    }

    [Theory]
    [InlineData(null, "missing version_bits")]
    [InlineData("0000200", "incorrect size of version_bits")]
    [InlineData("00002xyz", "invalid version_bits")]
    [InlineData("80000000", "rolling-version mask violation")]
    public void MergedProcess_UsesSameStrictVersionRollingValidation(
        string versionBits, string expectedMessage)
    {
        var clock = MockMasterClock.FromTicks(638010200200475015);
        var job = new VersionValidationMergedJob();
        job.Configure(clock);
        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory),
            container.Resolve<RecyclableMemoryStreamManager>(), clock, "merged", false);
        worker.SetContext(new MergedMiningBitcoinWorkerContext
        {
            ExtraNonce1 = "60000001",
            Difficulty = 0.000000000001d,
            VersionRollingMask = 0x1fffe000,
        });

        var ex = Assert.Throws<StratumException>(() => job.ProcessShareMerged(worker,
            "01000000", "63445774", "51036775", versionBits));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Process_Invalid_Nonce()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>("[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"63445774\",\"61036775\"]", jsonSerializerSettings);

        // extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // validate & process
        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void Process_Invalid_Time()
    {
        var (job, worker) = CreateJob();

        var submitParams = JsonConvert.DeserializeObject<object[]>("[\"yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4\",\"00000001\",\"01000000\",\"13445774\",\"51036775\"]", jsonSerializerSettings);

        // extract params
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;

        // validate & process
        Assert.ThrowsAny<StratumException>(() => job.ProcessShare(worker, extraNonce2, nTime, nonce));
    }

    [Fact]
    public void DirectSoloJobs_AreDestinationSpecificAndWorkerIsolated()
    {
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var pc = new PoolConfig
        {
            Coin = "bitcoin",
            Template = coin,
        };
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
            DefaultWitnessCommitment =
                "6a24aa21a9ed" + new string('0', 64),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var fee = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var minerA = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var minerB = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var recipients = BitcoinDirectCoinbase.ValidateRecipients(new[]
        {
            new RewardRecipient
            {
                Address = fee.ToString(),
                Percentage = 2,
            },
        }, value => BitcoinAddress.Create(value, Network.RegTest));

        DirectSerializationBitcoinJob Create(string id,
            BitcoinAddress miner, long generation = 0)
        {
            var result = new DirectSerializationBitcoinJob();
            result.InitDirect(blockTemplate, id, pc, null,
                new ClusterConfig(), clock, pool, Network.RegTest, false,
                coin.ShareMultiplier, coin.CoinbaseHasherValue,
                coin.HeaderHasherValue, coin.BlockHasherValue,
                new BitcoinDirectCoinbaseTemplate
                {
                    AuthorizationGeneration = generation,
                    MinerAddress = miner.ToString(),
                    MinerDestination = miner,
                    MinerScriptPubKey = miner.ScriptPubKey.ToHex(),
                    Recipients = recipients,
                });
            return result;
        }

        var jobA = Create("direct-a", minerA);
        var jobB = Create("direct-b", minerB);
        var paramsA = (object[]) jobA.GetJobParams(true);
        var paramsB = (object[]) jobB.GetJobParams(true);

        Assert.NotEqual(paramsA[0], paramsB[0]);
        Assert.Equal(paramsA[2], paramsB[2]);
        Assert.NotEqual(paramsA[3], paramsB[3]);
        Assert.NotEqual(jobA.DirectPayoutAddress, jobB.DirectPayoutAddress);

        var coinbaseA = jobA.SerializeCoinbaseForTest("00000001",
            "00000000000000");
        var coinbaseB = jobB.SerializeCoinbaseForTest("00000001",
            "00000000000000");
        var transactionA = Transaction.Parse(coinbaseA.ToHexString(),
            Network.RegTest);
        var transactionB = Transaction.Parse(coinbaseB.ToHexString(),
            Network.RegTest);
        var blockA = jobA.SerializeBlockForTest(coinbaseA);
        Assert.Equal(transactionA.GetHash(), transactionA.GetWitHash());
        Assert.Equal(checked(blockA.Length * 4L),
            jobA.DirectBlockWeight);
        Span<byte> coinbaseHashA = stackalloc byte[32];
        coin.CoinbaseHasherValue.Digest(coinbaseA, coinbaseHashA);
        Assert.Equal(transactionA.GetHash().ToString(),
            new uint256(coinbaseHashA).ToString());
        Assert.NotEqual(transactionA.GetHash(), transactionB.GetHash());
        Assert.NotEqual(transactionA.GetHash().ToString(),
            transactionB.GetHash().ToString());

        Assert.Contains("6a24aa21a9ed", (string) paramsA[3]);
        var transaction = transactionA;
        Assert.Equal(blockTemplate.Height - 1,
            transaction.LockTime.Value);
        Assert.Equal(uint.MaxValue - 1,
            Assert.Single(transaction.Inputs).Sequence.Value);
        Assert.Equal(3, transaction.Outputs.Count);
        Assert.Equal(minerA.ScriptPubKey,
            transaction.Outputs[0].ScriptPubKey);
        Assert.Equal(fee.ScriptPubKey,
            transaction.Outputs[1].ScriptPubKey);
        Assert.Equal(0, transaction.Outputs[2].Value.Satoshi);
        Assert.StartsWith("6a24aa21a9ed",
            transaction.Outputs[2].ScriptPubKey.ToHex(),
            StringComparison.OrdinalIgnoreCase);
        var outputs = jobA.Outputs;
        Assert.Equal(2, outputs.Count);
        Assert.Equal(minerA.ScriptPubKey,
            outputs[0].ScriptPubKey);
        Assert.Equal(4_900_000_000,
            outputs[0].Value.Satoshi);
        Assert.Equal(fee.ScriptPubKey,
            outputs[1].ScriptPubKey);
        Assert.Equal(100_000_000,
            outputs[1].Value.Satoshi);

        // AxeOS 3a09ea00c6f1254e4e19cb7033f8f6b8bf055e44 extracts the
        // address before the worker suffix, matches its script against every
        // coinbase output and reports user value / total coinbase value.
        var axeOsUsername = $"{minerA}.worker";
        var axeOsAddress = BitcoinAddress.Create(
            axeOsUsername.Split('.')[0], Network.RegTest);
        var decodedOutputs = Enumerable.Range(0, transaction.Outputs.Count)
            .Select(index => transaction.Outputs[index])
            .ToArray();
        var axeOsUserSatoshis = decodedOutputs
            .Where(output => output.ScriptPubKey == axeOsAddress.ScriptPubKey)
            .Sum(output => output.Value.Satoshi);
        var axeOsCoinbaseSatoshis = decodedOutputs.Sum(output =>
            output.Value.Satoshi);
        Assert.True(axeOsUserSatoshis > 0);
        Assert.Equal(98m, decimal.Divide(axeOsUserSatoshis * 100m,
            axeOsCoinbaseSatoshis));

        var context = new BitcoinWorkerContext();
        var authorizationA = context.SetDirectPayoutAuthorization(
            minerA.ToString(), minerA);
        jobA = Create("direct-a-current", minerA,
            authorizationA.Generation);
        Assert.True(context.TryAddDirectJob(jobA, 4));
        Assert.Same(jobA, context.GetJob(jobA.JobId));
        Assert.Null(context.GetJob(jobB.JobId));
        context.SetDirectPayoutAuthorization(minerB.ToString(), minerB);
        Assert.Equal(minerA.ToString(), jobA.DirectPayoutAddress);
        Assert.Null(context.GetJob(jobA.JobId));
    }

    [Theory]
    [InlineData("bitcoin", true, 100u, 0xfffffffeu, false)]
    [InlineData("bitcoin", false, 0u, 0u, true)]
    [InlineData("litecoin", true, 0u, 0u, true)]
    public void CanonicalBitcoinCustodialCoinbase_UsesConfiguredBip54PolicyWithoutChangingAltcoins(
        string coinId, bool bip54Coinbase, uint expectedLockTime,
        uint expectedSequence, bool witnessFirst)
    {
        var coin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates[coinId]);
        var pc = new PoolConfig
        {
            Coin = coinId,
            Template = coin,
        };
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
            DefaultWitnessCommitment =
                "6a24aa21a9ed" + new string('0', 64),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var job = new DirectSerializationBitcoinJob();

        job.Init(blockTemplate, $"{coinId}-custodial", pc,
            new BitcoinPoolConfigExtra { Bip54Coinbase = bip54Coinbase },
            new ClusterConfig(), clock, pool, Network.RegTest, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue);

        var coinbase = job.SerializeCoinbaseForTest("00000001",
            "00000000000000");
        var transaction = Transaction.Parse(coinbase.ToHexString(),
            Network.RegTest);

        Assert.Equal(expectedLockTime, transaction.LockTime.Value);
        Assert.Equal(expectedSequence,
            Assert.Single(transaction.Inputs).Sequence.Value);
        Assert.Equal(2, transaction.Outputs.Count);
        var witnessIndex = witnessFirst ? 0 : 1;
        var valueIndex = witnessFirst ? 1 : 0;
        Assert.Equal(0, transaction.Outputs[witnessIndex].Value.Satoshi);
        Assert.StartsWith("6a24aa21a9ed",
            transaction.Outputs[witnessIndex].ScriptPubKey.ToHex(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pool.ScriptPubKey,
            transaction.Outputs[valueIndex].ScriptPubKey);
        Assert.Equal(blockTemplate.CoinbaseValue,
            transaction.Outputs[valueIndex].Value.Satoshi);
    }

    [Fact]
    public void CanonicalBitcoinMergedMiningCoinbase_UsesBip54FieldsAndWitnessLast()
    {
        // BTC-parent merged mining is not a supported production topology today;
        // this pins the latent InitMerged path because it shares BitcoinJob's
        // coinbase serializer and must remain safe if that topology is enabled.
        var coin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var pc = new PoolConfig
        {
            Coin = "bitcoin",
            Template = coin,
        };
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
            DefaultWitnessCommitment =
                "6a24aa21a9ed" + new string('0', 64),
        };
        var auxiliaryTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.AuxBlockTemplate
        {
            Bits = "207fffff",
            Hash = new string('0', 64),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var job = new MergedSerializationBitcoinJob();

        job.InitMerged(blockTemplate, auxiliaryTemplate, "bitcoin-merged",
            pc, null, new ClusterConfig(), clock, pool, Network.RegTest,
            false, coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue);

        var transaction = Transaction.Parse(job.SerializeCoinbaseForTest(
                "00000001", "00000000000000").ToHexString(),
            Network.RegTest);

        Assert.Equal(100u, transaction.LockTime.Value);
        Assert.Equal(uint.MaxValue - 1,
            Assert.Single(transaction.Inputs).Sequence.Value);
        Assert.Equal(pool.ScriptPubKey,
            transaction.Outputs[0].ScriptPubKey);
        Assert.StartsWith("6a24aa21a9ed",
            transaction.Outputs[^1].ScriptPubKey.ToHex(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ANOK")]
    [InlineData("RVH")]
    public void DaemonWitnessCommitment_IsSerializedVerbatim(string symbol)
    {
        var bitcoin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var coin = new ProgpowCoinTemplate
        {
            Name = symbol,
            CanonicalName = symbol,
            Symbol = symbol,
            Family = CoinFamily.Bitcoin,
            CoinbaseTxVersion = 1,
        };
        var pc = new PoolConfig
        {
            Coin = symbol.ToLowerInvariant(),
            Template = coin,
        };
        var commitment = "6a24aa21a9ed" + new string('1', 64);
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
            DefaultWitnessCommitment = commitment,
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var job = new SerializationProgpowJob();

        job.Init(blockTemplate, $"{symbol}-commitment", pc, null,
            new ClusterConfig(), clock, pool, Network.RegTest, false,
            bitcoin.ShareMultiplier, bitcoin.CoinbaseHasherValue,
            bitcoin.HeaderHasherValue, bitcoin.BlockHasherValue,
            Substitute.For<IProgpowCache>());

        var transaction = Transaction.Parse(job.SerializeCoinbaseForTest(
                "0001").ToHexString(),
            Network.RegTest);

        Assert.Equal(commitment,
            transaction.Outputs[0].ScriptPubKey.ToHex());
    }

    [Fact]
    public void BitcoinAndAltcoinJobs_KeepIndependentCoinbasePolicies()
    {
        var bitcoin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var litecoin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["litecoin"]);
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);

        DirectSerializationBitcoinJob Create(string coinId,
            BitcoinTemplate template)
        {
            var job = new DirectSerializationBitcoinJob();
            job.Init(blockTemplate, coinId, new PoolConfig
                {
                    Coin = coinId,
                    Template = template,
                }, null, new ClusterConfig(), clock, pool, Network.RegTest,
                false, template.ShareMultiplier,
                template.CoinbaseHasherValue, template.HeaderHasherValue,
                template.BlockHasherValue);
            return job;
        }

        var bitcoinJob = Create("bitcoin", bitcoin);
        var litecoinJob = Create("litecoin", litecoin);
        var bitcoinCoinbase = Transaction.Parse(bitcoinJob
                .SerializeCoinbaseForTest("00000001", "00000000000000")
                .ToHexString(),
            Network.RegTest);
        var litecoinCoinbase = Transaction.Parse(litecoinJob
                .SerializeCoinbaseForTest("00000001", "00000000000000")
                .ToHexString(),
            Network.RegTest);

        Assert.Equal(100u, bitcoinCoinbase.LockTime.Value);
        Assert.Equal(uint.MaxValue - 1,
            Assert.Single(bitcoinCoinbase.Inputs).Sequence.Value);
        Assert.Equal(0u, litecoinCoinbase.LockTime.Value);
        Assert.Equal(0u,
            Assert.Single(litecoinCoinbase.Inputs).Sequence.Value);
    }

    [Fact]
    public void CanonicalBitcoinCoinbase_RejectsZeroHeight()
    {
        var coin = Assert.IsType<BitcoinTemplate>(
            ModuleInitializer.CoinTemplates["bitcoin"]);
        var pc = new PoolConfig
        {
            Coin = "bitcoin",
            Template = coin,
        };
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 0,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var job = new DirectSerializationBitcoinJob();

        var error = Assert.Throws<InvalidDataException>(() => job.Init(
            blockTemplate, "bitcoin-zero-height", pc, null,
            new ClusterConfig(), clock, pool, Network.RegTest, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue));

        Assert.Contains("positive block height", error.Message);
    }

    [Fact]
    public void DirectSoloJob_RejectsNearFullTemplateWhenFinalBlockIsOverweight()
    {
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var pc = new PoolConfig
        {
            Coin = "bitcoin",
            Template = coin,
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var miner = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var configuredRecipients = Enumerable.Range(0,
                BitcoinDirectCoinbase.MaximumRecipientOutputs)
            .Select(_ => new Key().PubKey.GetAddress(
                ScriptPubKeyType.Segwit, Network.RegTest))
            .Select(address => new RewardRecipient
            {
                Address = address.ToString(),
                Percentage = 0.01m,
            })
            .ToArray();
        var recipients = BitcoinDirectCoinbase.ValidateRecipients(
            configuredRecipients,
            value => BitcoinAddress.Create(value, Network.RegTest));
        var directTemplate = new BitcoinDirectCoinbaseTemplate
        {
            MinerAddress = miner.ToString(),
            MinerDestination = miner,
            MinerScriptPubKey = miner.ScriptPubKey.ToHex(),
            Recipients = recipients,
        };

        Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate Template(
            params Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction[]
                transactions) => new()
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = transactions,
            DefaultWitnessCommitment =
                "6a24aa21a9ed" + new string('0', 64),
        };

        DirectSerializationBitcoinJob Create(
            Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate template,
            string id)
        {
            var job = new DirectSerializationBitcoinJob();
            job.InitDirect(template, id, pc, null, new ClusterConfig(), clock,
                pool, Network.RegTest, false, coin.ShareMultiplier,
                coin.CoinbaseHasherValue, coin.HeaderHasherValue,
                coin.BlockHasherValue, directTemplate);
            return job;
        }

        var coinbaseOnly = Create(Template(), "weight-baseline");
        var availableTransactionWeight =
            BitcoinJob.BitcoinConsensusMaxBlockWeight -
            coinbaseOnly.DirectBlockWeight!.Value;
        Transaction CreateTransaction(int scriptLength, int discriminator)
        {
            var transaction = Transaction.Create(Network.RegTest);
            transaction.Inputs.Add(new TxIn(new OutPoint(
                new uint256(discriminator.ToString("x64")), 0)));
            transaction.Outputs.Add(Money.Zero,
                new Script(new byte[scriptLength]));
            return transaction;
        }

        Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction
            Describe(Transaction transaction, long? weight = null) => new()
            {
                Data = Convert.ToHexString(transaction.ToBytes())
                    .ToLowerInvariant(),
                TxId = transaction.GetHash().ToString(),
                Hash = transaction.GetWitHash().ToString(),
                Weight = weight ?? checked(transaction.ToBytes().Length * 4L),
            };

        var largeTransaction = CreateTransaction(
            checked((int) (availableTransactionWeight / 4) - 100), 1);
        var large = Describe(largeTransaction);
        while(large.Weight > availableTransactionWeight)
        {
            var excessBytes = checked((int)
                ((large.Weight.Value - availableTransactionWeight + 3) / 4));
            largeTransaction = CreateTransaction(
                largeTransaction.Outputs[0].ScriptPubKey.Length - excessBytes,
                1);
            large = Describe(largeTransaction);
        }
        while(availableTransactionWeight - large.Weight >= 4)
        {
            var additionalBytes = checked((int)
                ((availableTransactionWeight - large.Weight.Value) / 4));
            largeTransaction = CreateTransaction(
                largeTransaction.Outputs[0].ScriptPubKey.Length +
                additionalBytes, 1);
            large = Describe(largeTransaction);
        }

        var nearLimit = Create(Template(large), "weight-near-limit");
        Assert.InRange(nearLimit.DirectBlockWeight!.Value,
            BitcoinJob.BitcoinConsensusMaxBlockWeight - 3,
            BitcoinJob.BitcoinConsensusMaxBlockWeight);
        var second = Describe(CreateTransaction(1, 2));
        var error = Assert.Throws<InvalidDataException>(() =>
            Create(Template(large, second),
                "weight-over-limit"));
        Assert.Contains("exceeds Bitcoin's", error.Message);
        var missing = Describe(largeTransaction, 1);
        missing.Weight = null;
        var missingWeight = Assert.Throws<InvalidDataException>(() =>
            Create(Template(missing), "weight-missing"));
        Assert.Contains("positive daemon-reported weight",
            missingWeight.Message);
        var mismatched = Describe(CreateTransaction(1, 3));
        mismatched.Weight++;
        var mismatchedWeight = Assert.Throws<InvalidDataException>(() =>
            Create(Template(mismatched), "weight-mismatch"));
        Assert.Contains("does not match serialized weight",
            mismatchedWeight.Message);
        var missingTransactions = Template();
        missingTransactions.Transactions = null;
        var missingTransactionArray = Assert.Throws<InvalidDataException>(() =>
            Create(missingTransactions, "weight-transactions-missing"));
        Assert.Contains("transaction array", missingTransactionArray.Message);
    }

    [Fact]
    public async Task DirectJobBuiltDuringReauthorization_CannotReenterQueue()
    {
        var context = new BitcoinWorkerContext();
        var destinationA = new Key().PubKey.GetAddress(
            ScriptPubKeyType.Segwit, Network.RegTest);
        var destinationB = new Key().PubKey.GetAddress(
            ScriptPubKeyType.Segwit, Network.RegTest);
        var authorizationA = context.SetDirectPayoutAuthorization(
            destinationA.ToString(), destinationA);
        var job = new BitcoinJob();
        var template = typeof(BitcoinJob).GetField("directCoinbaseTemplate",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        template.SetValue(job, new BitcoinDirectCoinbaseTemplate
        {
            AuthorizationGeneration = authorizationA.Generation,
            MinerAddress = destinationA.ToString(),
            MinerDestination = destinationA,
        });
        typeof(BitcoinJob).GetProperty(nameof(BitcoinJob.JobId))!
            .SetValue(job, "stale-direct-job");

        using var built = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var insertion = Task.Run(() =>
        {
            built.Set();
            release.Wait();
            return context.TryAddDirectJob(job, 4);
        });

        Assert.True(built.Wait(TimeSpan.FromSeconds(5)));
        context.SetDirectPayoutAuthorization(destinationB.ToString(),
            destinationB);
        release.Set();

        Assert.False(await insertion);
        Assert.Null(context.GetJob(job.JobId));
        Assert.Equal(destinationB.ToString(), context.DirectPayoutAddress);
    }

    [Fact]
    public async Task DirectSubmissionInProgress_DelaysReauthorizationSuccess()
    {
        var context = new BitcoinWorkerContext();
        var destinationA = new Key().PubKey.GetAddress(
            ScriptPubKeyType.Segwit, Network.RegTest);
        var destinationB = new Key().PubKey.GetAddress(
            ScriptPubKeyType.Segwit, Network.RegTest);
        var authorizationA = context.SetDirectPayoutAuthorization(
            destinationA.ToString(), destinationA);

        await context.EnterDirectPayoutSubmissionAsync(
            CancellationToken.None);
        var submissionGateHeld = true;
        try
        {
            var reauthorization = context.SetDirectPayoutAuthorizationAsync(
                destinationB.ToString(), destinationB,
                CancellationToken.None);

            Assert.False(reauthorization.IsCompleted);
            Assert.Equal(authorizationA,
                context.GetDirectPayoutAuthorization());

            context.ExitDirectPayoutSubmission();
            submissionGateHeld = false;
            var authorizationB = await reauthorization;

            Assert.True(authorizationB.Generation >
                authorizationA.Generation);
            Assert.Equal(destinationB.ToString(),
                context.DirectPayoutAddress);
        }
        finally
        {
            if(submissionGateHeld)
                context.ExitDirectPayoutSubmission();
        }
    }

    [Fact]
    public void DirectOptionOff_PreservesCanonicalCoinbaseBytes()
    {
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["bitcoin"];
        var pc = new PoolConfig
        {
            Coin = "bitcoin",
            Template = coin,
        };
        var blockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
        {
            Version = 0x20000000,
            PreviousBlockhash = new string('0', 64),
            CoinbaseValue = 5_000_000_000,
            Target = "7" + new string('f', 63),
            CurTime = 1_700_000_000,
            Bits = "207fffff",
            Height = 101,
            Transactions = Array.Empty<Miningcore.Blockchain.Bitcoin.DaemonResponses.BitcoinBlockTransaction>(),
        };
        var clock = MockMasterClock.FromTicks(
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcTicks);
        var pool = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit,
            Network.RegTest);
        var legacy = new DirectSerializationBitcoinJob();
        var explicitNull = new DirectSerializationBitcoinJob();

        legacy.Init(blockTemplate, "same", pc, null, new ClusterConfig(),
            clock, pool, Network.RegTest, false, coin.ShareMultiplier,
            coin.CoinbaseHasherValue, coin.HeaderHasherValue,
            coin.BlockHasherValue);
        explicitNull.InitDirect(blockTemplate, "same", pc, null,
            new ClusterConfig(), clock, pool, Network.RegTest, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue,
            coin.HeaderHasherValue, coin.BlockHasherValue, null);

        Assert.Equal(
            legacy.SerializeCoinbaseForTest("00000001",
                "00000000000000"),
            explicitNull.SerializeCoinbaseForTest("00000001",
                "00000000000000"));
        Assert.Equal(JsonConvert.SerializeObject(legacy.GetJobParams(true)),
            JsonConvert.SerializeObject(explicitNull.GetJobParams(true)));
    }

    private (BitcoinJob, StratumConnection) CreateJob(double difficulty = 0.01d)
    {
        var job = new VersionSerializationBitcoinJob();
        var coin = (BitcoinTemplate) ModuleInitializer.CoinTemplates["dash"];
        var pc = new PoolConfig { Template = coin };

        var blockTemplate = JsonConvert.DeserializeObject<Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate>("{\"version\":536870912,\"previousBlockhash\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b\",\"coinbaseValue\":1801475949,\"target\":\"000001d771000000000000000000000000000000000000000000000000000000\",\"nonceRange\":\"00000000ffffffff\",\"curTime\":1665423220,\"bits\":\"1e01d771\",\"height\":813750,\"transactions\":[],\"coinbaseAux\":{\"flags\":null},\"default_witness_commitment\":null,\"capabilities\":[\"proposal\"],\"rules\":[\"csv\",\"dip0001\",\"bip147\",\"dip0003\",\"dip0008\",\"realloc\",\"dip0020\",\"dip0024\"],\"vbavailable\":{},\"vbrequired\":0,\"longpollid\":\"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b814670\",\"mintime\":1665422408,\"mutable\":[\"time\",\"transactions\",\"prevblock\"],\"sigoplimit\":40000,\"sizelimit\":2000000,\"previousbits\":\"1e01bee4\",\"masternode\":[{\"payee\":\"yVXDAM73Tg6A44Bm3qduXsMCYxzuqBCT48\",\"script\":\"76a91464f2b2b84f62d68a2cd7f7f5fb2b5aa75ef716d788ac\",\"amount\":1080885569}],\"masternode_payments_started\":true,\"masternode_payments_enforced\":true,\"superblock\":[],\"superblocks_started\":true,\"superblocks_enabled\":true,\"coinbase_payload\":\"0200b66a0c00fbab6816312c05803d026cce30fec0332c059f66e421ab0bf65b96ea9efb8a22e12cfc31666208b47a006e5b74f95a4c0797b6bc620ea1cc07cb53616e547302\"}", jsonSerializerSettings);
        var clock = MockMasterClock.FromTicks(638010200200475015);
        var poolAddressDestination = BitcoinUtils.AddressToDestination("yNkA6gVSPqKzW6WmJtTazRLKbSkQA5ND2h", Network.TestNet);
        var network = Network.GetNetwork("testnet");

        var context = new BitcoinWorkerContext
        {
            Miner = "yXHmbak4AdgK5vWamwqFtEijn2NpgLvmi4",
            ExtraNonce1 = "60000001",
            Difficulty = difficulty,
            UserAgent = "cpuminer-multi/1.3.1"
        };

        var worker = new StratumConnection(new NullLogger(LogManager.LogFactory), container.Resolve<RecyclableMemoryStreamManager>(), clock, "1", false);
        worker.SetContext(context);

        job.Init(blockTemplate, "1", pc, null, new ClusterConfig(), clock, poolAddressDestination, network, false,
            coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);

        return (job, worker);
    }

    private sealed class VersionValidationMergedJob : MergedMiningBitcoinJob
    {
        public void Configure(Miningcore.Time.IMasterClock masterClock)
        {
            clock = masterClock;
            BlockTemplate = new Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate
            {
                CurTime = 1665423220,
            };
        }
    }

    private sealed class VersionSerializationBitcoinJob : BitcoinJob
    {
        public void SetTemplateVersion(uint version) => BlockTemplate.Version = version;

        public uint SerializeVersion(uint? versionMask, uint? versionBits)
        {
            var header = SerializeHeader(new byte[32], BlockTemplate.CurTime, 0,
                versionMask, versionBits);
            return BinaryPrimitives.ReadUInt32LittleEndian(header);
        }
    }

    private sealed class DirectSerializationBitcoinJob : BitcoinJob
    {
        public TxOutList Outputs => txOut.Outputs;
        public byte[] SerializeCoinbaseForTest(string extraNonce1,
            string extraNonce2) => SerializeCoinbase(extraNonce1,
            extraNonce2);
        public byte[] SerializeBlockForTest(byte[] coinbase) =>
            SerializeBlock(new byte[80], coinbase);
    }

    private sealed class MergedSerializationBitcoinJob : MergedMiningBitcoinJob
    {
        public byte[] SerializeCoinbaseForTest(string extraNonce1,
            string extraNonce2) => SerializeCoinbase(extraNonce1,
            extraNonce2);
    }

    private sealed class SerializationProgpowJob : ProgpowJob
    {
        public byte[] SerializeCoinbaseForTest(string extraNonce1) =>
            SerializeCoinbase(extraNonce1);
    }
}
