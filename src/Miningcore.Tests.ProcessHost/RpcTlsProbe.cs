using System.Reactive.Linq;
using System.Security.Authentication;
using System.Net.Http;
using System.Text;
using Miningcore.Configuration;
using Miningcore.Blockchain.Ergo;
using Miningcore.Blockchain.Alephium;
using Miningcore.Blockchain.Beam;
using Miningcore.Blockchain.Conceal;
using Miningcore.Blockchain.Warthog;
using Miningcore.Messaging;
using Miningcore.Rpc;
using Miningcore.Rest;
using Microsoft.Extensions.DependencyInjection;
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
        var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpTlsWarning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logging = new LoggingConfiguration();
        logging.AddRule(LogLevel.Warn, LogLevel.Fatal, new FailureTarget(failure, httpTlsWarning));
        LogManager.Configuration = logging;
        try
        {
            var endpoint = JsonConvert.DeserializeObject<DaemonEndpointConfig>(await File.ReadAllTextAsync(configPath));
            if(transport.StartsWith("rest-", StringComparison.Ordinal))
            {
                var services = new ServiceCollection();
                services.AddDaemonRestClient();
                using var provider = services.BuildServiceProvider();
                var restClient = DaemonRestClient.Create(provider.GetRequiredService<IHttpClientFactory>(), endpoint);
                var path = transport switch
                {
                    "rest-beam" => BeamExplorerCommands.GetStatus,
                    "rest-conceal" => ConcealConstants.DaemonRpcGetInfoLocation,
                    "rest-warthog" => WarthogCommands.GetChainInfo,
                    "rest-http2" => BeamExplorerCommands.GetStatus,
                    "rest-http2-http1" => BeamExplorerCommands.GetStatus,
                    "rest-redirect-http" => BeamExplorerCommands.GetStatus,
                    _ => throw new ArgumentOutOfRangeException(nameof(transport)),
                };
                try
                {
                    var response = await restClient.Get<Dictionary<string, string>>(path, deadline.Token,
                        headers: new Dictionary<string, string> { ["X-Synthetic"] = "synthetic-secret" });
                    return response.GetValueOrDefault("value") == "verified" ? Report(true) : 2;
                }
                catch(Exception ex) when(IsTlsHandshake(ex))
                {
                    return Report(false);
                }
                catch(HttpRequestException ex) when(transport == "rest-http2-http1" &&
                    ex.HttpRequestError == HttpRequestError.VersionNegotiationError)
                {
                    return Report(false);
                }
                catch(HttpRequestException ex) when(transport == "rest-redirect-http" &&
                    ex.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect)
                {
                    return Report(false);
                }
            }
            var client = new RpcClient(endpoint, new JsonSerializerSettings(), new MessageBus(), "tls-test");
            var logger = LogManager.GetCurrentClassLogger();
            if(transport == "wss")
            {
                var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var subscription = client.WebsocketSubscribe(logger, deadline.Token, endpoint, "get_info")
                    .Subscribe(data => received.TrySetResult(data));
                var completed = await Task.WhenAny(received.Task, failure.Task).WaitAsync(deadline.Token);
                if(completed == failure.Task)
                    return await failure.Task ? Report(false) : 2;
                return Encoding.UTF8.GetString(await received.Task) == "verified" ? Report(true) : 2;
            }

            if(transport == "batch")
            {
                var responses = await client.ExecuteBatchAsync(logger, deadline.Token, new RpcRequest("get_info"));
                if(responses.Length != 1) return 2;
                return Result(responses[0], httpTlsWarning.Task.IsCompleted);
            }
            if(transport is "https" or "http2")
                return Result(await client.ExecuteAsync<JToken>(logger, "get_info", deadline.Token),
                    httpTlsWarning.Task.IsCompleted);
            if(transport is "ergo" or "alephium")
            {
                var pool = new PoolConfig { Daemons = [endpoint] };
                try
                {
                    if(transport == "ergo")
                        return await ErgoClientFactory.CreateClient(pool, new ClusterConfig(), null)
                            .GetNodeInfoAsync(deadline.Token) != null ? Report(true) : 2;
                    return await AlephiumClientFactory.CreateClient(pool, new ClusterConfig(), null)
                        .GetInfosSelfCliqueAsync(deadline.Token) != null ? Report(true) : 2;
                }
                catch(Exception ex) when(IsTlsHandshake(ex))
                {
                    return Report(false);
                }
            }
            return 64;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().FullName);
            return 2; // Unexpected failures cannot satisfy a TLS rejection assertion.
        }
        finally
        {
            deadline.Cancel();
            LogManager.Shutdown();
        }
    }

    private static int Result(RpcResponse<JToken> response, bool tlsWarningRecorded)
    {
        if(response.Error == null)
            return !tlsWarningRecorded && response.Response?.Value<string>() == "verified" ? Report(true) : 2;
        if(IsTlsHandshake(response.Error.InnerException) && tlsWarningRecorded)
            return Report(false);
        return 2;
    }

    private static int Report(bool accepted)
    {
        Console.WriteLine(accepted ? "accepted" : "rejected");
        return accepted ? 0 : 20;
    }

    private static bool IsTlsHandshake(Exception failure)
    {
        var cause = failure;
        for(var depth = 0; cause != null && depth < 8; cause = cause.InnerException, depth++)
            if(cause is AuthenticationException)
                return true;
        cause = failure;
        for(var depth = 0; cause != null && depth < 8; cause = cause.InnerException, depth++)
            if(cause is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                return true;
        return false;
    }

    private sealed class FailureTarget(TaskCompletionSource<bool> failure, TaskCompletionSource httpTlsWarning) : Target
    {
        protected override void Write(LogEventInfo logEvent)
        {
            const string prefix = "RPC diagnostic ";
            var message = logEvent.FormattedMessage;
            if(!message.StartsWith(prefix, StringComparison.Ordinal))
                return;
            try
            {
                var data = JObject.Parse(message[prefix.Length..]);
                if(data.Value<string>("stage") != "Failure") return;
                if(data.Value<string>("transport") == "WebSocket" && logEvent.Level == LogLevel.Error)
                    failure.TrySetResult(data.Value<string>("failure") == "tls-handshake");
                if(data.Value<string>("transport") == "Http" && logEvent.Level == LogLevel.Warn &&
                   data.Value<string>("failure") == "tls-handshake")
                    httpTlsWarning.TrySetResult();
            }
            catch(JsonException) { failure.TrySetResult(false); }
        }
    }
}
