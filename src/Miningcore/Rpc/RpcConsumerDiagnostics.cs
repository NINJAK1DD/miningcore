using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Rpc;

// Output-only projection. Never rewrite daemon responses, exception messages, or
// reconciliation evidence: application code still needs the original values.
internal static class RpcConsumerDiagnostics
{
    internal static string Failure(Exception exception) => exception switch
    {
        null => "unknown",
        OperationCanceledException { InnerException: TimeoutException } => "timeout",
        OperationCanceledException => "cancelled",
        TimeoutException => "timeout",
        JsonException => "json",
        HttpRequestException => "http",
        System.Net.WebSockets.WebSocketException => "websocket",
        System.Net.Sockets.SocketException => "socket",
        InvalidDataException => "invalid-data",
        IOException => "io",
        ObjectDisposedException => "disposed",
        ArgumentException => "argument",
        _ => "other",
    };

    // No exception is ever attached to the NLog event. The finite catalogue also
    // rejects arbitrary labels from future/plugin callers.
    internal static void Write(ILogger logger, LogLevel level, string operation,
        Exception failure = null, int? code = null)
    {
        if(!logger.IsEnabled(level))
            return;

        logger.Log(level, "RPC consumer diagnostic " + new JObject
        {
            ["operation"] = operation != null && RpcConsumerOperations.All.Contains(operation) ? operation : "other",
            ["failure"] = failure == null ? null : Failure(failure),
            ["code"] = code,
        }.ToString(Formatting.None));
    }

    internal const string WithheldError =
        "Remote operation failed; sensitive error detail is withheld. See the operation diagnostic and reconcile before retrying an uncertain payment.";

    // Malformed wallet responses may put arbitrary text in a purported txid.
    // Keep the exact returned value in reconciliation evidence, not in an alert.
    internal static string TransactionId(string value)
    {
        var hex = value != null && value.StartsWith("0x", StringComparison.Ordinal) ? value.AsSpan(2) : value.AsSpan();
        return hex.Length == 64 && hex.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            ? value : "[unverified transaction identifier withheld]";
    }
}
