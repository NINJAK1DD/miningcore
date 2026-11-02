using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Payments;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Pearl;

[Collection("Pearl proof worker processes")]
public class PearlNoIndexLiveTests
{
    [PearlNoIndexLabFact]
    public async Task ConfirmedDuringDowntimeReconcilesWithoutTxIndexOrSecondWalletSend()
    {
        var path = Environment.GetEnvironmentVariable("MININGCORE_PEARL_NOINDEX_CONFIG");
        var config = JObject.Parse(await File.ReadAllTextAsync(path));
        var node = config["node"].ToObject<DaemonEndpointConfig>();
        var wallet = config["wallet"].ToObject<DaemonEndpointConfig>();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var http = new HttpClient {Timeout = TimeSpan.FromMinutes(2)};
        async Task<JObject> Rpc(DaemonEndpointConfig endpoint, string method, params object[] parameters)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"http://{endpoint.Host}:{endpoint.Port}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(endpoint.User + ":" + endpoint.Password)));
            message.Content = new StringContent(JsonConvert.SerializeObject(new {jsonrpc = "1.0", id = 1, method, @params = parameters}), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(message, stop.Token);
            response.EnsureSuccessStatusCode();
            return JObject.Parse(await response.Content.ReadAsStringAsync(stop.Token));
        }
        await using var lab = await PearlPayoutIntegrationTests.Lab.Create(true);
        lab.Config.Address = config.Value<string>("rewardAddress");
        lab.Config.PaymentProcessing.Extra = new Dictionary<string, object> {{"walletPassword", config.Value<string>("walletPassphrase")}};
        lab.Config.Daemons = new[] {node, wallet};
        var recipients = await lab.Seed(1, .1m);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); var port = ((IPEndPoint) reservation.LocalEndpoint).Port; reservation.Stop();
        using var proxy = new HttpListener();
        proxy.Prefixes.Add($"http://127.0.0.1:{port}/"); proxy.Start();
        var sends = 0;
        string transactionId = null;
        var serving = Task.Run(async () =>
        {
            try
            {
                while(proxy.IsListening)
                {
                    var incoming = await proxy.GetContextAsync();
                    using var reader = new StreamReader(incoming.Request.InputStream);
                    var request = JObject.Parse(await reader.ReadToEndAsync());
                    var parameters = request["params"]?.ToObject<object[]>() ?? Array.Empty<object>();
                    var response = await Rpc(wallet, request.Value<string>("method"), parameters);
                    if(request.Value<string>("method") == "sendmany")
                    {
                        sends++;
                        Assert.True(response["error"]?.Type is null or JTokenType.Null);
                        transactionId = response.Value<string>("result");
                        incoming.Response.Abort(); // Real broadcast, deliberately lost acknowledgement.
                        continue;
                    }
                    response["id"] = request["id"];
                    var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                    incoming.Response.ContentLength64 = bytes.Length;
                    await incoming.Response.OutputStream.WriteAsync(bytes, stop.Token);
                    incoming.Response.Close();
                }
            }
            catch(Exception ex) when(ex is HttpListenerException or ObjectDisposedException) { }
        });
        try
        {
            lab.Config.Daemons = new[] {node, new DaemonEndpointConfig {Category = "wallet", Host = "127.0.0.1", Port = port}};
            var handler = await lab.Handler();
            await Assert.ThrowsAsync<PayoutOutcomeUncertainException>(() => handler.PayoutAsync(null, recipients, stop.Token));
            Assert.Equal(1, sends);
            Assert.NotNull(transactionId);
            Assert.Equal(1, await lab.Count("pearl_payout_intents"));
            Assert.Equal(0, await lab.Count("payments"));
            var mined = await Rpc(node, "generate", 1);
            Assert.True(mined["error"]?.Type is null or JTokenType.Null);
            JObject transaction;
            do
            {
                await Task.Delay(250, stop.Token);
                transaction = await Rpc(wallet, "gettransaction", transactionId);
            } while(transaction["result"]?.Value<int>("confirmations") is null or < 1);
            var missing = await Rpc(node, "getrawtransaction", transactionId, 1);
            Assert.Equal(-5, missing["error"].Value<int>("code")); // Actual node has txindex disabled.
            handler = await lab.Handler(); // Recreate production handler after confirmation.
            await handler.PayoutAsync(null, recipients, stop.Token);
            Assert.Equal(1, sends);
            Assert.Equal(1, await lab.Count("payments"));
            Assert.Equal(1, await lab.Count("payment_batches"));
            Assert.Equal(0, await lab.Count("pearl_payout_intents"));
            await handler.PayoutAsync(null, Array.Empty<Miningcore.Persistence.Model.Balance>(), stop.Token);
            Assert.Equal(1, sends);
        }
        finally { proxy.Close(); await serving; }
    }
}

internal sealed class PearlNoIndexLabFactAttribute : FactAttribute
{
    public PearlNoIndexLabFactAttribute()
    {
        if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MININGCORE_PEARL_NOINDEX_CONFIG")))
            Skip = "Requires isolated proof-enabled Pearl/Oyster without txindex and a real PostgreSQL ledger";
    }
}
