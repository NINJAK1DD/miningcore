using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Miningcore.Configuration;

internal static class ListenerAddressUtils
{
    internal readonly record struct IPv4InterfaceSubnet(IPAddress Address,
        IPAddress Mask);

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

    internal static bool IsSuitableForListener(IPAddress address,
        out string reason)
    {
        return IsSuitableForListener(address, GetActiveIPv4Subnets(),
            out reason);
    }

    internal static bool IsSuitableForListener(IPAddress address,
        IEnumerable<IPv4InterfaceSubnet> activeIPv4Subnets,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(activeIPv4Subnets);
        address = NormalizeListenerAddress(address);

        if(address.Equals(IPAddress.Broadcast))
        {
            reason = "IPv4 broadcast addresses cannot host a TCP listener";
            return false;
        }

        if(address.AddressFamily == AddressFamily.InterNetwork)
        {
            var firstOctet = address.GetAddressBytes()[0];
            if(firstOctet is >= 224 and <= 239)
            {
                reason = "IPv4 multicast addresses cannot host a TCP listener";
                return false;
            }

            if(activeIPv4Subnets.Any(subnet =>
                   IsDirectedBroadcast(address, subnet)))
            {
                reason = "IPv4 directed broadcast addresses cannot host a TCP listener";
                return false;
            }
        }
        else if(address.IsIPv6Multicast)
        {
            reason = "IPv6 multicast addresses cannot host a TCP listener";
            return false;
        }

        reason = null;
        return true;
    }

    private static IPv4InterfaceSubnet[] GetActiveIPv4Subnets()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface =>
                    networkInterface.OperationalStatus == OperationalStatus.Up)
                .SelectMany(networkInterface =>
                    networkInterface.GetIPProperties().UnicastAddresses)
                .Where(unicast =>
                    unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(unicast.Address) &&
                    unicast.IPv4Mask != null)
                .Select(unicast => new IPv4InterfaceSubnet(unicast.Address,
                    unicast.IPv4Mask))
                .ToArray();
        }
        catch(Exception ex) when(ex is NetworkInformationException or
            PlatformNotSupportedException)
        {
            // Host-specific Bind remains authoritative. Interface enumeration is used only for
            // positive rejection of a broadcast identity the operating system may still bind.
            return Array.Empty<IPv4InterfaceSubnet>();
        }
    }

    private static bool IsDirectedBroadcast(IPAddress candidate,
        IPv4InterfaceSubnet subnet)
    {
        if(candidate.AddressFamily != AddressFamily.InterNetwork ||
            subnet.Address?.AddressFamily != AddressFamily.InterNetwork ||
            subnet.Mask?.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var addressBytes = subnet.Address.GetAddressBytes();
        var maskBytes = subnet.Mask.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        var hostBitCount = 0;
        var sawHostBit = false;

        for(var index = 0; index < maskBytes.Length; index++)
        {
            for(var bit = 7; bit >= 0; bit--)
            {
                var networkBit = (maskBytes[index] & (1 << bit)) != 0;
                if(!networkBit)
                {
                    sawHostBit = true;
                    hostBitCount++;
                }
                else if(sawHostBit)
                    return false;
            }
        }

        // RFC 3021 /31 point-to-point networks and /32 host routes have no broadcast address.
        if(hostBitCount < 2)
            return false;

        for(var index = 0; index < addressBytes.Length; index++)
        {
            var broadcastOctet = (byte) (addressBytes[index] |
                ~maskBytes[index]);
            if(candidateBytes[index] != broadcastOctet)
                return false;
        }

        return true;
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
