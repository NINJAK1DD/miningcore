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
            PaymentProcessingExtraProjectionResult analysis;

            try
            {
                analysis = PaymentProcessingExtraProjection.Analyze(
                    pool.Template.Family, pool.PaymentProcessing?.Extra);
            }
            catch(Exception)
            {
                // Diagnostics are advisory. Never fail pool startup or log an
                // exception whose text could contain a rejected value.
                logger.Warn(() =>
                    $"Pool '{pool.Id}' payment-processing response omissions " +
                    "could not be classified safely; the public API remains " +
                    "fail-closed and no extension values were logged");
                continue;
            }

            var omissions = analysis.Omissions;
            if(omissions.Count == 0)
                continue;

            foreach(var omission in omissions.Take(
                        MaximumKeyDiagnosticsPerPool))
            {
                var reason = GetReason(omission.Outcome);
                logger.Warn(() =>
                    $"Pool '{pool.Id}' payment-processing extension key " +
                    $"'{omission.DiagnosticKey}' is omitted from the public " +
                    $"API (reason={reason.Code}: {reason.Description}). " +
                    "No value was logged; correct or remove the setting after " +
                    "checking its family-specific runtime contract");
            }

            var remainder = omissions.Skip(MaximumKeyDiagnosticsPerPool)
                .ToArray();
            if(remainder.Length == 0)
                continue;

            var counts = string.Join(", ", remainder
                .GroupBy(omission => omission.Outcome)
                .OrderBy(group => group.Key)
                .Select(group => $"{GetReason(group.Key).Code}={group.Count()}"));

            logger.Warn(() =>
                $"Pool '{pool.Id}' has {remainder.Length} additional " +
                "payment-processing extension key omission(s) beyond the " +
                $"{MaximumKeyDiagnosticsPerPool}-entry diagnostic limit " +
                $"({counts}). Sensitive-looking keys are included in these " +
                "counts but remain redacted; no values were logged");
        }
    }

    private static (string Code, string Description) GetReason(
        PaymentProcessingExtraProjectionOutcome outcome) => outcome switch
        {
            PaymentProcessingExtraProjectionOutcome.UnknownKey =>
                ("unknown-key", "not approved for this coin family"),
            PaymentProcessingExtraProjectionOutcome.AmbiguousCaseVariant =>
                ("ambiguous-case", "multiple case variants match one approved key"),
            PaymentProcessingExtraProjectionOutcome.NonScalarValue =>
                ("non-scalar", "the approved key requires a scalar JSON value"),
            PaymentProcessingExtraProjectionOutcome.ConversionFailure =>
                ("conversion-failure",
                    "the scalar cannot convert to the approved runtime type"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                "Only omitted payment-processing outcomes can be logged"),
        };
}
