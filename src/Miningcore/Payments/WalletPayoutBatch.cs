using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Miningcore.Persistence.Model;

namespace Miningcore.Payments;

/// <summary>Non-secret identity of the selected liability snapshot, not a payment receipt.</summary>
public sealed record WalletPayoutBatch(string PoolId, string Digest)
{
    public static WalletPayoutBatch Create(string poolId, Balance[] balances)
    {
        if(string.IsNullOrWhiteSpace(poolId) || poolId.Length > 200)
            throw new ArgumentException("Wallet payout pool identity must contain 1-200 characters", nameof(poolId));
        using var stream = new MemoryStream();
        using(var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("miningcore-wallet-payout-v1");
            writer.Write(poolId);
            writer.Write(balances.Length);
            foreach(var balance in balances.OrderBy(x => x.Address, StringComparer.Ordinal).ThenBy(x => x.Amount))
            {
                writer.Write(balance.Address);
                writer.Write(balance.Amount.ToString("G29", CultureInfo.InvariantCulture));
            }
        }
        return new(poolId, Convert.ToHexStringLower(SHA256.HashData(stream.ToArray())));
    }
}
