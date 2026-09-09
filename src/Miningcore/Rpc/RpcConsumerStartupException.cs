using Miningcore.Mining;
using Newtonsoft.Json;

namespace Miningcore.Rpc;

internal sealed class RpcConsumerStartupException : PoolStartupException
{
    internal RpcConsumerStartupException(string poolId, Exception originalFailure)
        : base($"Pool '{poolId}' daemon startup failed ({RpcConsumerDiagnostics.Failure(originalFailure)}). " +
            "Inspect RPC consumer diagnostics and verify daemon readiness, credentials, network and template compatibility.", poolId)
    {
        OriginalFailure = originalFailure;
    }

    // Not InnerException: generic host exception logging must not render remote
    // payloads. RPC results and the original failure itself are not rewritten.
    [JsonIgnore]
    internal Exception OriginalFailure { get; }
}
