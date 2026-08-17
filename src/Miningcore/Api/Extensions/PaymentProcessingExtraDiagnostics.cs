using Miningcore.Configuration;
using NLog;

namespace Miningcore.Api.Extensions;

internal static class PaymentProcessingExtraDiagnostics
{
    internal const int MaximumKeyDiagnosticsPerPool = 10;

    internal static void Log(IEnumerable<PoolConfig> pools, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(logger);

        foreach(var pool in pools)
        {
            ArgumentNullException.ThrowIfNull(pool);
            ArgumentNullException.ThrowIfNull(pool.Template);

            var analysis = PaymentProcessingExtraProjection.Analyze(
                pool.Template.Family, pool.PaymentProcessing?.Extra);
            var omissions = analysis.Omissions.Where(omission =>
                    omission.Outcome != PaymentProcessingExtraProjectionOutcome.
                        RuntimeOnlyKey)
                .ToArray();
            if(omissions.Length == 0)
                continue;

            var poolId = PaymentProcessingExtraSensitivityPolicy.
                CreateDiagnosticLabel(pool.Id, "<empty-pool-id>");

            foreach(var omission in omissions.Take(
                        MaximumKeyDiagnosticsPerPool))
            {
                var reason = GetReason(omission);
                logger.Warn(() =>
                    $"Pool '{poolId}' payment-processing extension key " +
                    $"'{omission.DiagnosticKey}' is omitted from the public " +
                    $"API (reason={reason.Code}: {reason.Description}). " +
                    $"No value was logged; {reason.Remediation}");
            }

            var remainder = omissions.Skip(MaximumKeyDiagnosticsPerPool)
                .ToArray();
            if(remainder.Length == 0)
                continue;

            var counts = string.Join(", ", remainder
                .GroupBy(omission => omission.Outcome)
                .OrderBy(group => group.Key)
                .Select(group =>
                    $"{GetReasonCode(group.Key)}={group.Count()}"));

            logger.Warn(() =>
                $"Pool '{poolId}' has {remainder.Length} additional " +
                "payment-processing extension key omission(s) beyond the " +
                $"{MaximumKeyDiagnosticsPerPool}-entry diagnostic limit " +
                $"({counts}). Sensitive-looking keys are included in these " +
                "counts but remain redacted; no values were logged");
        }
    }

    private static (string Code, string Description, string Remediation)
        GetReason(PaymentProcessingExtraOmission omission) =>
        omission.Outcome switch
        {
            PaymentProcessingExtraProjectionOutcome.UnknownKey =>
                ("unknown-key",
                    "not recognised by this coin family's runtime or public contract",
                    "check the spelling and family-specific runtime contract, " +
                    "then correct or remove the setting"),
            PaymentProcessingExtraProjectionOutcome.AmbiguousCaseVariant =>
                ("ambiguous-case",
                    $"{omission.VariantCount} case variants match one recognised key",
                    "retain exactly one spelling of the setting"),
            PaymentProcessingExtraProjectionOutcome.NonScalarValue =>
                ("non-scalar",
                    "the approved key requires a boolean, number, string or null JSON value",
                    "replace the value with a supported scalar representation"),
            PaymentProcessingExtraProjectionOutcome.ConversionFailure =>
                ("conversion-failure",
                    "the scalar cannot convert to the approved runtime type",
                    "correct the value for the family-specific public contract"),
            _ => throw new ArgumentOutOfRangeException(nameof(omission),
                omission.Outcome,
                "Only omitted payment-processing outcomes can be logged"),
        };

    private static string GetReasonCode(
        PaymentProcessingExtraProjectionOutcome outcome) => outcome switch
        {
            PaymentProcessingExtraProjectionOutcome.UnknownKey =>
                "unknown-key",
            PaymentProcessingExtraProjectionOutcome.AmbiguousCaseVariant =>
                "ambiguous-case",
            PaymentProcessingExtraProjectionOutcome.NonScalarValue =>
                "non-scalar",
            PaymentProcessingExtraProjectionOutcome.ConversionFailure =>
                "conversion-failure",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                "Only actionable payment-processing outcomes can be summarized"),
        };
}
