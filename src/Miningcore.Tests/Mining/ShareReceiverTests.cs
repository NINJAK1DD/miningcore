using Miningcore.Blockchain;
using Miningcore.Mining;
using Xunit;

namespace Miningcore.Tests.Mining;

public class ShareReceiverTests
{
    [Fact]
    public void BlockOnlyRelayMessage_IsNotEligibleForOrdinaryShareTelemetry()
    {
        var share = new Share { BlockOnly = true, IsBlockCandidate = true };

        Assert.False(ShareReceiver.IsShareTelemetryEligible(share));
    }

    [Fact]
    public void OrdinaryRelayShare_RemainsEligibleForShareTelemetry()
    {
        Assert.True(ShareReceiver.IsShareTelemetryEligible(new Share()));
    }
}
