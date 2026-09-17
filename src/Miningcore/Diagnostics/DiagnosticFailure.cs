using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
        AuthenticationException => "tls-handshake",
        CryptographicException => "cryptographic",
        JsonException => "json",
        FormatException => "format",
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
        IBoundedShareFailure share => share.DiagnosticKind switch
        {
            ShareFailureKind.JobNotFound => "job-not-found",
            ShareFailureKind.DuplicateShare => "duplicate-share",
            ShareFailureKind.LowDifficultyShare => "low-difficulty-share",
            ShareFailureKind.UnauthorizedWorker => "unauthorized-worker",
            ShareFailureKind.NotSubscribed => "not-subscribed",
            ShareFailureKind.InvalidJobChainIndex => "invalid-job-chain-index",
            ShareFailureKind.InvalidWorker => "invalid-worker",
            ShareFailureKind.InvalidNonce => "invalid-nonce",
            ShareFailureKind.InvalidBlockChainIndex => "invalid-block-chain-index",
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
        IBoundedShareFailure share => share.DiagnosticCode,
        _ => null,
    };
}
