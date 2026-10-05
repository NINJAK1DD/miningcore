using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using ZeroMQ;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Rpc;

/// <summary>
/// JsonRpc interface to blockchain node
/// </summary>
public class RpcClient
{
    public RpcClient(DaemonEndpointConfig endPoint, JsonSerializerSettings serializerSettings, IMessageBus messageBus, string poolId)
    {
        Contract.RequiresNonNull(serializerSettings);
        Contract.RequiresNonNull(messageBus);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(poolId));

        config = endPoint;
        this.serializerSettings = serializerSettings;
        this.messageBus = messageBus;
        this.poolId = poolId;

        serializer = new JsonSerializer
        {
            ContractResolver = serializerSettings.ContractResolver!
        };
    }

    private readonly JsonSerializerSettings serializerSettings;
    protected readonly DaemonEndpointConfig config;
    private readonly JsonSerializer serializer;
    private readonly IMessageBus messageBus;
    private readonly string poolId;

    private static readonly HttpClient httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,

        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true,
    });

    #region API-Surface

    public async Task<RpcResponse<TResponse>> ExecuteAsync<TResponse>(ILogger logger, string method, CancellationToken ct,
        object payload = null, bool throwOnError = false)
        where TResponse : class
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method));

        try
        {
            var response = await RequestAsync(logger, ct, config, method, payload);

            try
            {
                // Preserve boxed scalar contracts used by shared Ethereum/Xelis
                // callers (notably bool/object), and reject numeric-to-string
                // coercion. Explicit token callers still receive the parsed token.
                if(response.Result is JValue value && !typeof(JToken).IsAssignableFrom(typeof(TResponse)))
                    return new RpcResponse<TResponse>((TResponse) value.Value, response.Error);

                if(response.Result is JToken token)
                    return new RpcResponse<TResponse>(token is TResponse typed ? typed : token.ToObject<TResponse>(serializer), response.Error);

                return new RpcResponse<TResponse>((TResponse) response.Result, response.Error);
            }
            catch(Exception ex) when(ex is JsonException or InvalidCastException or ArgumentException)
            {
                // Result conversion follows successful framing/envelope decode.
                // Scalars can fail by CLR cast rather than Json.NET conversion.
                throw new JsonSerializationException("JSON-RPC result contract is incompatible", ex);
            }
        }

        catch(TaskCanceledException ex)
        {
            RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Failure, method, failure: ex);
            // Preserve the structural cause. Callers that need to distinguish a
            // client-side timeout/cancellation from a daemon error must not have to
            // pattern-match this synthetic message.
            return new RpcResponse<TResponse>(null,
                new JsonRpcError(-500, "Cancelled", null, ex));
        }

        catch(Exception ex)
        {
            RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Failure, method, failure: ex);
            if(throwOnError)
                throw;

            // Caller-observable data is preserved; unsafe consumer logging is tracked in #154.
            return new RpcResponse<TResponse>(null, new JsonRpcError(-500, ex.Message, null, ex));
        }
    }

    public Task<RpcResponse<JToken>> ExecuteAsync(ILogger logger, string method, CancellationToken ct, bool throwOnError = false)
    {
        return ExecuteAsync<JToken>(logger, method, ct, null, throwOnError);
    }

    public async Task<RpcResponse<JToken>[]> ExecuteBatchAsync(ILogger logger, CancellationToken ct, params RpcRequest[] batch)
    {
        Contract.RequiresNonNull(batch);

        try
        {
            var response = await BatchRequestAsync(logger, ct, config, batch);

            return response
                .Select(x => new RpcResponse<JToken>((JToken) x.Result, x.Error))
                .ToArray();
        }

        catch(Exception ex)
        {
            RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Failure, batchCount: batch.Length, failure: ex);
            // Caller-observable data is preserved; unsafe consumer logging is tracked in #154.
            return Enumerable.Repeat(new RpcResponse<JToken>(null, new JsonRpcError(-500, ex.Message, null, ex)), batch.Length).ToArray();
        }
    }

    public IObservable<byte[]> WebsocketSubscribe(ILogger logger, CancellationToken ct, DaemonEndpointConfig endPoint,
        string method, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null, int? endpointIndex = null)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(method));

        endpointIndex = endpointIndex > 0 ? endpointIndex : null;

        return WebsocketSubscribeEndpoint(logger, ct, endPoint, method, payload, payloadJsonSerializerSettings, endpointIndex)
            .Publish()
            .RefCount();
    }

    public IObservable<ZMessage> ZmqSubscribe(ILogger logger, CancellationToken ct,
        Dictionary<DaemonEndpointConfig, (string Socket, string Topic)> portMap,
        DaemonEndpointConfig[] configuredEndpoints)
    {
        // Callers must supply mapping context, or explicitly choose null for unknown.
        // Resolve against the full daemon array, not the filtered map or dictionary order.
        // Snapshot before Defer/RefCount so resubscription retains the same attribution.
        var endpoints = portMap.Select(entry => (entry.Value.Socket, entry.Value.Topic,
            Index: RpcDiagnostics.EndpointIndex(configuredEndpoints, entry.Key))).ToArray();
        return endpoints
            .Select(endpoint => ZmqSubscribeEndpoint(logger, ct, endpoint.Socket, endpoint.Topic, endpoint.Index))
            .Merge()
            .Publish()
            .RefCount();
    }

    #endregion // API-Surface

    private async Task<JsonRpcResponse<JToken>> RequestAsync(ILogger logger, CancellationToken ct, DaemonEndpointConfig endPoint, string method, object payload)
    {
        var sw = Stopwatch.StartNew();

        // build rpc request
        var rpcRequest = new JsonRpcRequest<object>(method, payload, GetRequestId());

        // build url
        var protocol = endPoint.Ssl || endPoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var requestUrl = $"{protocol}://{endPoint.Host}:{endPoint.Port}";

        if(!string.IsNullOrEmpty(endPoint.HttpPath))
            requestUrl += $"{(endPoint.HttpPath.StartsWith("/") ? string.Empty : "/")}{endPoint.HttpPath}";

        using(var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
        {
            if(endPoint.Http2)
                request.Version = new Version(2, 0);
            else
                request.Headers.ConnectionClose = false;    // enable keep-alive

            // content
            var json = JsonConvert.SerializeObject(rpcRequest, serializerSettings);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // auth header
            if(!string.IsNullOrEmpty(endPoint.User))
            {
                var auth = $"{endPoint.User}:{endPoint.Password}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth.ToByteArrayBase64());
            }

            RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Request, method);

            // send request
            using(var response = await httpClient.SendAsync(request, ct))
            {
                // read response
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                    RpcDiagnostics.Stage.Response, method, status: (int) response.StatusCode,
                    httpResponseChars: responseContent.Length,
                    elapsedMs: sw.ElapsedMilliseconds);

                var decoded = ReadResponse(responseContent, response.StatusCode);
                var result = DecodeEnvelope(decoded, serializer);
                messageBus.SendTelemetry(poolId, TelemetryCategory.RpcRequest, RpcDiagnostics.Method(method), sw.Elapsed, response.IsSuccessStatusCode);

                return result;
            }
        }
    }

    private async Task<JsonRpcResponse<JToken>[]> BatchRequestAsync(ILogger logger, CancellationToken ct, DaemonEndpointConfig endPoint, RpcRequest[] batch)
    {
        var sw = Stopwatch.StartNew();

        var batchRequestId = GetRequestId();
        var rpcRequests = batch
            .Select((x, index) => new JsonRpcRequest<object>(x.Method, x.Payload,
                $"{batchRequestId}-{index}"))
            .ToArray();

        // url
        var protocol = (endPoint.Ssl || endPoint.Http2) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var requestUrl = $"{protocol}://{endPoint.Host}:{endPoint.Port}";

        if(!string.IsNullOrEmpty(endPoint.HttpPath))
            requestUrl += $"{(endPoint.HttpPath.StartsWith("/") ? string.Empty : "/")}{endPoint.HttpPath}";

        using(var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
        {
            if(endPoint.Http2)
                request.Version = new Version(2, 0);
            else
                request.Headers.ConnectionClose = false;    // enable keep-alive

            // content
            var json = JsonConvert.SerializeObject(rpcRequests, serializerSettings);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            // auth header
            if(!string.IsNullOrEmpty(endPoint.User))
            {
                var auth = $"{endPoint.User}:{endPoint.Password}";
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth.ToByteArrayBase64());
            }

            RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                RpcDiagnostics.Stage.Request, batchCount: batch.Length);

            // send request
            using(var response = await httpClient.SendAsync(request, ct))
            {
                // deserialize response
                var responseContent = await response.Content.ReadAsStringAsync(ct);

                RpcDiagnostics.Write(logger, LogLevel.Trace, RpcDiagnostics.Transport.Http,
                    RpcDiagnostics.Stage.Response, batchCount: batch.Length, status: (int) response.StatusCode,
                    httpResponseChars: responseContent.Length,
                    elapsedMs: sw.ElapsedMilliseconds);

                var decoded = ReadResponse(responseContent, response.StatusCode);
                if(decoded is not JArray items)
                    throw new JsonSerializationException("JSON-RPC batch response contract is incompatible");
                var result = items.Select(item => DecodeEnvelope(item, serializer)).ToArray();

                messageBus.SendTelemetry(poolId, TelemetryCategory.RpcRequest, "batch",
                    sw.Elapsed, response.IsSuccessStatusCode);

                return OrderBatchResponses(rpcRequests, result);
            }
        }
    }

    private static JToken ReadResponse(string content, HttpStatusCode status)
    {
        // Bitcoind authentication failures may have no RPC body. Keep these as
        // transport failures for shared callers, without forwarding HTTP text.
        if(string.IsNullOrWhiteSpace(content))
            throw new HttpRequestException("JSON-RPC HTTP response was empty", null, status);

        try
        {
            using var reader = new JsonTextReader(new StringReader(content));
            var result = JToken.ReadFrom(reader);
            if(reader.Read())
                throw new JsonReaderException("JSON-RPC response contains trailing content");
            return result;
        }
        catch(JsonReaderException ex) when((int) status is < 200 or >= 300)
        {
            throw new HttpRequestException("JSON-RPC HTTP response could not be decoded", ex, status);
        }
    }

    internal static JsonRpcResponse<JToken> DecodeEnvelope(JToken decoded, JsonSerializer serializer)
    {
        try
        {
            if(decoded is not JObject envelope)
                throw new JsonSerializationException("JSON-RPC response envelope is missing");

            // Borrow the parsed result instead of serializing a potentially large
            // getblocktemplate tree a second time. Only typed DTO callers convert it.
            var result = new JsonRpcResponse<JToken>
            {
                Result = NullToClr(envelope["result"]),
                Id = ScalarOrToken(envelope["id"]),
            };
            var error = NullToClr(envelope["error"]);
            if(error != null)
            {
                if(error is not JObject fields)
                    throw new JsonSerializationException("JSON-RPC error envelope is incompatible");
                // Convert only the small scalar fields; retain arbitrary daemon
                // data without another tree copy or diagnostic disclosure.
                result.Error = new JsonRpcError(fields["code"]?.ToObject<int>(serializer) ?? 0,
                    fields["message"]?.ToObject<string>(serializer), ScalarOrToken(fields["data"]));
            }
            foreach(var property in envelope.Properties())
            {
                if(property.Name is "result" or "id" or "error") continue;
                result.Extra ??= new Dictionary<string, object>(StringComparer.Ordinal);
                result.Extra[property.Name] = NullToClr(property.Value);
            }
            return result;
        }
        catch(JsonException ex)
        {
            // Conversion can raise JsonReaderException too. Framing already
            // succeeded, so both single and batch expose a contract failure.
            throw new JsonSerializationException("JSON-RPC response contract is incompatible", ex);
        }
    }

    private static JToken NullToClr(JToken token) => token?.Type == JTokenType.Null ? null : token;

    private static object ScalarOrToken(JToken token) => token is JValue scalar ? scalar.Value : token;

    internal static JsonRpcResponse<JToken>[] OrderBatchResponses(
        IReadOnlyCollection<JsonRpcRequest<object>> requests,
        IReadOnlyCollection<JsonRpcResponse<JToken>> responses)
    {
        if(responses == null)
            throw new InvalidDataException("JSON-RPC batch response was empty");

        if(responses.Count != requests.Count)
            throw new InvalidDataException(
                $"JSON-RPC batch response count mismatch: expected {requests.Count}, received {responses.Count}");

        var responsesById = new Dictionary<string, JsonRpcResponse<JToken>>(StringComparer.Ordinal);

        foreach(var response in responses)
        {
            var id = NormalizeJsonRpcId(response?.Id);

            if(!responsesById.TryAdd(id, response))
                throw new InvalidDataException($"JSON-RPC batch response contained duplicate id {id}");
        }

        var result = new JsonRpcResponse<JToken>[requests.Count];
        var index = 0;

        foreach(var request in requests)
        {
            var id = NormalizeJsonRpcId(request.Id);

            if(!responsesById.Remove(id, out var response))
                throw new InvalidDataException($"JSON-RPC batch response omitted request id {id}");

            result[index++] = response;
        }

        if(responsesById.Count > 0)
            throw new InvalidDataException("JSON-RPC batch response contained an unknown request id");

        return result;
    }

    private static string NormalizeJsonRpcId(object id)
    {
        if(id == null)
            throw new InvalidDataException("JSON-RPC batch response omitted an id");

        return id is JToken token
            ? token.ToString(Formatting.None)
            : JToken.FromObject(id).ToString(Formatting.None);
    }

    protected string GetRequestId()
    {
        var rpcRequestId = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + StaticRandom.Next(10)).ToString();
        return rpcRequestId;
    }

    private IObservable<byte[]> WebsocketSubscribeEndpoint(ILogger logger, CancellationToken ct,
        DaemonEndpointConfig endPoint, string method, object payload,
        JsonSerializerSettings payloadJsonSerializerSettings, int? endpointIndex)
    {
        return Observable.Defer(() => Observable.Create<byte[]>(obs =>
        {
            var lifetime = new RpcSubscriptionLifetime(ct, () => RpcDiagnostics.Write(logger, LogLevel.Error,
                RpcDiagnostics.Transport.WebSocket, RpcDiagnostics.Stage.CancellationCallbackFailure,
                method, endpointIndex: endpointIndex));
            var token = lifetime.Token;

            var worker = lifetime.Run(async () =>
            {
                var buf = new byte[0x10000];

                while(!token.IsCancellationRequested)
                {
                    int? handshakeStatus = null;
                    try
                    {
                        using(var client = new ClientWebSocket())
                        {
                            // connect
                            var protocol = endPoint.Ssl ? "wss" : "ws";
                            var uri = new Uri($"{protocol}://{endPoint.Host}:{endPoint.Port}{endPoint.HttpPath}");
                            client.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                            RpcDiagnostics.Write(logger, LogLevel.Debug, RpcDiagnostics.Transport.WebSocket,
                                RpcDiagnostics.Stage.Connect, method, endpointIndex: endpointIndex);
                            client.Options.CollectHttpResponseDetails = true;
                            try { await client.ConnectAsync(uri, token); }
                            finally
                            {
                                if(client.HttpStatusCode != 0)
                                    handshakeStatus = (int) client.HttpStatusCode;
                            }

                            // subscribe
                            var request = new JsonRpcRequest(method, payload, GetRequestId());
                            var json = JsonConvert.SerializeObject(request, payloadJsonSerializerSettings);
                            var requestData = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));

                            RpcDiagnostics.Write(logger, LogLevel.Debug, RpcDiagnostics.Transport.WebSocket,
                                RpcDiagnostics.Stage.Subscribe, method, endpointIndex: endpointIndex);
                            await client.SendAsync(requestData, WebSocketMessageType.Text, true, token);

                            // stream response
                            while(!token.IsCancellationRequested && client.State == WebSocketState.Open)
                            {
                                await using var stream = new MemoryStream();

                                do
                                {
                                    var response = await client.ReceiveAsync(buf, token);

                                    if(response.MessageType == WebSocketMessageType.Binary)
                                        throw new InvalidDataException("expected text, received binary data");

                                    await stream.WriteAsync(buf, 0, response.Count, token);

                                    if(response.EndOfMessage)
                                        break;
                                } while(!token.IsCancellationRequested && client.State == WebSocketState.Open);

                                RpcDiagnostics.Write(logger, LogLevel.Debug, RpcDiagnostics.Transport.WebSocket,
                                    RpcDiagnostics.Stage.Receive, method, bytes: stream.Length, endpointIndex: endpointIndex);

                                // publish
                                obs.OnNext(stream.ToArray());
                            }
                        }
                    }

                    catch (TaskCanceledException) when(token.IsCancellationRequested)
                    {
                        break;
                    }

                    catch (ObjectDisposedException) when(token.IsCancellationRequested)
                    {
                        break;
                    }

                    catch(Exception ex)
                    {
                        RpcDiagnostics.Write(logger, LogLevel.Error, RpcDiagnostics.Transport.WebSocket,
                            RpcDiagnostics.Stage.Failure, method, status: handshakeStatus, failure: ex, endpointIndex: endpointIndex);
                    }

                    if(!token.IsCancellationRequested)
                    {
                        // Task.Delay can only cancel through this token. Retain the
                        // filter to make the shutdown-only boundary explicit.
                        try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                        catch(OperationCanceledException) when(token.IsCancellationRequested) { break; }
                    }
                }
            });

            _ = ObserveSubscriptionWorkerAsync(worker, logger, method, endpointIndex);
            return Disposable.Create(lifetime.Cancel);
        }));
    }

    internal static async Task ObserveSubscriptionWorkerAsync(Task worker, ILogger logger,
        string method, int? endpointIndex)
    {
        try { await worker; }
        catch(Exception ex)
        {
            RpcDiagnostics.Write(logger, LogLevel.Error, RpcDiagnostics.Transport.WebSocket,
                RpcDiagnostics.Stage.Failure, method, failure: ex, endpointIndex: endpointIndex);
            // Do not signal OnError: job managers merge push updates with polling.
            // A terminal push-worker fault must not terminate that polling fallback.
            // The Error-level diagnostic exposes the degraded push path without
            // forwarding a potentially secret-bearing exception to subscribers.
        }
    }

    private static IObservable<ZMessage> ZmqSubscribeEndpoint(ILogger logger, CancellationToken ct, string url, string topic, int? endpointIndex)
    {
        return Observable.Defer(() => Observable.Create<ZMessage>(obs =>
        {
            var lifetime = new RpcSubscriptionLifetime(ct, () => RpcDiagnostics.Write(logger, LogLevel.Error,
                RpcDiagnostics.Transport.Zmq, RpcDiagnostics.Stage.CancellationCallbackFailure,
                endpointIndex: endpointIndex));
            var token = lifetime.Token;

            var thread = new Thread(() =>
            {
                try
                {
                    while(!token.IsCancellationRequested)
                    {
                        try
                        {
                            using(var subSocket = new ZSocket(ZSocketType.SUB))
                            {
                                //subSocket.Options.ReceiveHighWatermark = 1000;
                                subSocket.ReceiveTimeout = TimeSpan.FromSeconds(1);
                                subSocket.Connect(url);
                                subSocket.Subscribe(topic);

                                RpcDiagnostics.Write(logger, LogLevel.Debug, RpcDiagnostics.Transport.Zmq,
                                    RpcDiagnostics.Stage.Subscribe, endpointIndex: endpointIndex);

                                while(!token.IsCancellationRequested)
                                {
                                    var msg = subSocket.ReceiveMessage(out var error);

                                    if(msg != null)
                                        obs.OnNext(msg);
                                    else if(error != ZError.EAGAIN && error != ZError.ETIMEDOUT)
                                        throw new ZException(error);
                                }
                            }
                        }

                        catch(Exception ex)
                        {
                            if(token.IsCancellationRequested)
                                break;

                            RpcDiagnostics.Write(logger, LogLevel.Error, RpcDiagnostics.Transport.Zmq,
                                RpcDiagnostics.Stage.Failure, failure: ex, endpointIndex: endpointIndex);

                            // do not run wild in case of a persistent error condition
                            Thread.Sleep(1000);
                        }
                    }
                }
                finally { lifetime.Complete(); }
            })
            {
                IsBackground = true,
                Name = $"ZMQ subscriber {endpointIndex?.ToString() ?? "unknown"}",
            };

            thread.Start();

            return Disposable.Create(() =>
            {
                lifetime.Cancel();

                if(!thread.Join(TimeSpan.FromSeconds(5)))
                    RpcDiagnostics.Write(logger, LogLevel.Warn, RpcDiagnostics.Transport.Zmq,
                        RpcDiagnostics.Stage.StopTimeout, endpointIndex: endpointIndex);

                // A timed-out worker retains ownership until it actually exits.
            });
        }));
    }
}
