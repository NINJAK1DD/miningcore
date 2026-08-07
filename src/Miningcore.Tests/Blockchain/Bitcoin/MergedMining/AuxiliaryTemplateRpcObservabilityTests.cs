using System;
using System.Linq;
using System.Net.Http;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Bitcoin.MergedMining;
using Miningcore.JsonRpc;
using Miningcore.Notifications;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin.MergedMining;

public class AuxiliaryTemplateRpcObservabilityTests
{
    public static TheoryData<RpcResponse<AuxBlockTemplate>, bool, bool,
        int> Outcomes => new()
    {
        {
            new RpcResponse<AuxBlockTemplate>(new AuxBlockTemplate()),
            false, false, (int) AuxiliaryTemplateRpcOutcome.Success
        },
        {
            ErrorResponse(new JsonRpcError(-1, "daemon rejected", null)),
            false, false, (int) AuxiliaryTemplateRpcOutcome.RpcError
        },
        {
            ErrorResponse(new JsonRpcError(-500, "Cancelled", null)),
            false, true, (int) AuxiliaryTemplateRpcOutcome.Timeout
        },
        {
            ErrorResponse(new JsonRpcError(-500, "Cancelled", null)),
            true, true, (int) AuxiliaryTemplateRpcOutcome.Cancellation
        },
        {
            ErrorResponse(new JsonRpcError(-500, "connection refused", null,
                new HttpRequestException("connection refused"))),
            false, false, (int) AuxiliaryTemplateRpcOutcome.TransportFailure
        },
        {
            new RpcResponse<AuxBlockTemplate>(null),
            false, false, (int) AuxiliaryTemplateRpcOutcome.TransportFailure
        },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Classifier_DistinguishesEveryBoundedOutcome(
        RpcResponse<AuxBlockTemplate> response, bool callerCancelled,
        bool timeoutCancelled, int expected)
    {
        var result = MergedMiningBitcoinJobManager
            .ClassifyAuxiliaryTemplateRpcOutcome(response, callerCancelled,
                timeoutCancelled);

        Assert.Equal((AuxiliaryTemplateRpcOutcome) expected, result);
    }

    [Fact]
    public void TimeoutDescription_ReportsConfiguredDeadlineInsteadOfCancelled()
    {
        var result = new AuxiliaryTemplateRpcResult(
            ErrorResponse(new JsonRpcError(-500, "Cancelled", null)),
            AuxiliaryTemplateRpcOutcome.Timeout,
            TimeSpan.FromMilliseconds(1004),
            TimeSpan.FromMilliseconds(1000));

        var description = MergedMiningBitcoinJobManager
            .DescribeAuxiliaryTemplateRpcFailure(result);

        Assert.Equal("timed out after 1000 ms", description);
    }

    [Fact]
    public void MetricLabels_AreStableAndBounded()
    {
        var labels = Enum.GetValues<AuxiliaryTemplateRpcOutcome>()
            .Select(MetricsPublisher.GetAuxiliaryTemplateRpcOutcomeLabel)
            .ToArray();

        Assert.Equal(new[]
        {
            "success",
            "rpc_error",
            "timeout",
            "cancellation",
            "transport_failure",
        }, labels);
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    private static RpcResponse<AuxBlockTemplate> ErrorResponse(JsonRpcError error) =>
        new(null, error);
}
