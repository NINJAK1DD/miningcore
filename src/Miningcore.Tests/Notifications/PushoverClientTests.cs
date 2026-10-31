using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Pushover;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Notifications;

public class PushoverClientTests
{
    [Fact]
    public async Task OversizedTitleAndMessageKeepIncidentSummaryAndInstruction()
    {
        const string instruction = "Acknowledge fatal state before restarting.";
        var title = "Fatal persistence failure " + new string('T', 300);
        var message = "Fatal state: /protected/recovery.fatal. " +
            string.Concat(Enumerable.Repeat("😀", 1100)) + instruction;
        var handler = new ReplyHandler("{\"status\":1}");
        var client = CreateClient(handler);

        await client.PushMessage(title, message, PushoverMessagePriority.None,
            CancellationToken.None);

        using var document = JsonDocument.Parse(handler.SentBody);
        var sentTitle = document.RootElement.GetProperty("title").GetString();
        var sentMessage = document.RootElement.GetProperty("message").GetString();
        Assert.True(sentTitle.EnumerateRunes().Count() <=
            PushoverMessagePolicy.MaxTitleCharacters);
        Assert.True(sentMessage.EnumerateRunes().Count() <=
            PushoverMessagePolicy.MaxMessageCharacters);
        Assert.Contains("Fatal state: /protected/recovery.fatal", sentMessage);
        Assert.Contains(instruction, sentMessage);
        Assert.Contains("details omitted", sentMessage);
    }

    [Theory]
    [InlineData("{\"status\":0,\"errors\":[\"private token\"]}")]
    [InlineData("null")]
    public async Task HttpSuccessWithoutApplicationAcknowledgementFailsSafely(
        string response)
    {
        var client = CreateClient(new ReplyHandler(response));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            client.PushMessage("incident", "failure",
                PushoverMessagePriority.None, CancellationToken.None));

        Assert.Contains("did not confirm", error.Message);
        Assert.DoesNotContain("private token", error.Message);
    }

    private static PushoverClient CreateClient(ReplyHandler handler)
    {
        var config = new ClusterConfig
        {
            Notifications = new NotificationsConfig
            {
                Pushover = new PushoverConfig
                {
                    Enabled = true, User = "user", Token = "token",
                },
            },
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(new HttpClient(handler));
        return new PushoverClient(config, factory);
    }

    private sealed class ReplyHandler(string response) : HttpMessageHandler
    {
        public string SentBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }
}
