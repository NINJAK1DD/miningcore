using Miningcore.Diagnostics;

namespace Miningcore.Payments;

public enum PaymentFailureReason { Unknown, WalletPasswordMissing }

// Created independently of the retained free-form error. Never infer a safe hint
// by matching or echoing a daemon message.
internal sealed record PaymentFailureDiagnostic
{
    private PaymentFailureDiagnostic() { }
    internal string Category { get; private init; }
    internal int? Code { get; private init; }
    internal int? FailureCode { get; private init; }
    internal string Hint { get; private init; }

    internal static PaymentFailureDiagnostic Create(Exception exception = null,
        int? daemonCode = null, PaymentFailureReason reason = PaymentFailureReason.Unknown) => new()
    {
        Category = DiagnosticFailure.Category(exception),
        Code = daemonCode,
        FailureCode = DiagnosticFailure.Code(exception),
        Hint = reason == PaymentFailureReason.WalletPasswordMissing
            ? "Wallet is locked but walletPassword was not configured. Configure wallet unlocking before retrying."
            : null,
    };

    internal string Summary => $"Failure category: {Category}; daemon code: {Code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; transport code: {FailureCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}." +
        (Hint == null ? string.Empty : " " + Hint);
}
