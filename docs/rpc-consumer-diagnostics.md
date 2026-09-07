# Credential-safe RPC consumer diagnostics

Daemon errors, REST bodies, parsing exceptions and wallet warnings are untrusted
text: a daemon or proxy can echo request credentials into them. Escaping that text
prevents line injection, but does not make it safe to disclose.

This change addresses [#154](https://github.com/NINJAK1DD/miningcore/issues/154),
complementing the [transport boundary](releases.md#unreleased-credential-safe-rpc-transport-diagnostics).

## Diagnostic contract

Affected messages use the prefix `RPC consumer diagnostic ` and compact JSON:

| Field | Meaning |
| --- | --- |
| `operation` | Finite, reviewed `Class.Method` label. Unknown labels become `other`. Pool-scoped loggers retain their pool identity; shared service records require surrounding operation context. |
| `failure` | Fixed structural exception category, or null for a daemon-result diagnostic. |
| `code` | Numeric daemon error code where available. Null means not captured, not success. |

No exception object, message, stack trace, URL, payload or response body enters this
projection. Global Json.NET settings cannot change it. Update monitoring filters
that matched the former free-form messages. Error severity alone does not establish
a conclusive payment failure: check the outcome and retained reconciliation evidence.

## Audited consumers

| Group | Reviewed output paths |
| --- | --- |
| Bitcoin, BLAKE2b, merged mining, Equihash, Handshake, Nexa, ProgPoW, Satoshicash | Template/statistics refresh, startup, subscriptions, block submission, direct settlement, replay and payouts. |
| Ethereum and inherited implementations | Work/stats refresh, WebSocket parsing, block submission and wallet failures. |
| CryptoNote, Conceal, Zano | Daemon/wallet failures, block alerts and transfers; CryptoNote transaction secret keys are no longer logged. |
| Alephium, Beam, Ergo, Kaspa, Warthog, Xelis | REST/gRPC/socket failures, typed error bodies, wallet warnings and block/payout alerts; Beam explorer announcements omit the URL. |
| Shared consumers | Pool worker and observable callbacks, payout retries/ownership, wallet relocking, recorder fallback, candidate/recovery failure reporting and notification delivery failures. |

The [source catalogue](../src/Miningcore/Rpc/RpcConsumerOperations.cs) defines the
diagnostic vocabulary, not permitted RPC operations. This is an output-only change:
original RPC errors, parsing exceptions and reconciliation objects are not rewritten.
Submission classification, wallet decisions, accounting, retries and fail-stop exit
codes retain their existing behavior.

Daemon startup and pool-run failures can also be printed by generic host logging.
These boundaries expose a safe `PoolStartupException` subtype and retain the original
failure in an internal JSON-ignored property, not `InnerException`. Cancellation
remains cancellation. Operators receive the pool and failure category rather than
potentially sensitive remote text. Earlier configuration validation retains its
named diagnostics.

The final payout-host boundary similarly retains the uncertain-payout exception
classification and private original evidence, while withholding the original cause
from generic host logging. It does not release a retained ownership lease or turn
an uncertain outcome into a conclusive failure.

## Alerts and reconciliation

Block-rejection and wallet-relock alerts retain the operation and required action
without repeating daemon explanations. Payment alerts preserve failed versus
uncertain outcomes, amounts, recipient counts and reconciliation groups, but omit
free-form `Error` and `Detail` text. Uncertain-payment alerts show only structurally
valid 32-byte hexadecimal transaction IDs (optionally `0x`-prefixed). Malformed IDs
are withheld, not repaired. Exact returned values remain in the original evidence.

Keep private evidence access-controlled. Compare ledger and wallet history before
releasing an uncertain payout lease or retrying a payment. Never import quarantine
files as recovery journals.

## Limits and historical exposure

This is **not a global redaction filter**. Normal metadata such as configured pool
names, recipient addresses, amounts, block identities and recovery file locations
remains visible. Success-path transaction notifications remain a separate contract.
The fix does not authenticate daemons or prevent them lying about chain state.

Original in-process errors, private recovery/fatal-state evidence and debugger
inspection remain sensitive. Configuration dumps are tracked in
[#144](https://github.com/NINJAK1DD/miningcore/issues/144). Share-relay transport,
generic process crash reports, unrelated API/configuration output and external
daemon logs are not claimed to be globally sanitized here.

Releases through v0.3.0 predate this fix. Restrict historical logs, alert mailboxes,
support exports and backups. Rotate exposed credentials using the daemon/wallet
procedure; an exposed private key requires secure wallet replacement, not simply
a password change. Do not publish historical logs or reconciliation error text.
The exclusion-first design follows [OWASP logging guidance](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html).

## Validation

Captured-output regressions send synthetic hostile data over real HTTP into
production Bitcoin and Ethereum consumers and over TCP into the Beam socket consumer.
They cover request/response confidentiality, malformed JSON/results and streamed
templates, block rejection, wallet relocking, startup reporting and payment rendering, while
checking unchanged original RPC data and financial outcomes. Existing transport,
payout, recovery, PostgreSQL and pinned-daemon tests provide complementary evidence;
they do not certify every coin family against its live daemon.
