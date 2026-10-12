using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Banning;

public class IntegratedBanManager : IBanManager
{
    private static readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions
    {
        ExpirationScanFrequency = TimeSpan.FromSeconds(10)
    });

    #region Implementation of IBanManager

    public bool IsBanned(IPAddress address)
    {
        address = Normalize(address);
        var result = cache.Get(address.ToString());
        return result != null;
    }

    public void Ban(IPAddress address, TimeSpan duration)
    {
        Contract.RequiresNonNull(address);
        Contract.Requires<ArgumentException>(duration.TotalMilliseconds > 0);
        address = Normalize(address);

        // don't ban loopback
        if(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback))
            return;

        cache.Set(address.ToString(), string.Empty, duration);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    #endregion
}
