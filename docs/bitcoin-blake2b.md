# Bitcoin BLAKE2b header-v2 mining

This is support for a **separate hard-fork chain**, not a new algorithm selectable on
SHA-256d Bitcoin. Use `coin: "bitcoin-blake2b"`; the existing `bitcoin` template and its
direct-coinbase SOLO policy are unchanged. Miningcore labels this chain `BTCB2B` to keep
pool/accounting identities distinct; that label is not a claim about an exchange ticker.

[BIP-110](https://github.com/bitcoin/bips/blob/master/bip-0110.mediawiki) describes the
Reduced Data Temporary Softfork. It is not the BLAKE2b proof-of-work specification. The
later hard fork is implemented by the pinned Knots sources listed below.

## Compatibility boundary

- A Miningcore build containing this feature is required; v0.3.0 does not contain it.
- The reviewed node is **Bitcoin Knots v29.4.1.knots20260508**, commit
  `8c85b1585dac23f964e2dd32045624de7f02aa58`. Startup requires its version and Knots identifier,
  an active deployment with the expected activation height, and mandatory GBT rule `!blake2b`.
  Version strings are compatibility checks, not proof of binary authenticity: independently
  verify the upstream release checksums and signatures.
  Runtime work re-attests version, chain and deployment on the first successful template
  poll after a 30-second cache expires, and before new work after a GBT/activation-parent RPC outage.
  Failed attestation RPCs withhold fresh work and retry with bounded exponential backoff
  (1–30 seconds), restarting all identity checks after the delay. Only a complete successful
  attestation resets the backoff. Successful identity or deployment mismatches
  fault only the affected BLAKE2b pool and close its mining admission. Cached attestation bounds
  detection latency; it does not authenticate binaries or eliminate an endpoint replacement
  between RPC calls. Stop Miningcore before changing its daemon binary or chain configuration.
  There is no operator version-pin bypass. Urgent security-update compatibility needs a
  reviewed source/build update, not merely an acknowledged version string; the safe upgrade
  policy is tracked in [#151](https://github.com/NINJAK1DD/miningcore/issues/151).
  Attestation shares the serialized polling loop, not a background timer: a cache-expiring
  update can pay three additional sequential RPCs. Ordinary template polling refreshes the
  cache even without a new block. This deliberate latency/safety trade-off avoids concurrent
  attestation-state races; it does not promise zero added new-tip latency.
- Mainnet first uses header-v2 at height **961640**. Its activation coinbase headline is
  `8-30 NYPost Deride And Conquer` and the one-time target shift is 22. Miningcore validates
  that first target against the parent and the mainnet proof-of-work limit; it does not
  apply another shift to ordinary shares or subsequent daemon targets.
  The parent-only comparison is restricted to mainnet carry-forward heights. At a retarget
  boundary or on min-difficulty regtest, the daemon owns difficulty selection; Miningcore
  still validates GBT target/bits and rejects malformed parent metadata. See the pinned
  [Knots difficulty selection](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/pow.cpp#L32-L89).
- Only mainnet and isolated regtest are configured. Testnet4 and signet are not advertised.
  The regtest fixture uses activation 20 and shift 20; its headline is
  `Miningcore BLAKE2b regtest`. These are a test contract, not mainnet settings.
  Regtest activation height/headline may be explicitly matched to a custom test node;
  its target shift remains 20 in the pinned daemon's `consensus/params.h` default.
  Height/headline overrides do not provide a target-shift override.
- The node release is stable. The project's compatible DATUM gateway/miner ecosystem is
  still described as public beta. A stable node does not prove compatibility with every
  ASIC, firmware, proxy, rental service or public network deployment.

## Node and wallet isolation

Use a separate daemon data directory, wallet, RPC credentials, pool ID and ledger attribution.
Do not repoint your SHA-256d Bitcoin pool or use a copied production wallet as a shortcut.
The chains share historical addresses and transactions; assess replay and wallet risks
independently before funding reserves or making payments. Miningcore does not add replay
protection to transactions created by a daemon wallet.

Run the verified node in that dedicated directory. For a same-host deployment alongside
ordinary Bitcoin, the example deliberately uses non-default ports. Confirm these are unused
by every local daemon, testnet and service first:

```ini
server=1
rpcbind=127.0.0.1
rpcallowip=127.0.0.1
rpcport=18332
rpcuser=CHANGE_ME_BLAKE2B_RPC_USER
rpcpassword=CHANGE_ME_BLAKE2B_RPC_PASSWORD
port=18333
bind=0.0.0.0:18333
bind=127.0.0.1:18334=onion
```

This remains **mainnet**: do not add `testnet`, `testnet4`, `signet` or `regtest` to a
mainnet deployment. Use the dedicated `-datadir` with every `bitcoin-cli` command too.
Let the daemon synchronize, load its dedicated payout wallet and inspect:

```console
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getnetworkinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getdeploymentinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getblocktemplate '{"rules":["segwit","blake2b"]}'
```

`getdeploymentinfo.blake2b` must have the expected height and `active: true`; the template
must advertise `!blake2b`. Stable Knots does **not** promise a `coinbaseaux.blake2b_headline`
field. Miningcore uses reviewed typed activation metadata, not an assumed optional GBT field.
The pool wallet address must belong to the loaded wallet if the pool is to pay rewards.
Use the daemon endpoint's `httpPath` to select a wallet when more than one is loaded.

## Configure Miningcore

Start from [bitcoin_blake2b_pool.json](../examples/bitcoin_blake2b_pool.json). Keep this
configuration outside the checkout, replace every `CHANGE_ME` value, and follow the
[operator preflight](operations.md#before-accepting-miners). Review the RPC ports, pool-wallet
and fee addresses, PostgreSQL credentials, logging and recovery paths before opening Stratum.

The sample uses **custodial SOLO**: the chain wallet receives the block reward, then
Miningcore pays the winning miner after maturity, minus reviewed fees. It does not use
canonical Bitcoin's default direct-coinbase SOLO mode. Do not add `soloCoinbasePayout` or
`bip54Coinbase`, even as false: those settings belong to a different reviewed runtime.

SOLO, PROP and PPLNS use their existing payout/accounting paths. PPS uses immutable assigned
difficulty evidence and the existing transactional credit ledger; follow the complete
[PPS reserve, schema and recovery checklist](pps.md) before selecting it. Keep both pool
and cluster payment processing enabled for PPS. No new database schema is introduced.
Relay receivers must understand the new family and have the same template/chain contract;
do not introduce a new chain into a mixed-version accounting deployment.

Keep the fee entry at zero until its address and intended percentage are reviewed.
Do not infer current profitability, market value or reserve adequacy from hash-rate telemetry.
Startup reserves the activation-headline scriptSig budget even if the current job does not
need the headline. With the shipped network contracts, `paymentProcessing.coinbaseString`
allows **24 UTF-8 bytes after trimming** (24 ASCII characters, fewer for multibyte text).
Oversized markers are rejected before daemon access. Pinned Knots emits an empty
`coinbaseaux` object; nonempty `coinbaseaux.flags` are refused explicitly before serialization,
not silently incorporated into the startup budget or reported only as a later length overflow.

## Miner protocol and difficulty

Connect compatible BLAKE2b/Sia-style miners directly to Miningcore, using a valid address
on this chain as the username (`ADDRESS.worker`). A DATUM gateway is not required between
the miner and Miningcore; the separate DATUM pooled-mining protocol is not implemented.
SHA-256d hardware, including SHA-256 Bitaxe devices, cannot mine this chain.

The [upstream miner guide](https://btc-blake2b.org/miners) lists Antminer A3 and Sia-style
Goldshell devices. That is upstream compatibility information, **not** a Miningcore firmware
certification. No physical BLAKE2b ASIC/firmware is claimed tested by this implementation.
Commission each miner/proxy on an isolated endpoint before sending production hash power.

Physical GPU validation used an RTX 3080 Ti with the unmodified OpenCL kernel from
[PyBLOCK miner revision 618ec513](https://github.com/GaltRanch/pyblock-miner/tree/618ec5130feca063ecd4d0ae634633d3d3ebc644)
and a separate, bounded Stratum adapter that reads Miningcore's exact compact share target.
Across the four payout schemes, the full Miningcore process recorded 98 accepted GPU-generated
shares and 98 distinct daemon-accepted blocks in an isolated PostgreSQL/regtest deployment.
Every accepted share also met the deliberately easy regtest network target; these are separate
share and block counts, not an estimate of mainnet block-finding performance.
This included PPS credits, duplicate/malformed rejection, reconnects, graceful restarts,
changing VarDiff targets, and daemon outage/recovery. GPU results were independently
verified with Python's BLAKE2b before submission, then checked against the daemon's active chain.
This validates the GPU kernel and pool path, **not** PyBLOCK's complete Rust client, ASIC
firmware, production payout economics, or a sustained mainnet soak. In particular, do not
assume a miner that rounds numerical difficulty can safely ignore the compact target in notify.

The production wire contract is Sia-style **profile 0**, with hasher time rolling disabled:

- Subscribe returns a four-byte connection extranonce and an eight-byte extranonce2 size.
- Notify contains the hidden previous hash, a 39-byte commitment in `coinb1`, empty `coinb2`,
  an empty merkle list, an eight-digit compact **share target**, and 16-digit miner time.
- Submit requires exactly five JSON strings: worker, job ID, extranonce2, time and nonce.
  Extranonce2, time and nonce must each contain exactly 16 hexadecimal characters.
  Extra version bits and shortened legacy fields are rejected, not padded or coerced.
- Version rolling is disabled; miners cannot change consensus-owned header fields. The
  miner-time bytes are nonce space for this fixed-time profile, not permission to change
  the committed consensus timestamp.
- Difficulty uses Bitcoin's `0x1d00ffff` reference target and multiplier 1, as in the
  reviewed gateway accounting contract. This is distinct from a miner display's SI units.
  Each assigned target is converted with exact integer arithmetic and truncated to the
  compact value actually sent on the wire. A valid network candidate is never discarded
  solely because its assigned share target is harder than the network target.
- Each job keeps its assigned difficulty snapshot across VarDiff changes. Changed targets
  require fresh notify data as well as `mining.set_difficulty`.
  Successful BIP310 `minimum-difficulty` changes announce difficulty before the new job too.
- Mainnet endpoint difficulty and VarDiff minimum/maximum must be at least **1**. This is
  a conservative miner-compatibility floor from the reviewed CONVOY high-32-bit admission
  boundary, not a Knots consensus rule. Sub-floor targets are allowed only on isolated
  regtest for software proof generation. Wider physical hardware ranges need commissioning
  before this production boundary can be relaxed; firmware doing harder work than the
  assigned target can otherwise be under-credited, particularly under PPS.
- Connection suffixes never wrap within a running allocator. A random 128-bit coinbase
  discriminator separates job commitments across processes and restarts. Duplicate work
  remains duplicate even if hexadecimal casing or the assigned target changes.

All four ASIC layouts and nonzero XOR-mask variants are covered by official Knots vector
tests (`HeaderV2_MatchesStableKnotsVectors`), but this
does not advertise selectable wire profiles 1–3 or anti-withholding service. Production
uses a zero XOR key. There is no user-supplied header-flags or profile override.

### Software commissioning adapter contract

The private lab adapter is not a supported public miner distribution. An operator building
an adapter around the PyBLOCK kernel can use the wire contract above and the public
[regtest wire fixture](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bWireSession.cs)
and [independent proof reconstruction](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bRegtestTests.cs)
as executable references:

- Preserve the connection extranonce, exact issued job ID and compact target for each job;
  do not substitute a target rounded from the numeric difficulty announcement.
- For profile 0, construct the 52-byte first-stage input as `0x00 || coinb1 || extranonce1 || extranonce2`.
  Hash it with BLAKE2b-256, then hash `hidden_previous || nonce || miner_time || first_stage_digest`
  with BLAKE2b-256. All concatenations are decoded bytes, not ASCII hex.
- Compare the final digest as a big-endian integer against the decoded compact target.
  Preserve the eight-byte nonce/time fields and submit the exact five-string wire request.
- Honor `clean_jobs`, target changes and reconnects; stop submitting invalidated work and
  bound pending jobs, GPU batches and outstanding requests. Repeated miner-requested
  difficulty changes are not a commissioning stress-test substitute; a dedicated request
  budget is tracked in [#152](https://github.com/NINJAK1DD/miningcore/issues/152).

First validate against the pinned isolated regtest node. These references do not certify
third-party miner firmware or provide a production-ready adapter.

## Miner-requested difficulty budget

Each BLAKE2b TCP connection shares one budget between `mining.suggest_difficulty`
requests, `mining.configure` requests whose extension list includes `minimum-difficulty`,
and `mining.authorize` requests with a parseable static-difficulty password control (`d=`).
Malformed subscribe/configure/authorize requests with an ID also consume this allowance.
It starts with **eight requests**, replenishes **one request per ten elapsed seconds**,
and stores at most eight requests. Fractional refill time is retained. The allowance
accommodates configure, static authorization, suggest, renegotiation and startup retries;
sustained miner-driven retargeting is limited to six requests per minute after that burst.
There is no configuration switch to disable this admission boundary.

Subscribe accepts omitted/null parameters as an empty argument list, or an array of
scalar/null values. Its user agent is parsed once; the exact trimmed value used for NiceHash detection is carried
into subscription commit. Other non-array parameters and nested objects/arrays are rejected
before lookup, extranonce changes or work creation. A valid first subscribe remains free
when earlier malformed subscription attempts have exhausted the allowance.
Configure requires a string extension array and parameter object; minimum-difficulty
accepts a finite, positive JSON number or numeric string for firmware compatibility.
Numbers and strings are parsed once with invariant culture, and that exact value reaches
the assignment handler. Authorization preserves scalar-to-string conversion for worker
names and optional passwords, including numeric worker names, null passwords and
ISO date-shaped strings that Json.NET materializes as date tokens; objects
and arrays in those consumed positions are rejected. Configure and authorize ignore
trailing fields after their consumed parameters. Invalid subscribe/configure/authorize
requests consume a token and receive Stratum error 20 without assignment or identity
mutation or authorization RPC. Once exhausted, their
responses follow the same bounded refusal/disconnect policy. Missing IDs receive error -1
without charging.

The missing/null-ID exemption is a deliberate compatibility policy: it preserves the
existing error-only handling for clients with incomplete request IDs without spending
or resetting their negotiation state. These requests stop before parameter validation,
address/NiceHash lookups, difficulty mutation or job creation. The allowance bounds
assignment negotiation, not all request/error traffic: repeated missing-ID requests can
continue receiving errors. Serial dispatch and the bounded send queue limit queued work,
but do not rate-limit a client that keeps draining replies. This Stratum behavior is
distinct from [JSON-RPC 2.0 notification handling](https://www.jsonrpc.org/specification#notification),
which requires no response when the ID member is absent.

Admission precedes inherited acknowledgments, VarDiff/difficulty changes and work issuance.
Static-difficulty authorization is parsed once with the inherited parser and passed to the
authorization handler, including semicolon-separated and legacy embedded `d=` syntax,
before any authorization RPC or identity change. Duplicate, below-base and malformed
suggestions with request IDs still consume admission. Parseable static-difficulty
authorizations count even when the requested value would leave difficulty unchanged.
A configure message consumes one
request even when it repeats the minimum-difficulty extension name. Calls made before
subscription consume the same connection budget; subscribing does not reset it.

An admitted configure/suggest/static-authorization value that would change the assignment
must also produce a representable BLAKE2b share target. Even finite positive values such
as `1e100` can exceed that range. Such requests receive one Stratum error 20 before
acknowledgment, mutation or authorization RPC, preserving identity, difficulty, VarDiff,
pending updates and existing jobs. They consume their normal admission token and leave
the healthy connection usable. BLAKE2b suggestions and configure minimum-difficulty use
invariant numeric conversion: `"1.5"` means 1.5 on every server locale. Send a JSON number
or a numeric string with a dot decimal separator and optional exponent; grouping separators
and decimal commas are not accepted. Malformed suggestions retain their compatibility
success acknowledgement with no assignment change, while still consuming admission.
This changes legacy locale-dependent suggestion strings; canonical Bitcoin retains its
existing parser. The exact parsed value is reused for execution.

Server-driven BLAKE2b VarDiff has an effective maximum of `65535 * 2^208`, the highest
representable difficulty (target 1), when `maxDiff` is omitted. A configured lower maximum
is honored. This runtime ceiling applies to both share-triggered and idle retargeting
without rewriting the operator's configuration. A genuine zero-length interval window
uses the Unix-millisecond timestamp resolution: a conservative mean of
**0.001 / min(interval count, 10) seconds**, including the current
interval, then applies normal proportional retargeting, `maxDelta` and difficulty bounds.
A full window uses 0.0001 seconds; two intervals use 0.0005 seconds. For example, difficulty 10
with a ten-second target and a full zero window becomes 1,000,000 without a delta limit, or 12
with `maxDelta: 2`; two zero intervals produce 200,000 without a delta limit. It does not
automatically jump to the protocol maximum. For target intervals at or below the estimate,
an unresolved zero window holds the current difficulty, subject to configured bounds,
instead of lowering it. Positive measured intervals retain their proportional calculation.
Extreme positive ratios avoid
intermediate overflow/underflow, and delta limiting uses the previous difficulty plus
or minus the limit, avoiding cancellation. The shared VarDiff arithmetic fixes also apply
to other pool families, which retain their existing effective maximum.

The BLAKE2b assignment gate covers the entire share/idle VarDiff operation: checking whether
VarDiff is enabled, calculating the retarget and publishing its assignment. A concurrent
fixed-difficulty configure or static authorization therefore either follows a completely
published retarget or disables VarDiff before it can calculate. No stale result can replace
the fixed assignment. Shared manager entry points also safely ignore a disabled context.
Every VarDiff check following an accepted share acquires this per-connection gate, even
when no retarget is due. This adds synchronization with that connection's own assignment
producers; it keeps the enabled-state check and calculation consistent with fixed-difficulty
changes. Share validation and accounting remain outside the gate.

VarDiff also owns its failure boundary while holding the gate. If work construction or
queueing fails, it closes admission, clears jobs and disconnects before another producer can
publish. This applies to periodic idle updates as well as accepted-share updates, so a
background error logger cannot leave a live hidden difficulty. Restored work or buffered
requests cannot resume that session. Host cancellation before the operation leaves it
untouched; cancellation during the operation invalidates the session without classifying
ordinary host shutdown as a publication failure. Accepted proofs remain valid.

Subscribe, configure, suggest and static authorization use the same terminal policy
for failed responses or assignment publication, including queue exhaustion, cancellation
and unexpected exceptions. Cleanup happens while the assignment gate is owned. Suggestions
propagate notification failures so a recovered queue cannot accept an unmatched job.
Pre-response protocol errors that leave assignment state untouched remain recoverable;
authorization RPC and subscription autodiff lookups remain outside the gate.
Once authorization has responded successfully, cancellation before acquiring the static
assignment gate or before its first mutation is terminal: the miner has already received
success for an unfinished assignment. Host shutdown clears jobs and closes the session
without a publication-failure diagnostic or metric. Cancellation before the request's
early validation gate remains a no-op on assignment state.

Both share and idle updates read the wall clock while holding the VarDiff state lock.
A no-op idle sweep leaves the share timestamp, interval buffer and assignment markers
unchanged, so a real share in the same millisecond still measures from the previous share or
actual retarget. An idle update advances the baseline only when difficulty really changes.
A backward timestamp, future retarget timestamp or invalid interval history resets the
measurement window and timing baseline without changing difficulty, jobs or the last
actual assignment marker. Later valid samples resume normal retargeting. Negative elapsed
time is never treated as a fast-miner observation. Invalid/non-finite arithmetic inputs
produce no retarget. These shared changes are tracked in
[#184](https://github.com/NINJAK1DD/miningcore/issues/184); interval measurement still uses
the wall clock, with explicit rollback recovery rather than a new monotonic timer.
Forward clock steps can still resemble an idle interval and lower difficulty within
configured bounds; [#185](https://github.com/NINJAK1DD/miningcore/issues/185) tracks a
cross-family migration to monotonic elapsed time. Shared startup validation rejects non-finite or non-positive minimum
and configured maximum difficulties; omitting `maxDiff` remains supported.

**Before upgrading:** set an explicit finite, positive `minDiff` appropriate for the coin
and endpoint in every `varDiff` block, including other pool families. An omitted `minDiff`
defaults to zero and now prevents startup. A previously working configuration may therefore
fail on its next restart unless corrected. Configured `maxDiff` must also be finite, positive
and at least `minDiff`; it may still be omitted. `targetTime` and `retargetTime` must
be finite and greater than zero. A specified `maxDelta` must be finite and nonnegative;
zero or omission disables delta limiting. Correct nonfinite or negative values before
upgrading; these startup checks apply across all pool families.

When exhausted:

- `mining.suggest_difficulty` and static-difficulty `mining.authorize` return an error with Stratum code `20`,
  `result: false`, and the original request ID.
- `mining.configure` returns an error string under `result.minimum-difficulty`:
  `Difficulty request rate limit exceeded; retry after 10 seconds`. Other requested
  extensions are explicitly unsupported (`false`) for this BLAKE2b protocol. The loader
  and pool configuration enforce disabled version rolling, including custom templates.
  Clients must check for literal boolean `true`: an error string is a refusal even in
  languages where non-empty strings are truthy. It does not change the assigned target.
  The retry interval in the wire text is derived from the refill policy. As with
  successful configure responses, the top-level `error` property is omitted for
  ordinary clients; NiceHash/ASICBoost compatibility responses include `error: null`.
- Refused requests leave the assignment, pending VarDiff update and active jobs intact.
  A refused authorization also preserves existing authorization and miner identity;
  an unauthenticated connection stays unauthenticated. The authorization RPC is not run.
  They send no difficulty or job notification. A successful change still sends
  `mining.set_difficulty` before the matching immutable `mining.notify` job.
- The eighth consecutive over-budget request closes that connection without queuing
  another response. A newly admitted difficulty request resets this refusal count;
  unrelated messages do not. Closing is abortive, so buffered replies are not guaranteed
  to reach a miner that continues flooding. A miner should wait at least ten seconds
  after a refusal before retrying.

The **first valid subscribe is free**, even after difficulty allowance is exhausted. The first
duplicate `mining.subscribe` receives Stratum error 20 with `result: false`, retaining the
working connection. Another duplicate closes it without queuing another response. Neither
duplicate rotates extranonce, changes work or emits difficulty/job notifications.
Missing or null request IDs take precedence: they receive error -1 without consuming
the duplicate warning or difficulty allowance, even after subscription or a warning. The
one-warning allowance is per connection and is not reset by other requests or refill.
This protocol does not support in-session resubscription. Buffered requests cannot reopen
a terminal connection. Canonical Bitcoin's existing resubscription behavior is separately
tracked in [#181](https://github.com/NINJAK1DD/miningcore/issues/181).

A per-connection async gate covers assignment mutation, pending VarDiff application,
`mining.set_difficulty` and the immutable job/target snapshot plus `mining.notify`.
Miner changes, broadcasts and immediate VarDiff updates use the same gate, so one producer
cannot snapshot a new target under another producer's previous difficulty announcement.
Authorization address-validation RPC and share submission/accounting run outside the gate;
only their assignment changes acquire it. Subscribe resolves NiceHash autodiff outside the
gate, then commits subscription/extranonce state and publishes the initial assignment while
holding it. A cold HTTP lookup cannot delay the broadcast pipeline even when authorization
precedes subscription. A completed lookup returning no difficulty is not repeated. Gates
are independent between connections; each successful acquisition releases that exact
semaphore in `finally`, and cancellation before acquisition does not release it.

A protocol error before any response starts can return one error and leave the connection
usable. If an inherited handler has already acknowledged subscribe, configure, suggest,
static authorization or a share submission and subsequent work publication fails, the connection is terminal:
no second response is sent under that request ID, and buffered requests cannot resume the
session. This is a fail-closed boundary, not rollback of a published difficulty. A miner
may receive an acknowledgement or difficulty prefix before the abortive close; it must
reconnect and obtain a fresh complete assignment. A faulted pool also closes the connection.
The policy preserves [JSON-RPC response correlation](https://www.jsonrpc.org/specification#response_object)
without changing inherited success sequencing. Successful assignments still announce the
difficulty before their matching immutable notify; unavailable work must never leave a
live connection with a partial assignment. The same terminal latch covers unexpected
post-acknowledgment unrepresentable targets, including external autodiff values, and clears
active jobs. An accepted share remains accepted if its subsequent VarDiff publication
fails: accounting is preserved, with no extra invalid-share count or ban consideration.
Canonical Bitcoin's response policy is unchanged and tracked in
[#183](https://github.com/NINJAK1DD/miningcore/issues/183). The shared transport tracks
response attempts with one interlocked increment per response; notification writes do
not change that counter. All response payloads must use `RespondAsync`.

This assignment-ordering fix is tracked separately in
[#182](https://github.com/NINJAK1DD/miningcore/issues/182).
BLAKE2b supports custodial SOLO and rejects canonical `soloCoinbasePayout` options at
startup, so Bitcoin's direct-coinbase SOLO authorization-notify path is unreachable here.

Well-formed ordinary authorize without a parseable static-difficulty request, share
submission, well-formed configure without minimum-difficulty, pool
job broadcasts and server-driven VarDiff do not consume this budget. Connections have
independent allowances, including connections behind one proxy/IP. State is tied to
the connection with weak keys and has no per-IP history, timer or deferred work queue.
Elapsed time uses `TimeProvider.System.GetTimestamp`, independent of wall-clock/NTP
adjustments and miner timestamps. This bounds these three difficulty-request paths per
connection and rejects repeat subscription; it is not a global connection or general
Stratum denial-of-service limit. Fresh connections receive fresh allowances. Cross-connection
churn defenses, including shared-proxy/NAT and trusted client-address policy, are tracked in
[issue #180](https://github.com/NINJAK1DD/miningcore/issues/180).

Enforcement emits one Info-level structured `DifficultyBudgetDisconnect`,
`DuplicateSubscription` or `AssignmentPublicationFailure` event per closed connection,
with the server-generated connection ID and no request/password/address payload. Ordinary refusals produce no dedicated logs.
`miningcore_stratum_admission_total{pool,outcome}` counts `difficulty-refused`,
`difficulty-disconnect`, `duplicate-subscribe` and `publication-failure`; outcomes are allowlisted and there are
no per-miner, connection-ID or IP labels. Use these counters to distinguish renegotiation
refusals from duplicate-subscription and work-publication disconnects. Each terminal
publication failure is counted once, including failures following an accepted share.

### Research and wire validation for issue #152

The refusal format follows [BIP310's extension result specification](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki),
which permits an error string and repeated minimum-difficulty negotiation. The timer
uses [.NET's high-frequency timestamp API](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.gettimestamp?view=net-10.0).
This policy does not alter header serialization, target conversion or credited-share
difficulty from the consensus implementation.

Baseline measured on `d5864b38a2084e7909ffcadd55d097a4810d5b2f`, using newline-delimited
loopback TCP through `StratumConnection`, `StratumServer` and the real BLAKE2b pool:

| Path | Alternating requests | Fresh worker jobs | Responses / set-difficulty / notify | Elapsed |
| --- | ---: | ---: | --- | ---: |
| `suggest_difficulty` | 20 | 20 | 20 / 20 / 20 | 10.79 ms |
| configure minimum-difficulty | 20 | 20 | 20 / 20 / 20 | 20.43 ms |

These are functional loopback measurements, not a production throughput benchmark.
The dispatcher awaits each request serially, limits buffered inbound data to 32 KiB,
bounds the send queue at 16 items, and applies a five-second send timeout. Those bounds
do not limit request frequency when a miner promptly reads replies. The baseline
above crossed no existing dispatcher limit despite issuing twenty immutable snapshots.

The [TCP regression suite](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bDifficultyBudgetTests.cs)
now admits eight requests, refuses seven and disconnects on the next over-budget request
with a frozen monotonic clock. It covers each method, mixed methods, duplicate values
and extension names, pipelined bursts, pre-subscription exhaustion, two connections on
one pool, backward/forward wall-clock changes, exact refill boundaries, capacity after
long idle, pending VarDiff preservation, server-driven updates and original proof credit.
Additional cases cover increasing static-difficulty authorizations, their legacy password
syntax, refusals before initial authentication and reauthorization, ordinary authorization
while exhausted, the ordinary/NiceHash/ASICBoost configure response matrix, and seven
refusals followed by a refill/admission and another complete seven-refusal allowance.
Further wire cases cover a single duplicate-subscribe warning followed by twenty queued
repeats with no extra job, notification or extranonce change, including first-subscribe
success after exhaustion; bounded malformed configure/authorize traffic; numeric strings;
missing request IDs; terminal log/metric cardinality; and unchanged canonical Bitcoin
dispatch/authorization behavior. A custom template cannot enable version rolling.
[Deterministic contention cases](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bAdmissionHardeningTests.cs)
pause configure after mutation and broadcasts after a pending VarDiff announcement. They
wait for a competing operation to reach the held gate before releasing the barrier, then
check every wire target against the latest difficulty announcement. Additional coverage
proves authorization RPC and delayed NiceHash autodiff do not hold this gate. It checks
both present and absent autodiff results, no partial subscription/extranonce mutation,
scalar worker/trailing-field compatibility with unchanged admission accounting,
configure protocol-error recovery, exact malformed-error versus rate-refusal messages,
malformed subscribe floods, free first-subscribe recovery, and identical prepared/committed
user-agent values. Missing-ID subscribe does not perform lookup or charge admission.
Real job-unavailability cases cover the four negotiation publication paths and prove at most
one response per request followed by terminal closure, including requests buffered behind
the failed one. Separate successful-publication tests require one response followed by the
matching difficulty and immutable job. Pre-acknowledgement errors remain recoverable.
Date-shaped subscribe/authorization values are checked under French and Turkish cultures;
omitted/null subscribe is tested against both the canonical and BLAKE2b dispatchers.
A canonical Bitcoin request missing `minimum-difficulty.value` now declines the extension
without dropping the connection. Startup tests explicitly reject direct-coinbase SOLO
options whether their `enabled` property is true or false.
The fixture drives the production server dispatch wrapper, not a direct handler call.
The [live Knots suite](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bRegtestTests.cs)
also exhausts the shared budget before VarDiff and real accepted header-v2 submissions
for SOLO, PPS, PROP and PPLNS, explicitly checking exhaustion immediately before the
wire proof loop and after accepted submissions. A further live case removes current work
during VarDiff publication after a real accepted proof across all four payout schemes.
It verifies terminal closure, one response attempt, rejection of buffered follow-on
requests, unchanged invalid-share counts and exactly-once PostgreSQL settlement.
[Representability tests](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bRepresentabilityTests.cs)
cover finite out-of-range requests, remaining token capacity, unchanged worker state,
external/future post-acknowledgment failure and invariant suggestion parsing across six locales.
The target unit tests pin the adjacent floating-point values at the highest accepted
difficulty. Additional live-proof cases exercise an omitted VarDiff maximum and ten zero
intervals, both with and without `maxDelta`: accepted accounting survives, the connection
remains usable, and the next difficulty/notify pair has an exactly representable target.
Wire tests also cover idle retargeting, explicit lower maxima, retained jobs, unchanged
configuration and untouched negotiation allowance. Shared VarDiff unit tests cover ordinary
retargeting, extreme ratios and generic-family default bounds. Backward-clock tests cover
both producers and protocol bounds, preserved assignments, discarded invalid samples and
subsequent recovery. A lock-checking clock guards against reading time before the monitor.
Run the focused suite with:

```sh
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release --filter FullyQualifiedName~BitcoinBlake2bDifficultyBudgetTests
```

Set `MININGCORE_TEST_BLAKE2B_BITCOIND` to the verified, pinned Knots executable to run
the daemon cases with `--filter FullyQualifiedName~BitcoinBlake2b`. The separate
PostgreSQL ledger test additionally requires `MININGCORE_TEST_POSTGRES`.

## Troubleshooting and validation limits

- **Miners report difficulty rate-limit errors or disconnect after repeated requests:**
  check `DifficultyBudgetDisconnect` and the admission counter, then inspect the miner/proxy's frequency of `suggest_difficulty`, configure minimum-difficulty
  and authorization with `d=`. They share the eight-request burst and ten-second refill.
  Wait at least the retry interval before sending another difficulty request; continuing
  to retry closes the connection on the eighth consecutive refusal. Initial static-difficulty
  authorization can be refused if earlier negotiation already exhausted the allowance.
  Retry after refill if the connection is retained; firmware may instead treat an authorize
  error as fatal and reconnect, receiving a fresh budget (the residual path tracked in
  [#180](https://github.com/NINJAK1DD/miningcore/issues/180)). Do not assume authentication
  from an earlier subscribe response. Avoid periodic reauthorization with `d=` when the miner only needs
  ordinary authentication, and let server VarDiff handle adaptive retargeting where supported.
  Update or reconfigure firmware/proxies that continually renegotiate; commission their
  behavior on an isolated endpoint first. The limit is fixed and has no operator override.
  A fleet requiring faster sustained miner-selected changes needs a reviewed compatibility
  change; removing the guard during an incident restores the resource-exhaustion path.
- **Already-subscribed error or connection closes on subscribe:** the first duplicate
  receives an error and keeps the working connection. `DuplicateSubscription` means the
  client repeated the duplicate after that warning. Configure/authorize renegotiation
  does not require another subscribe. Fix the firmware/proxy sequence; initial subscribe
  remains free and the error does not invalidate previously issued work.
- **Connection closes during assignment publication:** `AssignmentPublicationFailure`
  means an assignment could not be completed safely, or the pool faulted. It can arise
  during an idle VarDiff update without any request or response in progress.
  This includes disconnects immediately after an accepted `mining.submit` when its VarDiff
  update cannot publish work. The accepted proof remains accounted for and is not counted
  as invalid. Check the `publication-failure` admission-counter outcome.
  No contradictory second response is sent. Reconnect for a fresh subscription and
  assignment. The counter combines work-unavailability and outbound-queue failures;
  it does not identify their cause. Across idle, accepted-share and request paths,
  inspect the `AssignmentPublicationFailure` record's `failure` and optional `code`
  fields. A `job-not-found` category (code `21`) points to work availability or pool
  isolation; an `io` category can indicate send-queue pressure, so also check whether
  the miner/proxy drains responses and whether its network connection is stalled.
  An `io` category alone does not prove the miner caused the failure. An `argument`
  or `share-rejected` category (including code `20`) can indicate a difficulty outside
  the supported BLAKE2b range reaching publication: check miner-requested/static
  values and any autodiff source, then the assignment path. These are general error
  categories, so they do not uniquely identify an invalid difficulty. An
  `invalid-operation` category points to a failed operation/state invariant;
  `cancelled` indicates cancellation outside observed host shutdown. Correlate these
  with pool lifecycle diagnostics; ordinary host cancellation closes partial
  assignments without emitting this event. Diagnostics use
  bounded categories and codes; raw exception messages are withheld. This event does
  not by itself mean the miner exceeded its negotiation allowance.
- **Startup refuses a node:** check exact version, RPC authentication, selected chain,
  deployment state, and `!blake2b`. Do not remove the gate or substitute the `bitcoin` template.
- **Activation parent RPC is temporarily unavailable:** work verification retries with
  exponential backoff from 1 to 30 seconds. No unverified job is published; previously
  verified work is retained. Forced rebroadcasts emit nothing until that first verified job
  exists, so startup remains blocked before listener activation. Transport failures do not
  stop unrelated pools. A successful
  but malformed/contradictory consensus response still invokes the terminal failure path.
- **Malformed or unknown work:** check firmware/proxy field lengths and job preservation.
  A successful but incompatible daemon identity, chain or deployment response faults the
  **affected BLAKE2b pool**, closes its listeners and rejects new submissions. Other pools
  remain running. This is distinct from retryable transport errors. Stop Miningcore before
  replacing or upgrading a daemon; review compatibility before restarting.
  Ordinary Bitcoin Stratum translation is not a compatible substitute.
- **Unexpected low-difficulty shares:** verify the notify compact target and miner protocol,
  not only the displayed `set_difficulty`. Avoid unreviewed time/version rolling.
  A stock SHA-256d Sv1 miner, or a generic BLAKE2b miner without this Sia-style header-v2
  contract, may show 100% low-difficulty rejects or malformed-work errors. Use a compatible
  miner; changing its displayed algorithm name or difficulty does not translate the protocol.
- **Daemon rejects a candidate:** preserve the submission hash, daemon rejection reason,
  template and recovery evidence. Independently look up the block. A missing response is
  not acceptance, and `duplicate-invalid` must never be treated as success.
- **Accounting pipeline stops:** follow [recovery guidance](troubleshooting.md); never import
  a quarantine file as a recovery journal. Preserve PostgreSQL and all journals first.

### Multi-pool failure isolation

BLAKE2b can share a Miningcore process with other enabled pools. A terminal failure in its
job pipeline or pool lifetime closes that pool's admission gate and listeners without
terminating healthy sibling pools. Its public pool API reports `miningState` as `starting`,
`online`, `draining`, `faulted` or `stopping`. `draining` means local admission is closed
after a fault but previously owned operations remain; `faulted` means that drain has finished.
`stopping` takes precedence once host shutdown is requested. The separate `miningFaulted`
Boolean stays true after a local fault, including during host shutdown, so operators can
still identify a faulted pool while restarting. Failures arising only during host shutdown
do not trigger local isolation or set this flag. It is false before local isolation; ordinary
non-isolated pool responses omit both fields. A fault also produces an operator notification
and an error log. The first three secondary failures are logged at Info, then further failures
at Debug without repeating notifications; expected host-shutdown noise remains suppressed.
Outstanding drains warn after the first 30 seconds and every 30 seconds thereafter until
completion. Counts are outstanding admission leases, including nested payout-cycle and
classification/commit leases, not distinct shares, payments or RPCs. Fast drains emit only
the completion message.
The state describes local mining availability, not proof of wallet or database health.

New payout cycles and wallet operations are skipped for the isolated pool. Block classification
rechecks admission after database loading and again before committing daemon observations.
If isolation occurs during classification, **all results are discarded** without changing
persisted block/reward/balance state. A commit lease acquired before isolation instead allows
those owned database transitions and their post-commit notifications to finish; it never
authorizes a later wallet payment, which has its own admission check. Submissions and
wallet operations already owned before isolation are allowed to finish: cancelling an RPC
after a daemon accepted a block or payment can lose its financial outcome. A slow owned
submission remains tracked even after the local connection-drain timeout; disconnected miners
must not interpret a missing acknowledgement as proof that their share was not recorded.
The submission lease spans candidate persistence and `PersistenceAdmission`: for ordinary
shares, that is recorder queue admission (or an emergency journal force-flush), **not** the
normal queued PostgreSQL commit. Ordinary-share durability continues to rely on the shared
Share Recorder's flush/failover policy; local isolation neither strengthens nor weakens it.
Existing accepted relay shares, recorder entries, PPS liabilities and recovery evidence are
not discarded, and their accounting identities are not reset.

Listener teardown closes miner sockets promptly; it does not wait for an error response to
reach an unresponsive client. Miners may therefore see a transport close rather than a JSON-RPC
error. Outstanding submissions retain ownership independently of that socket. Reconnects in
the teardown window are refused without per-attempt error stack traces.

There is no automatic restart or compatibility bypass. Correct the underlying daemon or
configuration problem, inspect outstanding block/payment outcomes and restart Miningcore in
a planned maintenance window. The failed pool stays isolated (`draining` then `faulted`)
until that restart.
Separate services remain an option when independent operator restarts are required.

This is **not** isolation from shared infrastructure failure. Invalid cluster configuration,
shared startup preflight failures, unrecoverable database/journal failures, uncertain wallet
outcomes and process-wide resource failures retain their existing fail-closed shutdown policy.
Other pools cannot safely continue accepting financial work when shared durability is lost.

Explorer metadata remains omitted until a chain-specific service and its block, transaction
and address routes are verified. Do not substitute a SHA-256d Bitcoin explorer for this fork.

Automated tests include the five official header-v2 vectors, strict configuration and share
parsing, exact target boundaries, difficulty snapshots, and a pinned-node activation/submission
fixture. The node fixture is enabled by `MININGCORE_TEST_BLAKE2B_BITCOIND` and is explicitly
skipped if that binary is unavailable; the main CI lane installs its checksum-pinned binary.
It drives real manager startup and newline-delimited TCP subscribe/authorize/configure/submit,
constructs Sia-style proofs independently from notify data, and verifies accepted blocks for
SOLO/PPS/PROP/PPLNS. It also checks VarDiff notification ordering, strict JSON rejection,
PPS admission evidence, coinbase maturity, fee allocation, and a confirmed wallet payment.
Its persistence sink is substituted: this test is not a PostgreSQL ledger integration test.
A second, explicitly gated test requires both that binary and `MININGCORE_TEST_POSTGRES`.
It feeds the real miner's accepted proof into the PostgreSQL accounting repository, verifies
PPS credit/remainder precision, duplicate replay and conflicting-payload rejection, and runs
SOLO/PROP/PPLNS allocation against actual share/balance tables. Confirmed or orphaned PPS
blocks cannot credit the same liability again or reverse it. Each run owns a disposable schema.
Inspect actual test results before deployment—test code existing is not evidence that a run passed.
Real-network maturity, payout liquidity, firmware behavior and long-running VarDiff require
operator commissioning beyond isolated regtest.

## Immutable source provenance

Protocol baseline rechecked against upstream on 2026-09-04 and 2026-09-05:

Loader constants enforce this reviewed compatibility boundary; they are not independent
proof of upstream consensus. That evidence is the pinned source audit, official vectors
and accepted-block integration tests. Changes to these constants require renewed review.

| Contract | Reviewed source |
| --- | --- |
| Stable node and release | [Knots v29.4.1.knots20260508](https://github.com/bitcoinknots/bitcoin/tree/8c85b1585dac23f964e2dd32045624de7f02aa58) |
| Header layout | [src/primitives/block.h](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.h) |
| H1/H2, ASIC profiles, PoW, XOR | [src/primitives/block.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.cpp) |
| Official vectors | [block_header_v2.json](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/test/data/block_header_v2.json) |
| GBT rules and version | [src/rpc/mining.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/rpc/mining.cpp) |
| Activation parameters and target shift | [chainparams.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/kernel/chainparams.cpp), [pow.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/pow.cpp) |
| Miner work and target accounting | [CONVOY datum_pow.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_pow.c) |
| Miner notify, submit and payout coinbase selection | [CONVOY datum_stratum.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_stratum.c) |

The Knots and CONVOY default heads were unchanged from these pins at the recheck. New,
unmerged gateway proposals addressed strict parsing, duplicate replies, diagnostics and C
memory safety; they do not redefine this consensus baseline. Re-audit upstream before merge
and before accepting a new daemon revision rather than automatically tracking a moving branch.
