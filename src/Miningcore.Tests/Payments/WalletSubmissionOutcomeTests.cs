using System;
using System.IO;
using System.Threading.Tasks;
using Miningcore.JsonRpc;
using Miningcore.Payments;
using Xunit;

namespace Miningcore.Tests.Payments;

public class WalletSubmissionOutcomeTests
{
    [Fact]
    public void TransportRpcError_IsUnknown()
    {
        var error = new JsonRpcError(-500, "connection reset", null,
            new IOException("response lost"));

        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            WalletSubmissionOutcome.ThrowIfUnknown(error, "send transaction"));
    }

    [Fact]
    public void CancelledRpcError_IsUnknownAndPreservesCause()
    {
        var cancellation = new TaskCanceledException("wallet request cancelled");
        var error = new JsonRpcError(-500, "Cancelled", null, cancellation);

        var exception = Assert.Throws<PayoutOutcomeUncertainException>(() =>
            WalletSubmissionOutcome.ThrowIfUnknown(error, "send transaction"));

        Assert.Same(cancellation, exception.InnerException);
    }

    [Fact]
    public void ConclusiveRpcRejection_IsNotUnknown()
    {
        var error = new JsonRpcError(-6, "insufficient funds", null);

        WalletSubmissionOutcome.ThrowIfUnknown(error, "send transaction");
    }

    [Fact]
    public void SuccessWithoutTransactionIdentity_IsUnknown()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            WalletSubmissionOutcome.RequireTransactionId(null, "send transaction"));
    }

    [Fact]
    public void NestedTransportException_IsUnknown()
    {
        var error = new InvalidOperationException("wallet call failed",
            new IOException("connection reset"));

        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            WalletSubmissionOutcome.RethrowIfUnknown(error, "send transaction"));
    }

    [Fact]
    public void MalformedSubmissionResponse_IsUnknown()
    {
        var error = new Newtonsoft.Json.JsonSerializationException(
            "wallet response did not contain the expected transaction object");

        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            WalletSubmissionOutcome.RethrowIfUnknown(error, "send transaction"));
    }

    [Fact]
    public void ValidationException_IsConclusive()
    {
        WalletSubmissionOutcome.RethrowIfUnknown(
            new ArgumentException("invalid address"), "send transaction");
    }
}
