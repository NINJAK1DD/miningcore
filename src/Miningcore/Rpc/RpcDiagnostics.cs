using System.Net.WebSockets;
using Miningcore.Configuration;
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
    internal enum Stage { Request, Response, Connect, Subscribe, Receive, Failure, StopTimeout, CancellationCallbackFailure }

    internal static string Method(string method) => RpcMethodCatalog.Label(method);

    // Diagnostic correlation must never make an otherwise usable endpoint fatal.
    // Unknown/cloned endpoints are not falsely attributed to the first daemon.
    internal static int? EndpointIndex(DaemonEndpointConfig[] configured, DaemonEndpointConfig endpoint)
    {
        if(configured == null || endpoint == null)
            return null;

        var index = Array.FindIndex(configured, item => ReferenceEquals(item, endpoint));
        return index >= 0 ? index + 1 : null;
    }

    /// <remarks>
    /// httpResponseChars counts decoded UTF-16 code units (String.Length), not wire
    /// bytes or Unicode code points. A null handshake status means no HTTP status
    /// was captured; it does not establish whether a connection or TLS succeeded.
    /// </remarks>
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
            ["failure"] = failure == null ? null : Diagnostics.DiagnosticFailure.Category(failure),
            ["failureCode"] = Diagnostics.DiagnosticFailure.Code(failure),
        };
        logger.Log(level, "RPC diagnostic " + data.ToString(Formatting.None));
    }
}
