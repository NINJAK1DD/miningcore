using System.Net.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using ZeroMQ;

namespace Miningcore.Rpc;

// This is a projection, not a redactor. Never pass payloads, endpoint strings,
// response IDs, error messages, or exception objects to a logging target.
internal static class RpcDiagnostics
{
    internal enum Transport { Http, WebSocket, Zmq }
    internal enum Stage { Request, Response, Connect, Subscribe, Receive, Failure, StopTimeout }

    internal static string Method(string method) => RpcMethodCatalog.Label(method);

    internal static void Write(ILogger logger, LogLevel level, Transport transport, Stage stage,
        string method = null, int? batchCount = null, int? status = null,
        long? bytes = null, long? elapsedMs = null, Exception failure = null,
        long? endpointIndex = null, int? httpResponseChars = null)
    {
        if(!logger.IsEnabled(level))
            return;

        // Explicit JValues avoid both application-wide JsonConvert.DefaultSettings
        // and RPC serializer settings/converters. Every string has a finite vocabulary.
        var data = new JObject
        {
            ["transport"] = transport.ToString(),
            ["stage"] = stage.ToString(),
            ["method"] = Method(method),
            ["batchCount"] = batchCount,
            ["httpStatus"] = status ?? (failure is HttpRequestException http ? (int?) http.StatusCode : null),
            ["bytes"] = bytes,
            ["httpResponseChars"] = httpResponseChars,
            ["elapsedMs"] = elapsedMs,
            ["endpointIndex"] = endpointIndex,
            ["failure"] = failure == null ? null : failure switch
            {
                TimeoutException => "timeout",
                OperationCanceledException { InnerException: TimeoutException } => "timeout",
                OperationCanceledException => "cancelled",
                JsonException => "json",
                HttpRequestException => "http",
                WebSocketException => "websocket",
                InvalidDataException => "invalid-data",
                ZException => "zmq",
                System.Net.Sockets.SocketException => "socket",
                ObjectDisposedException => "disposed",
                ArgumentException => "argument",
                IOException => "io",
                // Do not use arbitrary exception type names: custom/dynamic types
                // need not have safe names. Numeric platform codes supply detail.
                _ => "other",
            },
            ["failureCode"] = failure switch
            {
                WebSocketException ws => (int?) ws.WebSocketErrorCode,
                HttpRequestException request => (int?) request.HttpRequestError,
                ZException zmq => zmq.Error?.Number,
                System.Net.Sockets.SocketException socket => socket.NativeErrorCode,
                _ => null,
            },
        };
        logger.Log(level, "RPC diagnostic " + data.ToString(Formatting.None));
    }
}
