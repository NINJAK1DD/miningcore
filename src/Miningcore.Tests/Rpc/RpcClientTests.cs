using System.IO;
using Miningcore.JsonRpc;
using Miningcore.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class RpcClientTests
{
    [Fact]
    public void OrderBatchResponses_ReordersResponsesById()
    {
        var requests = new[]
        {
            new JsonRpcRequest<object>("submitblock", null, "batch-0"),
            new JsonRpcRequest<object>("getblock", null, "batch-1"),
        };
        var responses = new[]
        {
            Response("block", "batch-1"),
            Response("accepted", "batch-0"),
        };

        var ordered = RpcClient.OrderBatchResponses(requests, responses);

        Assert.Equal("accepted", ordered[0].Result.ToString());
        Assert.Equal("block", ordered[1].Result.ToString());
    }

    [Fact]
    public void OrderBatchResponses_WithMissingResponse_FailsClosed()
    {
        var requests = new[]
        {
            new JsonRpcRequest<object>("submitblock", null, "batch-0"),
            new JsonRpcRequest<object>("getblock", null, "batch-1"),
        };
        var responses = new[] { Response("accepted", "batch-0") };

        Assert.Throws<InvalidDataException>(() =>
            RpcClient.OrderBatchResponses(requests, responses));
    }

    [Fact]
    public void OrderBatchResponses_WithDuplicateId_FailsClosed()
    {
        var requests = new[]
        {
            new JsonRpcRequest<object>("submitblock", null, "batch-0"),
            new JsonRpcRequest<object>("getblock", null, "batch-1"),
        };
        var responses = new[]
        {
            Response("accepted", "batch-0"),
            Response("block", "batch-0"),
        };

        Assert.Throws<InvalidDataException>(() =>
            RpcClient.OrderBatchResponses(requests, responses));
    }

    [Fact]
    public void OrderBatchResponses_WithUnknownId_FailsClosed()
    {
        var requests = new[]
        {
            new JsonRpcRequest<object>("submitblock", null, "batch-0"),
            new JsonRpcRequest<object>("getblock", null, "batch-1"),
        };
        var responses = new[]
        {
            Response("accepted", "batch-0"),
            Response("block", "unexpected"),
        };

        Assert.Throws<InvalidDataException>(() =>
            RpcClient.OrderBatchResponses(requests, responses));
    }

    [Fact]
    public void OrderBatchResponses_WithNullId_FailsClosed()
    {
        var requests = new[]
        {
            new JsonRpcRequest<object>("submitblock", null, "batch-0"),
            new JsonRpcRequest<object>("getblock", null, "batch-1"),
        };
        var responses = new[]
        {
            Response("accepted", null),
            Response("block", "batch-1"),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            RpcClient.OrderBatchResponses(requests, responses));
        Assert.Contains("omitted an id", error.Message);
    }

    private static JsonRpcResponse<JToken> Response(string result, object id)
    {
        return new JsonRpcResponse<JToken>
        {
            Result = JToken.FromObject(result),
            Id = id,
        };
    }
}
