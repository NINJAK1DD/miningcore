using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Security.Cryptography;
using Miningcore.Blockchain.Pearl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[Collection("Pearl proof worker processes")]
public class PearlLiveLabTests
{
    // Explicit lab-only lane. This does not certify the production Stratum/accounting path.
    [PearlLiveLabFact]
    [Trait("Category", "PearlLiveLab")]
    public async Task RealGpuProof_BuildsBlockAcceptedByProofEnabledRegtest()
    {
        if(Environment.GetEnvironmentVariable("MININGCORE_PEARL_LIVE_LAB") != "1")
            return;
        var labRoot = Environment.GetEnvironmentVariable("MININGCORE_PEARL_LAB_ROOT")
            ?? throw new InvalidOperationException("MININGCORE_PEARL_LAB_ROOT is required");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("pearl-lab:pearl-lab-only")));
        async Task<JToken> Rpc(int port, string method, params object[] parameters)
        {
            using var content = new StringContent(JsonConvert.SerializeObject(new { jsonrpc = "1.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"http://127.0.0.1:{port}", content, ct);
            response.EnsureSuccessStatusCode();
            var json = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.True(json["error"]?.Type is null or JTokenType.Null, json["error"]?.ToString());
            return json["result"];
        }

        Assert.Equal("78d0ab27df6a51a8e9dbbdefffca1ebbbeb3818cb083ce2c2f43b047247e5832",
            (await Rpc(19501, "getblockhash", 0)).Value<string>());
        var address = (await Rpc(19502, "getnewaddress")).Value<string>();
        var templateJson = await Rpc(19501, "getblocktemplate",
            new { rules = new[] {"segwit"}, capabilities = new[] {"coinbasevalue"} });
        var template = templateJson.ToObject<PearlBlockTemplate>();
        var job = new PearlBlockJob(template, address, PearlNetwork.Regtest, RandomNumberGenerator.GetBytes(24));
        var header = job.Header.ToArray();
        var target = BigInteger.Parse("0000000000ffffffffffffffffffffffffffffffffffffffffffffffffffffff",
            System.Globalization.NumberStyles.HexNumber);
        var verifier = new PearlProofVerifier(
            Path.Combine(labRoot, "sources/pearl/zk-pow/target/release/miningcore-pearl-worker"));
        await verifier.CheckVersionAsync(ct);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        using var miner = new Process
        {
            StartInfo = new ProcessStartInfo(
                "/opt/miningcore-test-lab/miners/srbminer-multi-3.7.3/SRBMiner-Multi-3-7-3/SRBMiner-MULTI")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            }
        };
        foreach(var arg in new[] {"--algorithm","pearlhash","--pool",$"127.0.0.1:{port}",
            "--wallet",address+".integration","--password","x","--disable-cpu","--gpu-id","0"})
            miner.StartInfo.ArgumentList.Add(arg);
        miner.Start();
        // Consume diagnostics without printing identities/proofs into test results.
        var stdout = miner.StandardOutput.ReadToEndAsync(ct);
        var stderr = miner.StandardError.ReadToEndAsync(ct);
        byte[] proof;
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) {AutoFlush = true};
            var auth = JObject.Parse(await reader.ReadLineAsync(ct));
            Assert.Equal("mining.authorize", auth.Value<string>("method"));
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new {
                id = (object) null, method = "mining.notify",
                @params = new { job_id = "miningcore-lab", header = Convert.ToHexString(header).ToLowerInvariant(),
                    target = target.ToString("x64"), height = template.Height, cert_version = 3 }
            }));
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new {id = auth["id"], result = true, error = (object) null}));
            var submission = JObject.Parse(await reader.ReadLineAsync(ct));
            Assert.Equal("mining.submit", submission.Value<string>("method"));
            Assert.Equal("miningcore-lab", submission["params"].Value<string>("job_id"));
            proof = PearlProofEncoding.Decode(submission["params"].Value<string>("plain_proof"));
            await writer.WriteLineAsync(JsonConvert.SerializeObject(new {
                id = submission["id"], result = (object) null,
                error = new { code = 100, msg = "isolated integration capture" }
            }));
        }
        finally
        {
            if(!miner.HasExited)
                miner.Kill(entireProcessTree: true);
            await miner.WaitForExitAsync(CancellationToken.None);
        }

        var verification = await verifier.VerifyAsync(header, target, proof, false, ct);
        Assert.True(verification.IsBlockCandidate);
        var certificate = await verifier.VerifyAsync(header, target, proof, true, ct);
        var block = job.BuildBlock(certificate);
        var hash = SHA256.HashData(SHA256.HashData(
            PearlConsensus.SerializeCertifiedHeader(header, 3, certificate.PublicData, certificate.Certificate)[^108..]));
        Array.Reverse(hash);
        var expectedHash = Convert.ToHexString(hash).ToLowerInvariant();
        var submitted = await Rpc(19501, "submitblock", Convert.ToHexString(block));
        Assert.Equal(JTokenType.Null, submitted.Type);
        Assert.Equal(expectedHash, (await Rpc(19501, "getblockhash", template.Height)).Value<string>());
        var confirmed = await Rpc(19501, "getblock", expectedHash);
        Assert.Equal(1, confirmed.Value<int>("confirmations"));
        Assert.Equal(job.CoinbaseId, confirmed["tx"][0].Value<string>());
        var evidence = new {
            source = PearlProofVerifier.ExpectedVersion, template.Height, blockHash = expectedHash,
            coinbaseId = job.CoinbaseId, rewardGrains = job.RewardGrains,
            proofBytes = proof.Length, certificateBytes = certificate.Certificate.Length,
            network = "regtest", productionPoolTest = false
        };
        await File.WriteAllTextAsync(Path.Combine(labRoot, "evidence/accepted-block.json"),
            JsonConvert.SerializeObject(evidence, Formatting.Indented), ct);
    }
}



internal sealed class PearlLiveLabFactAttribute : FactAttribute
{
    public PearlLiveLabFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("MININGCORE_PEARL_LIVE_LAB") != "1")
            Skip = "Requires the isolated Pearl GPU/node/wallet lab; see docs/pearl-development.md";
    }
}
