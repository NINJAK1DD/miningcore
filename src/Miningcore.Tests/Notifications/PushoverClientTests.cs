using System;
using System.Collections.Concurrent;
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
    public async Task SharedGateReservesCapacityForCriticalAlerts()
    {
        var handler = new BlockingHandler();
        using var http = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(http);
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
        var firstClient = new PushoverClient(config, factory);
        var secondClient = new PushoverClient(config, factory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task<PushoverReponse> first = null;
        Task<PushoverReponse> second = null;
        Task<PushoverReponse> critical = null;
        try
        {
            first = firstClient.PushMessage("ordinary-1", "message",
                PushoverMessagePriority.None, deadline.Token);
            await handler.WaitForStartAsync(deadline.Token);
            second = secondClient.PushMessage("ordinary-2", "message",
                PushoverMessagePriority.None, deadline.Token);
            critical = secondClient.PushMessage("critical", "message",
                PushoverMessagePriority.None, true, deadline.Token);
            await handler.WaitForStartAsync(deadline.Token);

            Assert.Equal(2, handler.Active);
            Assert.Contains("critical", handler.StartedTitles);
            Assert.DoesNotContain("ordinary-2", handler.StartedTitles);
            handler.Release("ordinary-1");
            await handler.WaitForStartAsync(deadline.Token);
            Assert.Equal(2, handler.Active);
            Assert.True(handler.Peak <= 2);
            handler.Release("critical");
            handler.Release("ordinary-2");
            await Task.WhenAll(first, second, critical).WaitAsync(deadline.Token);
            Assert.Equal(0, handler.Active);
        }
        finally
        {
            // A failed assertion must not strand the process-wide gate for
            // every other Pushover test in this process.
            deadline.Cancel();
            handler.ReleaseAll();
            await Task.WhenAll(new[] { first, second, critical }
                .Where(task => task != null)).ContinueWith(_ => { });
        }
    }

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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        private readonly ConcurrentQueue<string> titles = new();
        private readonly SemaphoreSlim started = new(0);
        private int active;
        private int peak;

        public int Active => Volatile.Read(ref active);
        public int Peak => Volatile.Read(ref peak);
        public string[] StartedTitles => titles.ToArray();

        public Task WaitForStartAsync(CancellationToken ct) => started.WaitAsync(ct);

        public void Release(string title) => releases[title].SetResult();

        public void ReleaseAll()
        {
            foreach(var release in releases.Values)
                release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(
                await request.Content.ReadAsStringAsync(cancellationToken));
            var title = document.RootElement.GetProperty("title").GetString();
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releases[title] = release;
            titles.Enqueue(title);
            var current = Interlocked.Increment(ref active);
            int previous;
            do
            {
                previous = Volatile.Read(ref peak);
                if(previous >= current)
                    break;
            } while(Interlocked.CompareExchange(ref peak, current, previous) != previous);
            started.Release();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":1}"),
                };
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }
}
