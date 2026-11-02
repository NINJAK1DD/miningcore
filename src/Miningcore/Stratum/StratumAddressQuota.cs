using System.Net;
using System.Net.Sockets;

namespace Miningcore.Stratum;

// Resource-accounting identity only. Never use prefixes for trusted proxies,
// endpoint identity, authorization or bans, which retain exact client addresses.
internal static class StratumAddressQuota
{
    internal static IPAddress Key(IPAddress address, int ipv6PrefixLength)
    {
        if(ipv6PrefixLength is < 0 or > 128) throw new ArgumentOutOfRangeException(nameof(ipv6PrefixLength));
        address = StratumConnectionAdmission.Normalize(address);
        if(address.AddressFamily != AddressFamily.InterNetworkV6) return address;
        var bytes = address.GetAddressBytes();
        for(var bit = ipv6PrefixLength; bit < 128; bit++)
            bytes[bit / 8] &= (byte) ~(1 << (7 - bit % 8));
        return new IPAddress(bytes); // Scope IDs cannot create additional quota buckets.
    }
}
