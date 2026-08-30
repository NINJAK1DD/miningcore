using Miningcore.Blockchain;
using Miningcore.Persistence.Model;

namespace Miningcore.Notifications.Messages;

public sealed record ShareAccountingProjectionTelemetry(
    string PoolId, ShareAccountingRole Role);

public sealed record ShareAccountingPpsTelemetry(string PoolId, decimal Amount);

public sealed record ShareAccountingTelemetryEvent(
    Guid AccountingId,
    ShareAccountingInsertResult Outcome,
    ShareAccountingProjectionTelemetry[] Projections,
    ShareAccountingPpsTelemetry[] PpsCredits);

public sealed record UnsupportedShareRelayWireFormatTelemetryEvent(
    string RelayUrl, int WireFormat);

public enum MergedMiningAttributionRejection
{
    Missing,
    Invalid,
    ValidationUnavailable,
}

public sealed record MergedMiningAttributionRejectedTelemetryEvent(
    string ParentPoolId,
    string AuxiliaryPoolId,
    MergedMiningAttributionRejection Reason);
