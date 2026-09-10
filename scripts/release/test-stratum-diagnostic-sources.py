#!/usr/bin/env python3
"""Conservative architecture guard for reviewed direct Stratum logger calls.

This is not a C# parser or a taint analyzer. Exact reviewed invocations are removed
before checking for any remaining direct logger member access. New direct calls,
including exception/request/token overloads, require an explicit source audit.
Whitespace is ignored (including in literals); aliases and indirect sinks still
require the documented review and captured-output tests.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APPROVED = {
    "src/Miningcore/Stratum/StratumConnection.cs": (
        r'''logger.Info(() => $"[{ConnectionId}] {sslStream.SslProtocol.ToString().ToUpperInvariant()}-{sslStream.NegotiatedCipherSuite.ToString().ToUpperInvariant()} Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");''',
        r'''logger.Info(() => $"[{ConnectionId}] Connection from {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}:{RemoteEndpoint.Port} accepted on port {endpoint.IPEndPoint.Port}");''',
        r'''logger.Info(() => $"[{ConnectionId}] Connection closed");''',
        r'''logger.Info(() => $"Real-IP via Proxy-Protocol: {RemoteEndpoint.Address.CensorOrReturn(gpdrCompliantLogging)}");''',
    ),
    "src/Miningcore/Stratum/StratumServer.cs": (
        r'''logger.Info(() => $"Stratum ports {string.Join(", ", listeners.Select(x => $"{x.Endpoint.IPEndPoint.Address}:{x.Endpoint.IPEndPoint.Port}").ToArray())} online");''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Accepting connection from {remoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging.GPDRCompliant)}:{remoteEndpoint.Port} ...");''',
        r'''logger.Debug("Connection refused because local pool admission is closed");''',
        r'''logger.Fatal(
            "Timed out after {0} while draining {1} Stratum connection task(s). " +
            "Mining admission is closed and shutdown will continue so Share Recorder retains its recovery window.",
            ConnectionDrainTimeout, pending);''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Disconnecting banned client @ {connection.RemoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging?.GPDRCompliant == true)}");''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Banning client for sending junk");''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Banning client for failing SSL handshake");''',
        r'''logger.Debug(() => $"[{connection.ConnectionId}] {completion}");''',
        r'''logger.Debug(() => $"Disconnecting banned ip {remoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging?.GPDRCompliant == true)}");''',
    ),
}


def unreviewed_accesses(source: str, approved: list[str]) -> list[str]:
    remaining = re.sub(r"\s+", "", source)
    for invocation in approved:
        remaining = remaining.replace(re.sub(r"\s+", "", invocation), "")
    return re.findall(r"\blogger\.[A-Za-z_][A-Za-z_0-9]*", remaining)


def main() -> int:
    # Exercise the guard against representative accidental regressions, multiline
    # and exception-layout overloads included. Never load a live configuration.
    fixtures = [
        'logger.Error(error);',
        'logger.Error(error, "safe-looking message");',
        'logger.Debug(() => request);',
        'logger.Info("{0}", request.Params);',
        'logger.Trace(JToken.FromObject(request));',
        'logger.Log(new LogEventInfo { Exception = error });',
        'logger.\nWarn(\nrequest\n);',
    ]
    for source in fixtures:
        if not unreviewed_accesses(source, []):
            raise AssertionError("Source guard failed its negative fixture")
    for approved in APPROVED.values():
        joined = "\n".join(approved)
        if unreviewed_accesses(joined, approved):
            raise AssertionError("Source guard failed its approved fixture")
        for source in fixtures:
            if not unreviewed_accesses(joined + source, approved):
                raise AssertionError("Approved calls hid an unreviewed invocation")

    failed = False
    for relative, approved in APPROVED.items():
        findings = unreviewed_accesses((ROOT / relative).read_text(encoding="utf-8"), approved)
        if findings:
            failed = True
            print(f"{relative}: unreviewed direct logger access: {', '.join(findings)}", file=sys.stderr)
    if failed:
        return 1
    print("Stratum direct-logger architecture guard and negative fixtures passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
