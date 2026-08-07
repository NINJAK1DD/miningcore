namespace Miningcore.Notifications.Messages;

internal enum AuxiliaryTemplateRpcOutcome
{
    Success,
    RpcError,
    Timeout,
    Cancellation,
    TransportFailure,
}

internal record AuxiliaryTemplateRpcTelemetryEvent(string PoolId,
    AuxiliaryTemplateRpcOutcome Outcome, TimeSpan Elapsed);

internal record AuxiliaryTemplateStateTelemetryEvent(string PoolId,
    bool Degraded, bool FallbackUsed);
