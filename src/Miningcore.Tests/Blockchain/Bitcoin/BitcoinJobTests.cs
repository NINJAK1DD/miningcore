using Autofac;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;
using Xunit;
#pragma warning disable 8974

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class BitcoinJobTests : TestBase
{
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
        var job = new BitcoinJob();
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
}
