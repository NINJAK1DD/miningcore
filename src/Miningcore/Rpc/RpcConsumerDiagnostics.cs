using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Rpc;

// Output-only projection. Never rewrite daemon responses, exception messages, or
// reconciliation evidence: application code still needs the original values.
internal static class RpcConsumerDiagnostics
{
    internal static string Failure(Exception exception) => Diagnostics.DiagnosticFailure.Category(exception);
    internal enum Stage { Connect, Request, Response, Rejected, Degraded, Unavailable }

    // No exception is ever attached to the NLog event. The finite catalogue also
    // rejects arbitrary labels from future/plugin callers.
    internal static void Write(ILogger logger, LogLevel level, string operation,
        Exception failure = null, int? code = null, string poolId = null,
        int? failedCount = null, Stage? stage = null, string connectionId = null)
    {
        if(!logger.IsEnabled(level))
            return;

        logger.Log(level, "RPC consumer diagnostic " + new JObject
        {
            ["operation"] = operation != null && RpcConsumerOperations.All.Contains(operation) ? operation : "other",
            ["failure"] = failure == null ? null : Failure(failure),
            ["code"] = code,
            ["failureCode"] = Diagnostics.DiagnosticFailure.Code(failure),
            ["poolId"] = poolId,
            ["failedCount"] = failedCount,
            ["stage"] = stage?.ToString(),
            ["connectionId"] = connectionId,
        }.ToString(Formatting.None));
    }

    internal const string WithheldError =
        "Remote operation failed; sensitive error detail is withheld. See the operation diagnostic.";

    // Malformed wallet responses may put arbitrary text in a purported txid.
    // Keep the exact returned value in reconciliation evidence, not in an alert.
    internal static string TransactionId(string value)
        => IsHexIdentifier(value) ? value : "[unverified transaction identifier withheld]";

    // Block hashes have distinct operator-facing semantics from payment txids.
    // Callers supply any surrounding punctuation; null is absence, not rejection.
    internal static string BlockHash(string value)
        => value == null ? "(none)" : IsHexIdentifier(value) ? value : "withheld";

    private static bool IsHexIdentifier(string value)
    {
        var hex = value != null && value.StartsWith("0x", StringComparison.Ordinal) ? value.AsSpan(2) : value.AsSpan();
        return hex.Length == 64 && hex.IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }
}
