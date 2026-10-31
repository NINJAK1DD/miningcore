using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Rest;
using Xunit;

namespace Miningcore.Tests.Rpc;

public class SimpleRestClientResponseTests
{
    [Theory]
    [InlineData("send")]
    [InlineData("post")]
    [InlineData("delete")]
    public async Task ReturnedResponseBelongsToCaller(string operation)
    {
        using var content = new TrackingContent("{\"value\":\"ok\"}");
        using var http = new HttpClient(new ReplyHandler(HttpStatusCode.OK, content));
        var rest = new SimpleRestClient(http, "http://localhost");

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/status");
        var result = operation switch
        {
            "send" => await rest.SendWithResponse<Payload>(request, CancellationToken.None),
            "post" => await rest.PostWithResponse<Payload>("status", new { value = 1 }, CancellationToken.None),
            _ => await rest.DeleteWithResponse<Payload>("status", CancellationToken.None),
        };
        Assert.Equal("ok", result.Content.Value);
        Assert.False(content.IsDisposed);
        result.Dispose();
        Assert.True(content.IsDisposed);
    }

    [Theory]
    [InlineData("send")]
    [InlineData("post")]
    [InlineData("delete")]
    public async Task DeserializationFailureDisposesResponse(string operation)
    {
        using var content = new TrackingContent("invalid-json");
        using var http = new HttpClient(new ReplyHandler(HttpStatusCode.OK, content));
        var rest = new SimpleRestClient(http, "http://localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/status");

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            using var _ = operation switch
            {
                "send" => await rest.SendWithResponse<Payload>(request, CancellationToken.None),
                "post" => await rest.PostWithResponse<Payload>("status", new { value = 1 }, CancellationToken.None),
                _ => await rest.DeleteWithResponse<Payload>("status", CancellationToken.None),
            };
        });
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task RejectedSendDisposesResponse()
    {
        using var content = new TrackingContent("rejected");
        using var http = new HttpClient(new ReplyHandler(HttpStatusCode.BadRequest, content));
        var rest = new SimpleRestClient(http, "http://localhost");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/status");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            rest.SendWithResponse<Payload>(request, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.True(content.IsDisposed);
    }

    private sealed class Payload
    {
        public string Value { get; set; }
    }

    private sealed class ReplyHandler(HttpStatusCode status, HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct) => Task.FromResult(new HttpResponseMessage(status) { Content = content });
    }

    private sealed class TrackingContent(string value) : StringContent(value)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
