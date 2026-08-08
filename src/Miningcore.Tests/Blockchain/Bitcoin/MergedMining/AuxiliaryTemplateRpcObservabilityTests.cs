using System;
using System.Linq;
using System.Net.Http;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.JsonRpc;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxiliaryTemplateRpcObservabilityTests
{
    [Theory]
    [InlineData("success", false, false, (int) AuxiliaryTemplateRpcOutcome.Success)]
    [InlineData("rpc_error", false, false, (int) AuxiliaryTemplateRpcOutcome.RpcError)]
    [InlineData("cancelled", false, true, (int) AuxiliaryTemplateRpcOutcome.Timeout)]
    [InlineData("cancelled", true, true, (int) AuxiliaryTemplateRpcOutcome.Cancellation)]
    [InlineData("transport", false, false, (int) AuxiliaryTemplateRpcOutcome.TransportFailure)]
    [InlineData("malformed", false, false, (int) AuxiliaryTemplateRpcOutcome.RpcError)]
    [InlineData("cancelled", false, false, (int) AuxiliaryTemplateRpcOutcome.TransportFailure)]
    [InlineData("empty", false, false, (int) AuxiliaryTemplateRpcOutcome.TransportFailure)]
    public void Classifier_DistinguishesEveryBoundedOutcome(
        string responseKind, bool callerCancellationWon,
        bool deadlineWon, int expected)
    {
        var response = CreateResponse(responseKind);
        var result = MergedMiningBitcoinJobManager
            .ClassifyAuxiliaryTemplateRpcOutcome(response, callerCancellationWon,
                deadlineWon);

        Assert.Equal((AuxiliaryTemplateRpcOutcome) expected, result);
    }

    [Theory]
    [InlineData(true, false, (int) AuxiliaryTemplateRpcOutcome.Cancellation)]
    [InlineData(false, true, (int) AuxiliaryTemplateRpcOutcome.Timeout)]
    [InlineData(true, true, (int) AuxiliaryTemplateRpcOutcome.Cancellation)]
    public void Classifier_RaceWinnerOverridesLateSuccessfulResponse(
        bool callerCancellationWon, bool deadlineWon,
        int expected)
    {
        var response = new RpcResponse<AuxBlockTemplate>(new AuxBlockTemplate());

        var result = MergedMiningBitcoinJobManager
            .ClassifyAuxiliaryTemplateRpcOutcome(response, callerCancellationWon,
                deadlineWon);

        Assert.Equal((AuxiliaryTemplateRpcOutcome) expected, result);
    }

    [Fact]
    public void TimeoutDescription_ReportsConfiguredDeadlineInsteadOfCancelled()
    {
        var result = new AuxiliaryTemplateRpcResult(
            ErrorResponse(new JsonRpcError(-500, "Cancelled", null)),
            AuxiliaryTemplateRpcOutcome.Timeout,
            TimeSpan.FromMilliseconds(1000));

        var description = MergedMiningBitcoinJobManager
            .DescribeAuxiliaryTemplateRpcFailure(result);

        Assert.Equal("timed out after 1000 ms", description);
    }

    [Fact]
    public void SuccessDescription_IsBenignIfCalledDefensively()
    {
        var result = new AuxiliaryTemplateRpcResult(
            new RpcResponse<AuxBlockTemplate>(new AuxBlockTemplate()),
            AuxiliaryTemplateRpcOutcome.Success,
            TimeSpan.FromMilliseconds(500));

        var description = MergedMiningBitcoinJobManager
            .DescribeAuxiliaryTemplateRpcFailure(result);

        Assert.Equal("completed successfully", description);
    }

    [Fact]
    public void MetricLabels_AreStableAndBounded()
    {
        var outcomeLabels = Enum.GetValues<AuxiliaryTemplateRpcOutcome>()
            .Select(MetricsPublisher.GetAuxiliaryTemplateRpcOutcomeLabel)
            .ToArray();
        var phaseLabels = Enum.GetValues<AuxiliaryTemplateRpcPhase>()
            .Select(MetricsPublisher.GetAuxiliaryTemplateRpcPhaseLabel)
            .ToArray();

        Assert.Equal(new[]
        {
            "success",
            "rpc_error",
            "timeout",
            "cancellation",
            "transport_failure",
        }, outcomeLabels);
        Assert.Equal(outcomeLabels.Length,
            outcomeLabels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "startup", "refresh" }, phaseLabels);
        Assert.Equal(phaseLabels.Length,
            phaseLabels.Distinct(StringComparer.Ordinal).Count());
    }

    private static RpcResponse<AuxBlockTemplate> ErrorResponse(JsonRpcError error) =>
        new(null, error);

    private static RpcResponse<AuxBlockTemplate> CreateResponse(string kind) =>
        kind switch
        {
            "success" => new RpcResponse<AuxBlockTemplate>(new AuxBlockTemplate()),
            "rpc_error" => ErrorResponse(new JsonRpcError(-1,
                "daemon rejected", null)),
            "cancelled" => ErrorResponse(new JsonRpcError(-500,
                "Cancelled", null)),
            "transport" => ErrorResponse(new JsonRpcError(-500,
                "connection refused", null,
                new HttpRequestException("connection refused"))),
            "malformed" => ErrorResponse(new JsonRpcError(-500,
                "invalid JSON", null, new JsonReaderException("invalid JSON"))),
            "empty" => new RpcResponse<AuxBlockTemplate>(null),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unknown auxiliary-template response test case"),
        };
}
