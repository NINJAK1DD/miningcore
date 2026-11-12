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
    public void EmptyArgumentProbeTransportFailure_IsConclusiveButUnexpectedSuccessIsNot()
    {
        var cause = new TaskCanceledException("cancelled");
        var error = Assert.Throws<InvalidOperationException>(() => WalletSubmissionOutcome.RequireRejectedProbe(
            new JsonRpcError(-500, "Cancelled", null, cause), "empty probe", hasNoTransferArguments: true));
        Assert.Same(cause, error.InnerException);
        Assert.Throws<PayoutOutcomeUncertainException>(() => WalletSubmissionOutcome.RequireRejectedProbe(
            null, "empty probe", hasNoTransferArguments: true));
    }

    [Fact]
    public void PayoutSnapshotDigest_IsOrderIndependentAndChangesWithLiabilities()
    {
        var balances = new[] { new Miningcore.Persistence.Model.Balance { Address = "a", Amount = 1m },
            new Miningcore.Persistence.Model.Balance { Address = "b", Amount = 2m } };
        var snapshot = WalletPayoutBatch.Create("pool", balances);
        Assert.Equal(snapshot, WalletPayoutBatch.Create("pool", new[] { balances[1], balances[0] }));
        Assert.NotEqual(snapshot.Digest, WalletPayoutBatch.Create("other", balances).Digest);
        balances[0].Amount++;
        Assert.NotEqual(snapshot.Digest, WalletPayoutBatch.Create("pool", balances).Digest);
        Assert.Equal(64, snapshot.Digest.Length);
    }
    [Theory]
    [InlineData(-32601)]
    [InlineData(-32602)]
    [InlineData(-8)]
    public void RejectedCapabilityProbe_IsConclusive(int code) =>
        WalletSubmissionOutcome.RequireRejectedProbe(new JsonRpcError(code, "rejected", null), "probe");

    [Fact]
    public void SuccessfulOrUnknownCapabilityProbe_RequiresReconciliation()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() => WalletSubmissionOutcome.RequireRejectedProbe(null, "probe"));
        Assert.Throws<PayoutOutcomeUncertainException>(() => WalletSubmissionOutcome.RequireRejectedProbe(
            new JsonRpcError(-500, "lost", null), "probe"));
    }

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
