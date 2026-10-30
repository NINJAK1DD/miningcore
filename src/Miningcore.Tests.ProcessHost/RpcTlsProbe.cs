using System.Reactive.Linq;
using System.Security.Authentication;
using System.Text;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;

// A fresh process gives OpenSSL its own test CA file without changing the parent
// environment, the host trust store or the production transport's validation.
internal static class RpcTlsProbe
{
    internal static async Task<int> RunAsync(string configPath, string transport)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Error, LogLevel.Fatal, new FailureTarget(failure));
        LogManager.Configuration = logging;
        try
        {
            var endpoint = JsonConvert.DeserializeObject<DaemonEndpointConfig>(await File.ReadAllTextAsync(configPath));
            var client = new RpcClient(endpoint, new JsonSerializerSettings(), new MessageBus(), "tls-test");
            var logger = LogManager.GetCurrentClassLogger();
            if(transport == "wss")
            {
                var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = client.WebsocketSubscribe(logger, deadline.Token, endpoint, "get_info")
                    .Subscribe(data => received.TrySetResult(data));
                var completed = await Task.WhenAny(received.Task, failure.Task).WaitAsync(deadline.Token);
                if(completed == failure.Task)
                    return Report(false);
                return Encoding.UTF8.GetString(await received.Task) == "verified" ? Report(true) : 2;
            }

            if(transport == "batch")
            {
                var responses = await client.ExecuteBatchAsync(logger, deadline.Token, new RpcRequest("get_info"));
                if(responses.Length != 1) return 2;
                return Result(responses[0]);
            }
            if(transport is "https" or "http2")
                return Result(await client.ExecuteAsync<JToken>(logger, "get_info", deadline.Token));
            return 64;
        }
        catch(Exception)
        {
            return 2; // Unexpected failures cannot satisfy a TLS rejection assertion.
        }
        finally
        {
            deadline.Cancel();
            LogManager.Shutdown();
        }
    }

    private static int Result(RpcResponse<JToken> response)
    {
        if(response.Error == null)
            return response.Response?.Value<string>() == "verified" ? Report(true) : 2;
        for(var cause = response.Error.InnerException; cause != null; cause = cause.InnerException)
            if(cause is AuthenticationException) return Report(false);
        return 2;
    }

    private static int Report(bool accepted)
    {
        Console.WriteLine(accepted ? "accepted" : "rejected");
        return accepted ? 0 : 20;
    }

    private sealed class FailureTarget(TaskCompletionSource failure) : Target
    {
        protected override void Write(LogEventInfo logEvent) => failure.TrySetResult();
    }
}
