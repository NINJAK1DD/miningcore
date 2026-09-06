using System.Net.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Rpc;

// This is a projection, not a redactor. Never pass payloads, endpoint strings,
// response IDs, error messages, or exception objects to a logging target.
internal static class RpcDiagnostics
{
    internal enum Transport { Http, WebSocket, Zmq }
    internal enum Stage { Request, Response, Connect, Subscribe, Receive, Failure, StopTimeout }

    internal static string Method(string method) => method switch
    {
        "walletpassphrase" or "walletlock" or "getblocktemplate" or "submitblock" or
        "getblock" or "getblockhash" or "getblockchaininfo" or "getnetworkinfo" or
        "getdeploymentinfo" or "getbalance" or "gettransaction" or "sendmany" or
        "sendtoaddress" or "validateaddress" or "getinfo" or "getmininginfo" or
        "eth_subscribe" or "eth_getWork" or "eth_submitWork" => method,
        _ => "other",
    };

    internal static void Write(ILogger logger, LogLevel level, Transport transport, Stage stage,
        string method = null, int? batchCount = null, int? status = null,
        long? bytes = null, long? elapsedMs = null, Exception failure = null)
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
            ["httpStatus"] = status,
            ["bytes"] = bytes,
            ["elapsedMs"] = elapsedMs,
            ["failure"] = failure == null ? null : failure switch
            {
                OperationCanceledException => "cancelled",
                JsonException => "json",
                HttpRequestException => "http",
                WebSocketException => "websocket",
                InvalidDataException => "invalid-data",
                _ => "other",
            },
        };
        logger.Log(level, "RPC diagnostic " + data.ToString(Formatting.None));
    }
}
