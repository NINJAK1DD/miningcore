using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Xelis;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Autofac;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Tests.Rpc;
using NSubstitute;
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
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
        Exception bodyFailure = null;
        try
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule<AutofacModule>();
            using var container = builder.Build();
            using var logs = new RpcDiagnosticTests.CapturedLogs();
            var bus = Substitute.For<IMessageBus>();
            var client = new RpcClient(new DaemonEndpointConfig { Host = "127.0.0.1", Port = port, HttpPath = "json_rpc" },
                container.Resolve<JsonSerializerSettings>(), bus, "test");
            async Task<JToken> Rpc(string method, object parameters = null)
            {
                var response = await client.ExecuteAsync<JToken>(logs.Logger, method, deadline.Token, parameters);
                if(response.Error != null) throw response.Error.InnerException ?? new InvalidDataException("Daemon RPC failed");
                return response.Response;
            }
            for(var attempt = 0; ; attempt++)
            {
                Assert.False(daemon.HasExited, daemon.HasExited ? await stdout + await stderr : "");
                try { await Rpc("get_info"); break; }
                catch(HttpRequestException) when(attempt < 100) { await Task.Delay(100, deadline.Token); }
            }
            // Published upstream test address; no wallet, keys, funds or public peers.
            const string address = "xet:06t6uhz9gxk08fpm64ufhmdy2krw9rzzwvenw0ukctwz8l9yvafsqdltctp";
            var consumer = new RpcConsumerDiagnosticTests.XelisConsumer(container, bus, client, logs.Logger, address, "stagenet");
            Assert.True(await consumer.Refresh(deadline.Token), string.Join("\n", logs.Messages));
            var job = consumer.CurrentJob;
            Assert.Equal("xel/v1", job.Algorithm);
            Assert.Equal(new BigInteger(30000), job.ExactDifficulty);
            var work = Convert.FromHexString(job.BlockTemplate.Template);
            var originalHeader = job.BlockHeader.Template;
            var target = XelisConstants.Diff1Target / job.ExactDifficulty;
            Assert.Equal(target, new BigInteger(job.blockTargetBytes, true, true));
            var worker = XelisDifficultyTests.Worker((double) job.ExactDifficulty * 100);
            NonBitcoinCandidateCreditTests.Set(worker, "RemoteEndpoint", new IPEndPoint(IPAddress.Loopback, 12345));
            var issued = job.ForWorker(worker.ContextAs<XelisWorkerContext>());
            worker.ContextAs<XelisWorkerContext>().AddJob(issued, 8);
            Task<Share> Submit(string nonce) => consumer.SubmitShareAsync(worker,
                new object[] { "test", issued.JobId, nonce }, deadline.Token).AsTask();
            Array.Clear(work, 48, 32); // worker extranonce
            var hasher = new XelisHash();
            var digest = new byte[32];
            string solvedNonce = null;
            var rejected = false;
            for(ulong nonce = 0; nonce < 128 && !rejected; nonce++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt64BigEndian(work.AsSpan(40, 8), nonce);
                hasher.Digest(work, digest);
                var proof = new BigInteger(digest, true, true);
                var nonceHex = Convert.ToHexString(work.AsSpan(40, 8));
                if(proof > target)
                {
                    Assert.Equal(StratumError.LowDifficultyShare, (await Assert.ThrowsAsync<StratumException>(() => Submit(nonceHex))).Code);
                    rejected = true;
                }
            }
            Assert.True(rejected);
            // Real PoW is probabilistic. Search disjoint nonce ranges on a bounded
            // number of cores instead of making CI reliability depend on one core.
            var threads = Math.Clamp(Environment.ProcessorCount, 1, 4);
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = deadline.Token }, (index, loop) =>
            {
                var input = (byte[]) work.Clone();
                var proofBytes = new byte[32];
                for(ulong nonce = (ulong) index; nonce < 2000000 && !loop.IsStopped; nonce += (ulong) threads)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    BinaryPrimitives.WriteUInt64BigEndian(input.AsSpan(40, 8), nonce);
                    hasher.Digest(input, proofBytes);
                    if(new BigInteger(proofBytes, true, true) <= target)
                    {
                        Interlocked.CompareExchange(ref solvedNonce, Convert.ToHexString(input.AsSpan(40, 8)), null);
                        loop.Stop();
                    }
                }
            });
            Assert.NotNull(solvedNonce);

            // Keep the immutable challenge fixed while obtaining a newer daemon
            // timestamp. Reconstruct a header whose only changed field is time,
            // then ask the real daemon to derive its corresponding miner work.
            var originalWork = Convert.FromHexString(job.BlockTemplate.Template);
            var oldTimestamp = BinaryPrimitives.ReadUInt64BigEndian(originalWork.AsSpan(32, 8));
            ulong newerTimestamp = oldTimestamp;
            for(var attempt = 0; attempt < 100 && newerTimestamp <= oldTimestamp; attempt++)
            {
                var freshHeader = await Rpc("get_block_template", new { address });
                var bytes = Convert.FromHexString((string) freshHeader["template"]);
                newerTimestamp = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(9, 8));
                if(newerTimestamp <= oldTimestamp) await Task.Delay(10, deadline.Token);
            }
            Assert.True(newerTimestamp > oldTimestamp, "The daemon must supply a later template timestamp");
            var timestampOnlyHeader = Convert.FromHexString(originalHeader);
            BinaryPrimitives.WriteUInt64BigEndian(timestampOnlyHeader.AsSpan(9, 8), newerTimestamp);
            var timestampOnlyResponse = await Rpc("get_miner_work", new { template = Convert.ToHexString(timestampOnlyHeader), address });
            var timestampOnlyWork = Convert.FromHexString((string) timestampOnlyResponse["miner_work"]);
            Assert.Equal(originalWork.AsSpan(0, 32).ToArray(), timestampOnlyWork.AsSpan(0, 32).ToArray());
            Assert.Equal(newerTimestamp, BinaryPrimitives.ReadUInt64BigEndian(timestampOnlyWork.AsSpan(32, 8)));
            Assert.Equal(originalWork.AsSpan(40).ToArray(), timestampOnlyWork.AsSpan(40).ToArray());
            Assert.Equal(job.Algorithm, (string) timestampOnlyResponse["algorithm"]);
            Assert.Equal(job.ExactDifficulty, BigInteger.Parse((string) timestampOnlyResponse["difficulty"]));

            // The production refresh deliberately retains this unchanged work
            // identity. Its originally issued timestamp and proof must still be
            // accepted, not merely parseable by get_miner_work.
            Assert.False(await consumer.Refresh(deadline.Token));
            Assert.Same(job, consumer.CurrentJob);
            var share = await Submit(solvedNonce);
            Assert.True(share.IsBlockCandidate);
            Assert.InRange(share.Difficulty, 29999.999, 30000.001);
            Assert.Equal(StratumError.DuplicateShare, (await Assert.ThrowsAsync<StratumException>(() => Submit(solvedNonce))).Code);
            Assert.Equal(originalHeader, job.BlockHeader.Template);
            var info = await Rpc("get_info");
            Assert.Equal(1UL, (ulong) info["height"]);
            Assert.Equal(1UL, (ulong) info["topoheight"]);
            Assert.Equal(share.BlockHash, (string) info["top_block_hash"]);
        }
        catch(Exception ex) { bodyFailure = ex; throw; }
        finally
        {
            await CleanupAsync(bodyFailure, async () =>
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
                if(bodyFailure == null) Assert.True(daemon.ExitCode == 0, output);
            }, () => Directory.Delete(root, true));
        }
    }

    internal static async Task CleanupAsync(Exception bodyFailure, Func<Task> stop, Action delete)
    {
        var failures = new List<Exception>();
        try { await stop(); } catch(Exception ex) { failures.Add(ex); }
        try { delete(); } catch(Exception ex) { failures.Add(ex); }
        if(bodyFailure == null && failures.Count != 0) throw new AggregateException("Daemon cleanup failed", failures);
    }

    [Fact]
    public async Task CleanupNeverReplacesTheOriginalFailureAndStillAttemptsBothActions()
    {
        var deleted = false;
        Func<Task> stop = () => throw new IOException("stop failed");
        Action delete = () => { deleted = true; throw new IOException("delete failed"); };
        await CleanupAsync(new InvalidOperationException("body failed"), stop, delete);
        Assert.True(deleted);
        var error = await Assert.ThrowsAsync<AggregateException>(() => CleanupAsync(null, stop, delete));
        Assert.Equal(2, error.InnerExceptions.Count);
    }
}
