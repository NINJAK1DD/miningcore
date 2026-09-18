using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;

namespace Miningcore.Stratum;

internal sealed class StratumAdmissionException : Exception;

internal static class StratumProxyProtocol
{
    internal static bool IsTrustedPeer(TcpProxyProtocolConfig config, IPAddress peer)
    {
        if(config?.Enable != true) return false;
        peer = StratumConnectionAdmission.Normalize(peer);
        if(config.ProxyAddresses == null || config.ProxyAddresses.Length == 0)
            return peer.Equals(IPAddress.Loopback) || peer.Equals(IPAddress.IPv6Loopback);
        return config.ProxyAddresses.Any(x => IPAddress.TryParse(x, out var trusted) &&
            StratumConnectionAdmission.Normalize(trusted).Equals(peer));
    }

    internal static IPEndPoint Parse(string line, IPEndPoint peer)
    {
        // Includes CR but excludes LF, which the line dispatcher consumed. v1's wire maximum
        // is 107 bytes including CRLF. Never accept a family, address or port by partial parse.
        if(line.Length > 106 || !line.EndsWith('\r') || line.Any(c => c > 127))
            throw new InvalidDataException("Invalid PROXY v1 framing");
        var parts = line[..^1].Split(' ');
        if(parts.Length >= 2 && parts[1] == "UNKNOWN") return peer;
        if(parts.Length != 6 || parts[0] != "PROXY" ||
            parts[1] is not ("TCP4" or "TCP6"))
            throw new InvalidDataException("Invalid PROXY v1 fields");
        var family = parts[1] == "TCP4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        if(!TryAddress(parts[2], family, out var source) ||
            !TryAddress(parts[3], family, out _) ||
            !TryPort(parts[4], out var sourcePort) || !TryPort(parts[5], out _))
            throw new InvalidDataException("Invalid PROXY v1 endpoint");
        return new IPEndPoint(StratumConnectionAdmission.Normalize(source), sourcePort);
    }

    private static bool TryAddress(string text, AddressFamily family, out IPAddress address) =>
        IPAddress.TryParse(text, out address) && address.AddressFamily == family &&
        !text.Contains('%') && (family != AddressFamily.InterNetwork || address.ToString() == text);

    private static bool TryPort(string text, out ushort port) =>
        ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        (text.Length == 1 || text[0] != '0');
}
