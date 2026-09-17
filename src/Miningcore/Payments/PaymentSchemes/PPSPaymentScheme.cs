using System.Data;
using Miningcore.Mining;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;

namespace Miningcore.Payments.PaymentSchemes;

/// <summary>
/// Pay Per Share settlement boundary. Miner liabilities are created atomically when a valid
/// share is persisted; a later block outcome must never credit or debit those liabilities.
/// </summary>
public sealed class PPSPaymentScheme : IPayoutScheme
{
    public PPSPaymentScheme(IShareRepository shareRepo)
    {
        ArgumentNullException.ThrowIfNull(shareRepo);
    }

    public Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx,
        IMiningPool pool, IPayoutHandler payoutHandler, Block block,
        decimal blockReward, CancellationToken ct)
    {
        // PPS transfers network variance to the operator. Confirmed, orphaned and stale block
        // outcomes do not alter credits already committed for valid work. Statistical shares are
        // retained solely by PayoutManager's age-based PPS retention pass, independently of luck.
        return Task.CompletedTask;
    }
}
