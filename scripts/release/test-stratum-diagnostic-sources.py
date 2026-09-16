#!/usr/bin/env python3
"""Conservative architecture guard for reviewed direct Stratum logger calls.

This is not a C# parser or a taint analyzer. Each exact reviewed logger access must
match its listed occurrence count before removal. New calls require a source audit.
A narrow miner-identity scan also covers all Blockchain and Mining C# files, rather
than discovering scope from the wording that a regression could remove. Whitespace
is ignored for exact matching (including in literals). The call scanner handles
ordinary/verbatim strings and comments, not the complete C# grammar; aliases,
indirect sinks and new language constructs still require review and runtime tests.
"""

import re
import sys
from collections import Counter
from collections.abc import Sequence
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
        r'''logger.Info(() => $"[{connection.ConnectionId}] Accepting connection from {remoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging?.GPDRCompliant == true)}:{remoteEndpoint.Port} ...");''',
        r'''logger.Debug("Connection refused because local pool admission is closed");''',
        r'''logger.Fatal(
            "Timed out after {0} while draining {1} Stratum connection task(s). " +
            "Mining admission is closed and shutdown will continue so Share Recorder retains its recovery window.",
            ConnectionDrainTimeout, pending);''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Disconnecting banned client @ {connection.RemoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging?.GPDRCompliant == true)}");''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Banning client for sending junk");''',
        r'''logger.Info(() => $"[{connection.ConnectionId}] Banning client for failing SSL handshake");''',
        # Existing AuthenticationException and security-IOException branches each
        # contain this exact message. Both occurrences are explicitly reviewed.
        r'''logger.Info(() => $"[{connection.ConnectionId}] Banning client for failing SSL handshake");''',
        r'''logger.Debug(() => $"[{connection.ConnectionId}] {completion}");''',
        r'''logger.Debug(() => $"Disconnecting banned ip {remoteEndpoint.Address.CensorOrReturn(clusterConfig.Logging?.GPDRCompliant == true)}");''',
    ),
    "src/Miningcore/Stratum/StratumDiagnostics.cs": (
        r'''logger.IsEnabled(level)''',
        r'''logger.Log(level, "Stratum diagnostic " + record.ToString(Formatting.None));''',
    ),
}

IDENTITY_ROOTS = ("src/Miningcore/Blockchain", "src/Miningcore/Mining")
DIRECT_FACTORY = re.compile(r"\bLogManager\s*\.\s*GetCurrentClassLogger\s*\(\s*\)\s*\.\s*[A-Za-z_]")
CALL_START = re.compile(r"\blogger\s*\.\s*[A-Za-z_][A-Za-z_0-9]*\s*\(")
IDENTITY = re.compile(
    r"\bcontext\s*\??\.\s*(?:Miner|Worker|UserAgent)\b|"
    r"\b(?:workerValue|minerName|jobId|passParts)\b|"
    r"\bshare\s*\??\.\s*Miner\b|\brequest\s*\??\.\s*(?:Params|Id|Method)\b"
)
# Permit only the reviewed, closed-vocabulary projection, not a whole logger
# invocation containing it. Any adjacent raw field must still fail the scan.
# Qualified lookalikes and more complex arguments require explicit review.
SAFE_METHOD = re.compile(
    r"(?<![\w.@])StratumDiagnostics\s*\.\s*Method\s*\(\s*request\s*\??\.\s*Method\s*\)"
)
# Retain source offsets so identifier checks see interpolation contents, while
# parentheses in a quoted message do not truncate the enclosing logger call.
LITERALS_AND_COMMENTS = re.compile(
    r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\''
)


def without_safe_methods(arguments: str) -> str:
    def replace(match: re.Match[str]) -> str:
        # A whitespace/comment-separated qualifier must not turn a lookalike
        # helper into the trusted projection. Leave those fields for rejection.
        prefix = re.sub(r"//[^\n]*|/\*[\s\S]*?\*/", "", arguments[:match.start()]).rstrip()
        return match[0] if prefix.endswith((".", ":")) else ""

    return SAFE_METHOD.sub(replace, arguments)


def identity_accesses(source: str) -> list[str]:
    masked = LITERALS_AND_COMMENTS.sub(lambda match: " " * len(match[0]), source)
    findings = []
    if DIRECT_FACTORY.search(masked):
        findings.append("inline LogManager.GetCurrentClassLogger")
    for call in CALL_START.finditer(masked):
        depth = 1
        end = call.end()
        while end < len(masked) and depth:
            if masked[end] == "(":
                depth += 1
            elif masked[end] == ")":
                depth -= 1
            end += 1
        if depth:
            findings.append("unbalanced logger invocation requires review")
        elif IDENTITY.search(without_safe_methods(source[call.end():end])):
            findings.append("miner identity/request field in logger invocation")
    return findings


def unreviewed_accesses(source: str, approved: Sequence[str]) -> list[str]:
    remaining = re.sub(r"\s+", "", source)
    findings = []
    expected = Counter(re.sub(r"\s+", "", invocation) for invocation in approved)
    for needle, expected_count in expected.items():
        count = remaining.count(needle)
        if count != expected_count:
            findings.append(f"approved logger access occurs {count} times, expected {expected_count}")
        for _ in range(expected_count):
            remaining = remaining.replace(needle, "", 1)
    findings.extend(re.findall(r"\blogger\.[A-Za-z_][A-Za-z_0-9]*", remaining))
    if DIRECT_FACTORY.search(remaining):
        findings.append("inline LogManager.GetCurrentClassLogger")
    return findings


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
        'LogManager.GetCurrentClassLogger().Error(error, "safe-looking message");',
    ]
    for source in fixtures:
        if not unreviewed_accesses(source, []):
            raise AssertionError("Source guard failed its negative fixture")
    for approved in APPROVED.values():
        joined = "\n".join(approved)
        if unreviewed_accesses(joined, approved):
            raise AssertionError("Source guard failed its approved fixture")
        for invocation in approved:
            if not unreviewed_accesses(joined + invocation, approved):
                raise AssertionError("Source guard accepted a duplicated approved access")
            if not unreviewed_accesses(joined.replace(invocation, "", 1), approved):
                raise AssertionError("Source guard accepted a missing approved access")
        for source in fixtures:
            if not unreviewed_accesses(joined + source, approved):
                raise AssertionError("Approved calls hid an unreviewed invocation")

    identity_fixtures = [
        'logger.Info(() => $"Authorized {context.Miner}");',
        'logger.Debug(() => context.Worker);',
        'logger.Info("{0}", context?.UserAgent);',
        'logger.Warn(Format(workerValue, Nested(minerName)));',
        'logger.Info("literal ) (", share.Miner);',
        'logger.Info(@"literal "")""", request.Params);',
        'logger.\nWarn(\njobId\n);',
        'logger.Info("{0}", passParts);',
        'LogManager.GetCurrentClassLogger().Error(error);',
        'logger.Debug(() => $"request {request.Id}");',
        'logger.Warn(() => request.Method);',
        'logger.Info("{0}", request?.Id);',
        'logger.\nWarn(\nrequest ?. Method\n);',
        'logger.Info("{0}", Format(request.Id));',
        'logger.Warn(() => $"{StratumDiagnostics.Method(request.Method)} {request.Method}");',
        'logger.Warn(StratumDiagnostics.Method(request.Method), request.Id);',
        'logger.Warn(StratumDiagnostics.Method(request.Id));',
        'logger.Warn(StratumDiagnostics.Method(request.Method + request.Params));',
        'logger.Warn(Other.StratumDiagnostics.Method(request.Method));',
        'logger.Warn(Other. \n StratumDiagnostics.Method(request.Method));',
        'logger.Warn(Other./* qualifier */StratumDiagnostics.Method(request.Method));',
        'logger.Warn(Other::StratumDiagnostics.Method(request.Method));',
        'logger.Warn(FakeStratumDiagnostics.Method(request.Method));',
        'logger.Warn(@StratumDiagnostics.Method(request.Method));',
        'logger.Warn(() => $"StratumDiagnostics.Method({request.Method})");',
    ]
    for source in identity_fixtures:
        if not identity_accesses(source):
            raise AssertionError("Identity guard failed its negative fixture")
    for source in (
        'logger.Info(() => $"[{connection.ConnectionId}] Authorized worker (identity withheld)");',
        'logger.Info("safe"); Use(context.Miner);',
        '// logger.Info(context.Miner);\nlogger.Debug("safe");',
        'logger.Info("safe", /* ) */ connection.ConnectionId);',
        'private static readonly ILogger logger = LogManager.GetCurrentClassLogger();',
        'logger.Warn(() => $"Use of Ethash Stratum V1 method: {StratumDiagnostics.Method(request.Method)}");',
        'logger.Warn("{0}", StratumDiagnostics . Method ( request ?. Method ));',
        'logger.Warn(StratumDiagnostics.Method(request.Method)); Use(request.Id);',
    ):
        if identity_accesses(source):
            raise AssertionError("Identity guard failed its safe fixture")

    failed = False
    for relative, approved in APPROVED.items():
        findings = unreviewed_accesses((ROOT / relative).read_text(encoding="utf-8"), approved)
        if findings:
            failed = True
            print(f"{relative}: unreviewed direct logger access: {', '.join(findings)}", file=sys.stderr)
    for relative in IDENTITY_ROOTS:
        directory = ROOT / relative
        if not directory.is_dir():
            raise AssertionError(f"Missing guarded source directory: {relative}")
        for path in sorted(directory.rglob("*.cs")):
            findings = identity_accesses(path.read_text(encoding="utf-8"))
            if findings:
                failed = True
                print(f"{path.relative_to(ROOT)}: {', '.join(findings)}", file=sys.stderr)
    if failed:
        return 1
    print("Stratum direct-logger and miner-identity guards and negative fixtures passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
