using System;
using Miningcore.Payments;
using Miningcore.Persistence;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PostgresPayoutManagerLeaseTests
{
    [Fact]
    public void IdleLease_CanReleaseOwnership()
    {
        var lease = new PostgresPayoutManagerLease(Substitute.For<IConnectionFactory>());

        Assert.True(lease.CanReleaseOwnership);
    }

    [Fact]
    public void ActiveFinancialOperation_RetainsOwnership()
    {
        var lease = new PostgresPayoutManagerLease(Substitute.For<IConnectionFactory>());

        lease.BeginFinancialOperation();

        Assert.False(lease.CanReleaseOwnership);

        lease.CompleteFinancialOperation();

        Assert.True(lease.CanReleaseOwnership);
    }

    [Fact]
    public void UnknownFinancialOutcome_RetainsOwnershipAfterOperationEnds()
    {
        var lease = new PostgresPayoutManagerLease(Substitute.For<IConnectionFactory>());

        lease.BeginFinancialOperation();
        lease.MarkFinancialOutcomeUncertain();

        Assert.False(lease.CanReleaseOwnership);
        Assert.Throws<InvalidOperationException>(() => lease.BeginFinancialOperation());
    }
}
