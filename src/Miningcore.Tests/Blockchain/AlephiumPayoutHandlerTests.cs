using System.Collections.ObjectModel;
using Miningcore.Blockchain.Alephium;
using Miningcore.Payments;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class AlephiumPayoutHandlerTests
{
    [Fact]
    public void ParseSweepResults_RejectsNullEnvelope()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(null));
    }

    [Fact]
    public void ParseSweepResults_RejectsNullCollection()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = null,
            }));
    }

    [Fact]
    public void ParseSweepResults_RejectsEmptyCollection()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults()));
    }

    [Fact]
    public void ParseSweepResults_RejectsNullEntry()
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = new Collection<TransferResult> { null },
            }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSweepResults_RejectsBlankTransactionId(string txId)
    {
        Assert.Throws<PayoutOutcomeUncertainException>(() =>
            AlephiumPayoutHandler.ParseSweepResults(new TransferResults
            {
                Results = new Collection<TransferResult>
                {
                    new() { TxId = txId },
                },
            }));
    }

    [Fact]
    public void ParseSweepResults_ReturnsEveryValidResult()
    {
        var first = new TransferResult { TxId = "tx-1", FromGroup = 0, ToGroup = 1 };
        var second = new TransferResult { TxId = "tx-2", FromGroup = 2, ToGroup = 3 };

        var result = AlephiumPayoutHandler.ParseSweepResults(new TransferResults
        {
            Results = new Collection<TransferResult> { first, second },
        });

        Assert.Equal(new[] { first, second }, result);
    }
}
