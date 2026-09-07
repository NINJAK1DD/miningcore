using System.Net.Sockets;
using System.Net.WebSockets;
using Miningcore.Stratum;
using Newtonsoft.Json;
using ZeroMQ;

namespace Miningcore.Diagnostics;

// Fixed vocabulary only: neither exception messages, stack traces nor arbitrary
// runtime type names are a safe diagnostic contract.
internal static class DiagnosticFailure
{
    internal static string Category(Exception error) => error switch
    {
        null => "unknown",
        TimeoutException => "timeout",
        OperationCanceledException { InnerException: TimeoutException } => "timeout",
        OperationCanceledException => "cancelled",
        JsonException => "json",
        HttpRequestException => "http",
        WebSocketException => "websocket",
        InvalidDataException => "invalid-data",
        ZException => "zmq",
        SocketException => "socket",
        Grpc.Core.RpcException => "grpc",
        ObjectDisposedException => "disposed",
        ArgumentException => "argument",
        IOException => "io",
        NullReferenceException => "null-reference",
        InvalidOperationException => "invalid-operation",
        StratumException stratum => stratum.Code switch
        {
            StratumError.JobNotFound => "job-not-found",
            StratumError.DuplicateShare => "duplicate-share",
            StratumError.LowDifficultyShare => "low-difficulty-share",
            StratumError.UnauthorizedWorker => "unauthorized-worker",
            StratumError.NotSubscribed => "not-subscribed",
            _ => "share-rejected",
        },
        _ => "other",
    };

    internal static int? Code(Exception error) => error switch
    {
        WebSocketException ws => (int) ws.WebSocketErrorCode,
        HttpRequestException http => (int) http.HttpRequestError,
        ZException zmq => zmq.Error?.Number,
        SocketException socket => socket.NativeErrorCode,
        Grpc.Core.RpcException grpc => (int) grpc.StatusCode,
        StratumException stratum => (int) stratum.Code,
        _ => null,
    };
}
