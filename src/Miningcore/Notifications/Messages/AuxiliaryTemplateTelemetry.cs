namespace Miningcore.Notifications.Messages;

internal enum AuxiliaryTemplateRpcOutcome
{
    Success,
    RpcError,
    Timeout,
    Cancellation,
    TransportFailure,
}

internal enum AuxiliaryTemplateRpcPhase
{
    Startup,
    Refresh,
}

internal record AuxiliaryTemplateRpcTelemetryEvent(string ParentPoolId,
    string AuxiliaryPoolId, AuxiliaryTemplateRpcPhase Phase,
    AuxiliaryTemplateRpcOutcome Outcome, TimeSpan Elapsed);

internal record AuxiliaryTemplateStateTelemetryEvent(string ParentPoolId,
    string AuxiliaryPoolId, bool Available, bool Degraded,
    bool FallbackStarted);
