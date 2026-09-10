using System.Security.Cryptography;
using Miningcore.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Miningcore.Stratum;

// Output-only: never modify the request, reply, exception or admission decision.
// Connection IDs come from the server, never JSON-RPC IDs or worker/session input.
internal static class StratumDiagnostics
{
    internal enum Event
    {
        Receive, Buffer, Send, ProxyHeader, Request, ConnectionError,
        AcceptError, ListenError, TerminalCallback, UntrackedCompletion,
        Completion, TaskRemoval, Drain, CertificateLoad, ReceiveWait, BufferWait,
    }

    internal static string Method(string method) => method switch
    {
        "mining.subscribe" or "mining.authorize" or "mining.suggest_difficulty" or
        "mining.notify" or "mining.submit" or "mining.set_difficulty" or
        "mining.get_transactions" or "mining.extranonce.subscribe" or
        "mining.multi_version" or "mining.configure" or "mining.hello" or
        "mining.noop" or "mining.set_extranonce" or "mining.ping" or
        "mining.pong" or "mining.print" or "mining.hashrate" or
        "mining.set_target" or "mining.suggest_target" or "connection.set_gzip" or
        "client.mining.unknown" or "set_extranonce" or "alph_submitHashrate" or "eth_submitLogin" or
        "eth_getWork" or "eth_submitWork" or "eth_submitHashrate" or
        // Bundled Cortex Ethash V1 methods are composed from the ctxc coin prefix.
        // Keep exact literals: accepting arbitrary prefixes would reopen this boundary.
        "ctxc_submitLogin" or "ctxc_getWork" or "ctxc_submitWork" or "ctxc_submitHashrate" or
        "_submitLogin" or "_getWork" or "_submitWork" or "_submitHashrate" or
        "login" or "job" or "getjob" or "submit" or "solution" or "keepalived" => method,
        _ => "other",
    };

    internal static void Write(ILogger logger, LogLevel level, Event operation,
        string connectionId = null, Exception failure = null, string method = null,
        long? bytes = null, int? port = null)
    {
        if(!logger.IsEnabled(level))
            return;

        // A concrete token tree avoids ambient Json.NET converters/settings.
        // Never attach the exception: NLog layouts can render it independently.
        logger.Log(level, "Stratum diagnostic " + new JObject
        {
            ["event"] = Enum.IsDefined(operation) ? operation.ToString() : "other",
            ["connectionId"] = connectionId,
            ["failure"] = failure == null ? null : DiagnosticFailure.Category(failure),
            ["code"] = DiagnosticFailure.Code(failure),
            ["method"] = operation == Event.Request ? Method(method) : null,
            ["bytes"] = bytes,
            ["port"] = port,
            ["reason"] = operation == Event.CertificateLoad ? CertificateReason(failure) : null,
        }.ToString(Formatting.None));
    }

    // Certificate loaders can wrap file-access failures in CryptographicException.
    // Inspect only a bounded structural chain, never messages, paths or HRESULT text.
    internal static string CertificateReason(Exception failure)
    {
        for(var depth = 0; depth < 4 && failure is CryptographicException { InnerException: not null }; depth++)
            failure = failure.InnerException;

        if(failure is CryptographicException { InnerException: not null })
            return "other";

        return failure switch
        {
            null => null,
            FileNotFoundException or DirectoryNotFoundException => "file-not-found",
            UnauthorizedAccessException => "access-denied",
            IOException => "file-io",
            CryptographicException => "invalid-certificate-or-password",
            _ => "other",
        };
    }
}
