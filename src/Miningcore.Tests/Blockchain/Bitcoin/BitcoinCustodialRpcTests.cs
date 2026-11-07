using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Miningcore.Configuration;
using Miningcore.Persistence.Model;
using Miningcore.Tests.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public partial class BitcoinPayoutHandlerTests
{
    [Theory]
    [InlineData("nexa", "txid", "valid", "generate", 101, false)]
    [InlineData("nexa", "txid", "valid", "generate", 102, true)]
    [InlineData("nexa", "txid", "valid", "generate", 103, true)]
    [InlineData("nexa", "txid", "valid", "immature", 103, false)]
    [InlineData("nexa", "txid", "wrong-coinbase", "generate", 102, false)]
    [InlineData("nexa", "txid", "wrong-hash", "generate", 102, false)]
    [InlineData("nexa", "txid", "wrong-height", "generate", 102, false)]
    [InlineData("nexa", "txid", "below-depth", "generate", 102, false)]
    [InlineData("nexa", "txid", "inactive", "generate", 102, false)]
    [InlineData("nexa", "txid", "empty", "generate", 102, false)]
    [InlineData("nexa", "txid", "malformed", "generate", 102, false)]
    [InlineData("nexa", "txid", "null-response", "generate", 102, false)]
    [InlineData("nexa", "txid", "rpc-error", "generate", 102, false)]
    [InlineData("nexa", "tx", "valid", "generate", 102, false)]
    [InlineData("nexa", "txidem", "valid", "generate", 102, false)]
    [InlineData("bitcoin", "tx", "valid", "generate", 102, true)]
    [InlineData("bitcoin", "txid", "valid", "generate", 102, false)]
    public async Task CustodialRpc_UsesFamilyTransactionIdsWithoutWeakeningSettlement(
        string coin, string transactionField, string variant, string category,
        int confirmations, bool confirmed)
    {
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JToken.Parse(await reader.ReadToEndAsync());
            JObject Reply(JToken command)
            {
                JToken result;
                JToken error = JValue.CreateNull();
                switch(command.Value<string>("method"))
                {
                    case "gettransaction":
                        Assert.Equal("coinbase-txid", command["params"][0].Value<string>());
                        result = SuccessTransaction(category, 50m, confirmations)[0].Response;
                        break;
                    case "getblock":
                        Assert.Equal("doge-block", command["params"][0].Value<string>());
                        result = new JObject
                        {
                            ["hash"] = variant == "wrong-hash" ? "other" : "doge-block",
                            ["height"] = variant == "wrong-height" ? 101 : 100,
                            ["confirmations"] = variant == "inactive" ? -1 :
                                variant == "below-depth" ? 101 : confirmations,
                            [transactionField] = variant == "empty" ? new JArray() :
                                variant == "malformed" ? new JObject() :
                                new JArray(variant == "wrong-coinbase" ? "other" : "coinbase-txid"),
                        };
                        if(transactionField == "txid")
                            result["txidem"] = new JArray("different-idem");
                        if(variant == "null-response") result = JValue.CreateNull();
                        if(variant == "rpc-error")
                        {
                            // A plausible payload must not override an RPC error.
                            error = new JObject { ["code"] = -5, ["message"] = "unavailable" };
                        }
                        break;
                    default:
                        throw new Xunit.Sdk.XunitException("Unexpected RPC method");
                }
                return new JObject { ["id"] = command["id"], ["result"] = result, ["error"] = error };
            }
            JToken response = request is JArray batch
                ? new JArray(batch.Select(Reply)) : Reply(request);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response.ToString(Formatting.None));
        });

        var fixture = await CreateFixtureAsync(coinTemplate: coin, minimumConfirmations: 102);
        fixture.Config.Daemons = new[] { server.Endpoint() };
        fixture.Handler.UseDaemonRpc = true;
        await fixture.Handler.ConfigureAsync(new ClusterConfig(), fixture.Config, CancellationToken.None);
        var block = PendingBlock("coinbase-txid");
        using var deadline = new CancellationTokenSource(System.TimeSpan.FromSeconds(10));
        Assert.Single(await fixture.Handler.ClassifyBlocksAsync(fixture.Pool,
            new[] { block }, deadline.Token));
        Assert.Equal(confirmed ? BlockStatus.Confirmed : BlockStatus.Pending, block.Status);
        Assert.Equal(confirmed, block.NotifyBlockUnlockedOnUpdate);
        Assert.Equal(50m, block.Reward);
        Assert.Equal("coinbase-txid", block.TransactionConfirmationData);
        Assert.Equal(category == "generate" && confirmations >= 102 ? 1 : 0, fixture.Handler.BlockCalls);
        Assert.Equal(1, fixture.Handler.TransactionCalls);
        if(confirmed) Assert.Equal(1, block.ConfirmationProgress);
        else Assert.InRange(block.ConfirmationProgress, 0, 0.999d);
    }
}
