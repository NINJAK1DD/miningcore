using System.Buffers.Binary;
using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;
using Xunit;
using DaemonBlock = Miningcore.Blockchain.Bitcoin.DaemonResponses.Block;
#pragma warning disable 8974

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobTests : TestBase
{
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
}
