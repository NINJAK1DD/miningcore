using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[Collection("Pearl proof worker processes")]
public class PearlCandidateNodeTests
{
    [PearlNodeFact]
    public async Task RealNodeDuplicateOrphanStaysUncertainUntilItsParentArrives()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var donor = new Node(); await using var receiver = new Node();
        await donor.Ready(deadline.Token); await receiver.Ready(deadline.Token);
        var generated = await donor.Call("generate", new object[] {2}, deadline.Token);
        Assert.Null(generated.Error);
        var parent = generated.Response[0].Value<string>();
        var child = generated.Response[1].Value<string>();
        var parentBlock = (await donor.Call("getblock", new object[] {parent, 0}, deadline.Token)).Response.Value<string>();
        var childBlock = (await donor.Call("getblock", new object[] {child, 0}, deadline.Token)).Response.Value<string>();
        var bytes = Convert.FromHexString(childBlock);
        // First submit stores the orphan; the indexed header remains unknown.
        Assert.Null((await receiver.Call("submitblock", new object[] {childBlock}, deadline.Token)).Error);
        Assert.Equal(-5, (await receiver.Call("getblockheader", new object[] {child, false}, deadline.Token)).Error?.Code);
        for(var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(PearlCandidateOutcome.Uncertain,
                await PearlCandidateSubmission.ReconcileAsync(child, bytes, 2, receiver.Call, deadline.Token));
            Assert.Contains("orphan", receiver.LastSubmission, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null((await receiver.Call("submitblock", new object[] {parentBlock}, deadline.Token)).Error);
        Assert.Equal(PearlCandidateOutcome.Accepted,
            await PearlCandidateSubmission.ReconcileAsync(child, bytes, 2, receiver.Call, deadline.Token));
        Assert.Equal(child, (await receiver.Call("getbestblockhash", null, deadline.Token)).Response.Value<string>());
    }

    private sealed class Node : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "pearl-orphan-" + Guid.NewGuid().ToString("N"));
        private readonly Process process;
        private readonly HttpClient http = new() {Timeout = TimeSpan.FromMinutes(3)};
        internal string LastSubmission;

        internal Node()
        {
            Directory.CreateDirectory(directory);
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start(); var port = ((IPEndPoint) reservation.LocalEndpoint).Port;
            var secret = Guid.NewGuid().ToString("N");
            http.BaseAddress = new Uri($"http://127.0.0.1:{port}");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("lab:" + secret)));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("MININGCORE_TEST_PEARLD"))
                {UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true};
            foreach(var arg in new[] {"--regtest", "--datadir=" + directory, "--logdir=" + directory,
                "--rpclisten=127.0.0.1:" + port, "--listen=127.0.0.1:0", "--rpcuser=lab", "--rpcpass=" + secret,
                "--notls", "--miningaddr=" + PearlWorkTests.Address}) start.ArgumentList.Add(arg);
            start.Environment["RAYON_NUM_THREADS"] = "2";
            reservation.Stop();
            process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start isolated Pearl node");
            // Discard private node diagnostics and continuously drain both pipes.
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }

        internal async Task Ready(CancellationToken ct)
        {
            for(var i = 0; i < 100; i++)
            {
                if(process.HasExited) throw new InvalidOperationException("Isolated Pearl node exited during startup");
                try { if((await Call("getblockcount", null, ct)).Error == null) return; }
                catch(HttpRequestException) { }
                await Task.Delay(100, ct);
            }
            throw new TimeoutException("Isolated Pearl node did not start");
        }

        internal async Task<RpcResponse<JToken>> Call(string method, object parameters, CancellationToken ct)
        {
            using var content = new StringContent(JsonConvert.SerializeObject(new {jsonrpc = "1.0", id = 1, method, @params = parameters ?? Array.Empty<object>()}), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("", content, ct);
            response.EnsureSuccessStatusCode();
            var result = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
            if(method == "submitblock") LastSubmission = result["result"]?.Value<string>();
            return new RpcResponse<JToken>(result["result"], result["error"]?.Type == JTokenType.Object ? result["error"].ToObject<JsonRpcError>() : null);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if(!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { process.Dispose(); http.Dispose(); Directory.Delete(directory, true); }
        }
    }
}

internal sealed class PearlNodeFactAttribute : FactAttribute
{
    public PearlNodeFactAttribute()
    {
        if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MININGCORE_TEST_PEARLD")))
            Skip = "Set MININGCORE_TEST_PEARLD to the pinned Pearl 1.5.5 executable for isolated node tests";
    }
}
