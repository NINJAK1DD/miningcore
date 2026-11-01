using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Blockchain.Pearl;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

public class PearlJobManagerTests
{
    private sealed class Node : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        internal readonly int Port;
        internal string Genesis = PearlJobManager.Genesis(PearlNetwork.Regtest);
        internal bool Syncing;
        internal JObject Template;
        internal int Calls;
        internal Node()
        {
            using var stream = GetType().Assembly.GetManifestResourceStream("Miningcore.Tests.Fixtures.Pearl.regtest-template.json");
            using var reader = new StreamReader(stream);
            Template = JObject.Parse(reader.ReadToEnd());
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint) reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            loop = Serve();
        }
        internal DaemonEndpointConfig Endpoint() => new() {Host = "127.0.0.1", Port = Port, User = "test", Password = "test"};
        private async Task Serve()
        {
            try
            {
                while(listener.IsListening)
                {
                    var request = await listener.GetContextAsync();
                    using var reader = new StreamReader(request.Request.InputStream);
                    var value = JObject.Parse(await reader.ReadToEndAsync());
                    Calls++;
                    JToken result = value.Value<string>("method") switch
                    {
                        "getblockhash" => Genesis,
                        "getblockchaininfo" => new JObject {["blocks"] = Template.Value<long>("height") - 1, ["initialblockdownload"] = Syncing},
                        "getblocktemplate" => Template.DeepClone(),
                        _ => throw new InvalidOperationException("Unexpected Pearl RPC")
                    };
                    var bytes = Encoding.UTF8.GetBytes(new JObject {["id"] = value["id"], ["result"] = result, ["error"] = null}.ToString(Formatting.None));
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
            }
            catch(Exception ex) when(ex is HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() {listener.Close(); await loop;}
    }
    private sealed class Clock : TimeProvider
    {
        internal long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }

    [Fact]
    public async Task FailoverChecksGenesisSameTipChangesAndUnsupportedUpgradesRetireWork()
    {
        await using var wrong = new Node {Genesis = new string('0',64)};
        await using var good = new Node();
        var clock = new Clock();
        var config = new PoolConfig {Id = "pearl", Address = PearlWorkTests.Address, Daemons = new[] {wrong.Endpoint(), good.Endpoint()}};
        var manager = new PearlJobManager(config, PearlNetwork.Regtest, new JsonSerializerSettings(), Substitute.For<IMessageBus>(), LogManager.CreateNullLogger(), clock);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await manager.RefreshAsync(stop.Token));
        Assert.True(manager.Ready);
        Assert.Equal(1, wrong.Calls); // never request work from another network
        var original = manager.Current;
        var job = manager.CreateJob();
        Assert.False(await manager.RefreshAsync(stop.Token));
        good.Template["curtime"] = good.Template.Value<long>("curtime") + 1;
        Assert.True(await manager.RefreshAsync(stop.Token));
        Assert.NotEqual(original.Fingerprint, manager.Current.Fingerprint);
        Assert.Equal(original.Tip, manager.Current.Tip);
        Assert.Equal(job.PreviousBlockHash, original.Tip);
        clock.Seconds = 45;
        Assert.False(manager.Ready);
        Assert.Throws<InvalidOperationException>(() => manager.CreateJob());
        Assert.False(await manager.RefreshAsync(stop.Token));
        Assert.True(manager.Ready);
        good.Syncing = true;
        Assert.False(await manager.RefreshAsync(stop.Token));
        Assert.NotNull(manager.Current);
        Assert.True(manager.Ready);
        clock.Seconds += 45;
        Assert.False(manager.Ready);
        good.Syncing = false;
        Assert.False(await manager.RefreshAsync(stop.Token));
        Assert.True(manager.Ready);
        good.Template["requiredcertversion"] = 4;
        Assert.False(await manager.RefreshAsync(stop.Token));
        Assert.False(manager.Ready);
        good.Syncing = true;
        Assert.False(await manager.RefreshAsync(stop.Token));
        Assert.False(manager.Ready); // Outage cannot reopen work after an incompatible upgrade.
        good.Syncing = false;
        good.Template["requiredcertversion"] = 3;
        good.Template["previousblockhash"] = new string('a',64);
        Assert.True(await manager.RefreshAsync(stop.Token));
        Assert.NotEqual(job.PreviousBlockHash, manager.CreateJob().PreviousBlockHash);
        Assert.Equal(original.Tip, job.PreviousBlockHash);
    }
}
