using System.Globalization;
using Miningcore.Configuration;
using NLog;

namespace Miningcore.Api.Extensions;

internal static class PaymentProcessingExtraDiagnostics
{
    internal const int MaximumKeyDiagnosticsPerPool = 10;
    private const int MaximumRuntimeFieldHints = 5;

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
            var runtimeOnlyNames = PaymentProcessingExtraProjection.
                GetRuntimeOnlyContractNames(pool.Template.Family);
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
                var reason = GetReason(omission.Outcome);
                var description = reason.Describe(omission.VariantCount);
                var remediation = GetRemediation(reason, omission,
                    runtimeOnlyNames);
                logger.Warn(() =>
                    $"Pool '{poolId}' payment-processing extension key " +
                    $"'{omission.DiagnosticKey}' is omitted from the public " +
                    $"API (reason={reason.Code}: {description}). " +
                    $"No value was logged; {remediation}");
            }

            var remainder = omissions.Skip(MaximumKeyDiagnosticsPerPool)
                .ToArray();
            if(remainder.Length == 0)
                continue;

            var counts = string.Join(", ", remainder
                .GroupBy(omission => omission.Outcome)
                .OrderBy(group => group.Key)
                .Select(group =>
                    $"{GetReason(group.Key).Code}={group.Count()}"));

            logger.Warn(() =>
                $"Pool '{poolId}' has {remainder.Length} additional " +
                "payment-processing extension key omission(s) beyond the " +
                $"{MaximumKeyDiagnosticsPerPool}-entry diagnostic limit " +
                $"({counts}). Sensitive-looking keys are included in these " +
                "counts but remain redacted; no values were logged");
        }
    }

    private static PaymentProcessingExtraReason GetReason(
        PaymentProcessingExtraProjectionOutcome outcome) => outcome switch
        {
            PaymentProcessingExtraProjectionOutcome.UnknownKey =>
                new("unknown-key",
                    "not recognised by this coin family's runtime or public contract",
                    "check the spelling and family-specific runtime contract, " +
                    "then correct or remove the setting"),
            PaymentProcessingExtraProjectionOutcome.AmbiguousCaseVariant =>
                new("ambiguous-case",
                    "{0} case variants match one recognised key",
                    "retain exactly one spelling of the setting"),
            PaymentProcessingExtraProjectionOutcome.NonScalarValue =>
                new("non-scalar",
                    "the approved key requires a boolean, number, string or null JSON value",
                    "replace the value with a supported scalar representation"),
            PaymentProcessingExtraProjectionOutcome.ConversionFailure =>
                new("conversion-failure",
                    "the scalar cannot convert to the approved runtime type",
                    "correct the value for the family-specific public contract"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
                "Only omitted payment-processing outcomes can be logged"),
        };

    private static string GetRemediation(PaymentProcessingExtraReason reason,
        PaymentProcessingExtraOmission omission,
        IReadOnlyList<string> runtimeOnlyNames)
    {
        if(omission.Outcome != PaymentProcessingExtraProjectionOutcome.UnknownKey ||
           !omission.KeyWasRedacted || runtimeOnlyNames.Count == 0)
        {
            return reason.Remediation;
        }

        var fields = runtimeOnlyNames.Take(MaximumRuntimeFieldHints)
            .Select(name => PaymentProcessingExtraSensitivityPolicy.
                CreateDiagnosticLabel(name, "<empty-runtime-field>"))
            .Select(name => $"'{name}'")
            .ToArray();
        var remainder = runtimeOnlyNames.Count - fields.Length;
        var suffix = remainder > 0 ? $" and {remainder} more" : string.Empty;

        return $"{reason.Remediation}; compare it with this family's " +
            $"recognised private field(s): {string.Join(", ", fields)}{suffix}";
    }

    private sealed record PaymentProcessingExtraReason(string Code,
        string DescriptionFormat, string Remediation)
    {
        public string Describe(int variantCount) => string.Format(
            CultureInfo.InvariantCulture, DescriptionFormat, variantCount);
    }
}
