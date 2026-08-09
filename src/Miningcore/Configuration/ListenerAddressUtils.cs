using System.Net;
using System.Net.Sockets;

namespace Miningcore.Configuration;

internal static class ListenerAddressUtils
{
    internal static bool TryResolve(string listenAddress,
        out IPAddress address)
    {
        if(listenAddress == null)
        {
            address = IPAddress.Loopback;
            return true;
        }

        if(listenAddress == "*")
        {
            address = IPAddress.Any;
            return true;
        }

        if(!IPAddress.TryParse(listenAddress, out address))
            return false;

        address = NormalizeListenerAddress(address);
        return true;
    }

    internal static bool Overlaps(IPAddress first, IPAddress second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        first = NormalizeListenerAddress(first);
        second = NormalizeListenerAddress(second);

        // Some kernels permit identical TCP listeners when SO_REUSEADDR is set,
        // but connection ownership is then ambiguous. Never allow two pools to
        // depend on that platform-specific dispatch behavior.
        if(first.Equals(second))
            return true;

        // Miningcore creates dual-mode sockets for IPv6Any, so it occupies both
        // IPv4 and IPv6 socket space on supported production hosts.
        if(first.Equals(IPAddress.IPv6Any) ||
            second.Equals(IPAddress.IPv6Any))
            return true;

        if(first.Equals(IPAddress.Any))
            return second.AddressFamily == AddressFamily.InterNetwork;

        if(second.Equals(IPAddress.Any))
            return first.AddressFamily == AddressFamily.InterNetwork;

        return false;
    }

    internal static string FormatHost(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    private static IPAddress NormalizeListenerAddress(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();

        // Linux ignores sin6_scope_id when binding non-scoped unicast addresses,
        // including loopback and global unicast. IPAddress.Equals does not, so
        // discard the ignored value before validating whether listeners overlap.
        // Preserve interface zones for link-local and multicast addresses where
        // the kernel uses them to select a distinct scope. Deprecated site-local
        // addresses are treated as ordinary unicast because RFC 3879 removed
        // portable zone semantics for fec0::/10.
        if(address.AddressFamily == AddressFamily.InterNetworkV6 &&
            address.ScopeId != 0 &&
            !address.IsIPv6LinkLocal &&
            !address.IsIPv6Multicast)
        {
            return new IPAddress(address.GetAddressBytes());
        }

        return address;
    }
}
