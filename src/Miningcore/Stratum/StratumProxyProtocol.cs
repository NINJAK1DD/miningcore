using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Miningcore.Configuration;

namespace Miningcore.Stratum;

internal sealed class StratumAdmissionException : Exception;

// Built once per listener before accepting connections. No mutable configuration or
// IPAddress instance escapes into the lookup set; admission and header parsing share it.
internal sealed class StratumProxyPolicy
{
    private readonly FrozenSet<IPAddress> trusted;

    internal StratumProxyPolicy(TcpProxyProtocolConfig config)
    {
        Enabled = config?.Enable == true;
        Mandatory = Enabled && config.Mandatory;
        if(!Enabled)
        {
            trusted = FrozenSet<IPAddress>.Empty;
            return;
        }

        var literals = config.ProxyAddresses;
        if(literals == null || literals.Length == 0)
            literals = new[] { "127.0.0.1", "::1" };
        trusted = literals.Select(text =>
        {
            if(!IPAddress.TryParse(text, out var address))
                throw new InvalidOperationException("Enabled PROXY trust lists require valid literal IP addresses");
            return StratumConnectionAdmission.Normalize(address);
        }).ToFrozenSet();
    }

    internal bool Enabled { get; }
    internal bool Mandatory { get; }
    internal bool IsTrustedPeer(IPAddress peer) =>
        Enabled && trusted.Contains(StratumConnectionAdmission.Normalize(peer));
}

internal static class StratumProxyProtocol
{
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
