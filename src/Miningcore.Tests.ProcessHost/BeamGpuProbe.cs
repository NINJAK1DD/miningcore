using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Beam.DaemonResponses;
using Miningcore.Blockchain.Beam.StratumResponses;
using Miningcore.Configuration;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.VarDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Explicitly invoked, bounded loopback commissioning probe. Real GPU solutions
// pass through BeamJob and the native verifier; synthetic inputs never go to a
// daemon, wallet, database or public pool. This is not block-consensus evidence.
internal static class BeamGpuProbe
{
    internal static async Task<int> RunAsync(string portText, string evidencePath,
        string bindText = "127.0.0.1", string peerText = "127.0.0.1")
    {
        if(!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535)
            return 64;
        if(!IPAddress.TryParse(bindText, out var bind) || !IPAddress.TryParse(peerText, out var peer) ||
            !PrivateLabAddress(bind) || !PrivateLabAddress(peer))
            return 64;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var clock = new StandardClock();
        var context = new BeamWorkerContext { ExtraNonce1 = "000001", IsAuthorized = true, IsSubscribed = true };
        var options = new VarDiffConfig { MinDiff = 8, MaxDiff = 32, TargetTime = 60, RetargetTime = 0, VariancePercent = 1 };
        context.Init(8, options, clock);
        var worker = new StratumConnection(NLog.LogManager.GetLogger("beam-gpu-probe"),
            new Microsoft.IO.RecyclableMemoryStreamManager(), clock, "private-beam-gpu", true);
        worker.SetContext(context);
        var source = MakeJob("12345", new string('0', 64));
        var job = source.ForWorker(context);
        context.AddJob(job, 4);
        var stages = new int[3];
        var stage = 0;
        var rejected = 0;
        var duplicateChecks = 0;
        var proofs = new List<object>();
        var listener = new TcpListener(bind, port);
        listener.Start(1);
        Console.WriteLine($"READY beam-gpu {bind}:{port} permitted-peer={peer} duration=180s");
        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            if(!((IPEndPoint) client.Client.RemoteEndPoint).Address.Equals(peer))
                throw new InvalidDataException("Unpermitted lab peer");
            client.NoDelay = true;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            async Task Send(object response) => await writer.WriteLineAsync(JsonConvert.SerializeObject(response).AsMemory(), ct);
            async Task Publish()
            {
                await Send(new BeamJobResponse
                {
                    Id = job.JobId, Height = job.BlockTemplate.Height,
                    Difficulty = BeamUtils.PackedDifficulty(context.Difficulty),
                    Input = job.BlockTemplate.Input, Nonceprefix = context.ExtraNonce1,
                });
                Console.WriteLine($"JOB stage={stage} id={job.JobId} difficulty={context.Difficulty}");
            }
            while(!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if(line == null) break;
                if(line.Length > 4096) throw new InvalidDataException("Oversized probe request");
                var request = JObject.Parse(line);
                if(request.Value<string>("method") == "login")
                {
                    VarDiffManager.Update(context, options, clock);
                    await Send(new BeamLoginResponse { Code = 0, Description = "private lab", Nonceprefix = context.ExtraNonce1,
                        Forkheight = 0, Forkheight2 = 0 });
                    await Publish();
                    continue;
                }
                if(request.Value<string>("method") != "solution") continue;
                var id = request.Value<string>("id");
                var nonce = request.Value<string>("nonce");
                var solution = request.Value<string>("output");
                var submitted = context.GetJob(id);
                short code = BeamConstants.BeamRpcJobNotFound;
                if(submitted != null)
                {
                    using var receipt = context.UseShareReceipt(context.CaptureShareReceipt());
                    var result = submitted.ProcessShare(worker, nonce, solution);
                    code = result.stratumError;
                    if(code == BeamConstants.BeamRpcShareAccepted)
                    {
                        if(result.Share.Difficulty != context.Difficulty || result.Share.IsBlockCandidate)
                            throw new InvalidDataException("Unexpected credit or candidate in ordinary GPU proof");
                        var duplicate = submitted.ProcessShare(worker, nonce, solution);
                        if(duplicate.stratumError != BeamConstants.BeamRpcDuplicateShare)
                            throw new InvalidDataException("GPU proof was credited twice");
                        duplicateChecks++;
                        stages[stage]++;
                        proofs.Add(new { stage, id, nonce, solution, credit = result.Share.Difficulty });
                        Console.WriteLine($"ACCEPT stage={stage} id={id} credit={result.Share.Difficulty}");
                    }
                    else rejected++;
                }
                await Send(new BeamSubmitResponse { Id = id, Code = code, Description = code == 1 ? "accepted" : "rejected" });
                if(stages[stage] < 3) continue;
                if(stage == 2) break;
                stage++;
                // Clamp the bounded lab retargets while exercising the real
                // monotonic VarDiff calculation and pending-assignment boundary.
                options.MaxDiff = stage == 1 ? 32 : 16;
                var nextDifficulty = VarDiffManager.Update(context, options, clock);
                if(nextDifficulty != (stage == 1 ? 32 : 16))
                    throw new InvalidDataException("GPU cadence did not trigger the expected bounded VarDiff retarget");
                context.EnqueueNewDifficulty(nextDifficulty.Value);
                if(!context.ApplyPendingDifficulty())
                    throw new InvalidDataException("VarDiff assignment did not apply");
                if(stage == 2) source = MakeJob("12346", new string('1', 64));
                job = source.ForWorker(context);
                context.AddJob(job, 4);
                await Publish();
            }
        }
        catch(OperationCanceledException)
        {
            Console.Error.WriteLine("Beam GPU probe deadline expired");
        }
        finally
        {
            listener.Stop();
            await File.WriteAllTextAsync(evidencePath, JsonConvert.SerializeObject(new
            {
                passed = stages.All(x => x >= 3), stages, rejected, duplicateChecks,
                scope = "Real GPU BeamHash III ordinary proofs, production BeamJob/native verifier; no daemon consensus or durable credit",
                retarget = "Production VarDiffManager and pending assignment; bounded maxDiff 32 then 16",
                proofs,
            }, Formatting.Indented));
        }
        return stages.All(x => x >= 3) ? 0 : 1;
    }

    private static BeamJob MakeJob(string id, string input)
    {
        var job = new BeamJob();
        job.Init(new BeamBlockTemplate
        {
            JobId = id, Height = 1000000, Input = input, PowType = 2,
            PackedDifficulty = BeamUtils.PackedDifficulty(1_000_000_000),
        }, id, new PoolConfig { Template = new BeamCoinTemplate() }, new ClusterConfig(), new StandardClock(), new BeamHash());
        return job;
    }

    private static bool PrivateLabAddress(IPAddress address)
    {
        if(address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}
