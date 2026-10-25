using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Mining;

public class XelisDaemonTests
{
    private sealed class DaemonFactAttribute : FactAttribute
    {
        public DaemonFactAttribute()
        {
            if(!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_TEST_XELISD")))
                Skip = "Requires Linux native hashing and MININGCORE_TEST_XELISD (pinned Xelis v1.25.0)";
        }
    }

    [DaemonFact]
    public async Task DisposableStagenetAcceptsPoolProofAndRejectsLowDifficulty()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var root = Path.Combine(Path.GetTempPath(), "miningcore-xelis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MININGCORE_TEST_XELISD"))
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach(var arg in new[] { "--network", "stagenet", "--disable-p2p-server", "--rpc-bind-address", $"127.0.0.1:{port}",
            "--dir-path", root + "/", "--disable-file-logging", "--disable-interactive-mode", "--disable-ascii-art",
            "--rpc-threads", "2", "--use-db-backend", "sled" })
            start.ArgumentList.Add(arg);
        using var daemon = Process.Start(start);
        var stdout = daemon.StandardOutput.ReadToEndAsync();
        var stderr = daemon.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            async Task<JToken> Rpc(string method, object parameters = null)
            {
                using var content = new StringContent(JsonConvert.SerializeObject(new { jsonrpc = "2.0", id = 1, method, @params = parameters }),
                    System.Text.Encoding.UTF8, "application/json");
                using var response = await http.PostAsync("json_rpc", content, deadline.Token);
                response.EnsureSuccessStatusCode();
                var envelope = JObject.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                Assert.True(envelope["error"] == null || envelope["error"].Type == JTokenType.Null, envelope.ToString());
                return envelope["result"];
            }
            for(var attempt = 0; ; attempt++)
            {
                Assert.False(daemon.HasExited, daemon.HasExited ? await stdout + await stderr : "");
                try { await Rpc("get_info"); break; }
                catch(HttpRequestException) when(attempt < 100) { await Task.Delay(100, deadline.Token); }
            }
            // Published upstream test address; no wallet, keys, funds or public peers.
            const string address = "xet:06t6uhz9gxk08fpm64ufhmdy2krw9rzzwvenw0ukctwz8l9yvafsqdltctp";
            var header = (await Rpc("get_block_template", new { address })).ToObject<GetBlockHeaderResponse>();
            var template = (await Rpc("get_miner_work", new { template = header.Template, address })).ToObject<GetBlockTemplateResponse>();
            Assert.Equal("xel/v1", template.Algorithm);
            Assert.Equal(new BigInteger(30000), template.Difficulty);
            var work = Convert.FromHexString(template.Template);
            var job = new XelisJob();
            job.Init(header, template, "live", new MockMasterClock(), "stagenet", 1, Convert.ToHexString(work.AsSpan(0, 32)));
            var target = XelisConstants.Diff1Target / template.Difficulty;
            Assert.Equal(target, new BigInteger(job.blockTargetBytes, true, true));
            var worker = XelisDifficultyTests.Worker((double) template.Difficulty * 100);
            var issued = job.ForWorker(worker.ContextAs<XelisWorkerContext>());
            Array.Clear(work, 48, 32); // worker extranonce
            var hasher = new XelisHash();
            var digest = new byte[32];
            string solvedNonce = null;
            var rejected = false;
            for(ulong nonce = 0; nonce < 2000000; nonce++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt64BigEndian(work.AsSpan(40, 8), nonce);
                hasher.Digest(work, digest);
                var proof = new BigInteger(digest, true, true);
                var nonceHex = Convert.ToHexString(work.AsSpan(40, 8));
                if(!rejected && proof > target)
                {
                    Assert.Equal(StratumError.LowDifficultyShare, Assert.Throws<StratumException>(() => issued.ProcessShare(worker, nonceHex)).Code);
                    rejected = true;
                }
                if(proof <= target)
                {
                    solvedNonce = nonceHex;
                    break;
                }
            }
            Assert.True(rejected);
            Assert.NotNull(solvedNonce);
            var share = issued.ProcessShare(worker, solvedNonce);
            Assert.True(share.IsBlockCandidate);
            Assert.InRange(share.Difficulty, 29999.999, 30000.001);
            Assert.Equal(StratumError.DuplicateShare, Assert.Throws<StratumException>(() => issued.ProcessShare(worker, solvedNonce)).Code);
            Assert.True((bool) await Rpc("submit_block", new { block_template = issued.BlockHeader.Template }));
            var info = await Rpc("get_info");
            Assert.Equal(1UL, (ulong) info["height"]);
            Assert.Equal(1UL, (ulong) info["topoheight"]);
        }
        finally
        {
            if(!daemon.HasExited)
            {
                var stop = new ProcessStartInfo("/bin/kill") { UseShellExecute = false };
                stop.ArgumentList.Add("-INT");
                stop.ArgumentList.Add(daemon.Id.ToString());
                using var signal = Process.Start(stop);
                await signal.WaitForExitAsync();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await daemon.WaitForExitAsync(shutdown.Token); }
                catch(OperationCanceledException) { daemon.Kill(true); await daemon.WaitForExitAsync(); }
            }
            var output = await stdout + await stderr;
            Directory.Delete(root, true);
            Assert.True(daemon.ExitCode == 0, output);
        }
    }
}
