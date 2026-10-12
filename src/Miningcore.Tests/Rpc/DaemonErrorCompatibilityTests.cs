using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Rpc;

[Collection(RpcDiagnosticCollection.Name)]
public class DaemonErrorCompatibilityTests
{
    public static IEnumerable<object[]> ReviewedDaemonEnvelopes()
    {
        // Source-shaped offline fixtures, not claimed live daemon captures.
        var profiles = JArray.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Rpc", "daemon-error-envelopes.json")));
        foreach(var profile in profiles.Cast<JObject>())
        {
            yield return new object[] { profile["name"].Value<string>(), false, profile.ToString(Formatting.None) };
            yield return new object[] { profile["name"].Value<string>(), true, profile.ToString(Formatting.None) };
        }
    }

    [Theory]
    [MemberData(nameof(ReviewedDaemonEnvelopes))]
    public async Task ReviewedFamilyErrors_PreserveDaemonCodesMessagesAndData(string family, bool batch, string profileJson)
    {
        var profile = JObject.Parse(profileJson);
        var method = profile["method"].Value<string>();
        var expected = profile["reply"]["error"];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var logs = new RpcDiagnosticTests.CapturedLogs();
        await using var server = await RpcDiagnosticTests.Server.Start(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var request = JToken.Parse(await reader.ReadToEndAsync(deadline.Token));
            JObject Reply(JToken item)
            {
                Assert.Equal(method, item["method"].Value<string>());
                var reply = (JObject) profile["reply"].DeepClone();
                reply["id"] = item["id"];
                return reply;
            }
            var body = request is JArray items ? (JToken) new JArray(items.Select(Reply)) : Reply(request);
            await context.Response.WriteAsync(body.ToString(Formatting.None), deadline.Token);
        });
        var client = new RpcClient(server.Endpoint(), new JsonSerializerSettings(), Substitute.For<IMessageBus>(), "test");
        var response = batch
            ? Assert.Single(await client.ExecuteBatchAsync(logs.Logger, deadline.Token, new RpcRequest(method)))
            : await client.ExecuteAsync(logs.Logger, method, deadline.Token);

        Assert.Null(response.Response);
        Assert.NotNull(response.Error);
        Assert.True(response.Error.Code == expected["code"].Value<int>(), family);
        Assert.Equal(expected["message"].Value<string>(), response.Error.Message);
        Assert.Null(response.Error.InnerException); // Never replace the daemon code with synthetic -500.
        var data = expected["data"];
        if(data is JValue scalar)
            Assert.Equal(scalar.Value, response.Error.Data);
        else if(data != null)
            Assert.True(JToken.DeepEquals(data, Assert.IsAssignableFrom<JToken>(response.Error.Data)), family);
        else
            Assert.Null(response.Error.Data);
        logs.AssertSafe();
    }
}
