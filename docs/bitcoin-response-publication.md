# Bitcoin-family response and work publication failures

Issue [#183](https://github.com/NINJAK1DD/miningcore/issues/183) makes a failed
publication after a response attempt terminal for the connection. Successful wire
ordering is unchanged. There is no attempt to roll back an acknowledgement or a
difficulty notification that may already be visible to the miner.

## Audited paths and compatibility decision

The audit covers `BitcoinPool`, its `MergedMiningBitcoinPool` subclass, and the
overrides in `BitcoinBlake2bPool`. Other algorithms with independent `PoolBase`
dispatchers are outside this Bitcoin handler policy. They share the transport's
disconnect latch and completed-queue rejection.

| Path | Publication boundary and policy |
| --- | --- |
| Subscribe, including canonical resubscribe | Preserve subscriber data, success response, NiceHash lookup/difficulty, then initial notify. A failure after the response attempt closes the session and clears its jobs. Direct-SOLO still withholds work until authorized. This does not change the separate resubscription policy in #181. |
| Authorize, including static password difficulty | Address validation stays before the response. Success precedes static difficulty and direct-SOLO notify. Failure after success is terminal. Invalid address handling retains its existing configured login-ban behavior. |
| Submit and immediate VarDiff | Proof validation/rejection is separate from accounting, acknowledgement, accepted-share bookkeeping and later work publication. Publication failure cannot increment invalid shares or invoke share banning. Already admitted accounting is neither retried nor rolled back. |
| Idle VarDiff | Once the pending difficulty is applied, failure to construct or publish its work invalidates the connection even though no request is active. |
| Configure | Canonical version-rolling/minimum-difficulty negotiation still precedes its sole response, with no new canonical notify. A failed response enqueue or an override failing after responding is terminal. BLAKE2b retains its gated matching difficulty/notify publication. |
| Suggest difficulty | Malformed suggestions retain the existing acknowledged no-op behavior. Valid suggestions still acknowledge before changing difficulty. Publication errors now propagate to the terminal boundary instead of being logged and ignored. |
| Extranonce subscribe and unsupported requests | Each attempts one response. Failure of that attempt closes the connection; recovery does not try another response. Existing ignored methods remain ignored. |
| Merged mining | Inherits the audited dispatcher and publication handlers. Its auxiliary-address validation remains before acknowledgement. Manager-owned candidate outcomes and the pre-published statistical share retain their existing ownership and deduplication. |
| BLAKE2b overrides | Retain staged subscription lookup, assignment gate, admission budget and terminal budget latch from #179/#182. The shared response boundary delegates cleanup to that existing policy. |

Normal canonical difficulty/notify layouts, chain `nBits`, clean-job flags,
version masks and direct-SOLO authorization generations do not change. No work or
credit is rebound to a new worker identity. Direct-SOLO's existing pool-wide
consensus-construction fail-stop remains separate from connection publication
failure. See [direct-SOLO](bitcoin-direct-solo.md) and
[immutable notification snapshots](bitcoin-job-notifications.md).

## One attempt and bounded state

Serial dispatch snapshots `StratumConnection.ResponseSequence`. `RespondAsync`
increments it **before** enqueue, including attempts that fail because the queue
is full or completed. Notifications do not increment it. If a handler throws
after the sequence changes, no recovery response is attempted. A pre-response
`StratumException` can produce one error and leave the session usable; if that
error cannot be queued, cleanup is terminal too. Unexpected exceptions retain
their existing propagation to transport teardown.

This tracks attempts, not delivery: abortive TCP close may discard queued bytes.
Repeated miner-selected IDs belong to separate serial requests and remain valid.
There is no ID set, dictionary or request history; state is one counter and one
terminal bit per connection. All response payloads must use `RespondAsync`.

`Disconnect` latches before closing I/O. The receive loop checks the latch before
every buffered line, and cancellation after processing a line prevents dispatch
of the next one. Pool dispatch also checks the latch for already decoded calls.
Closing a socket alone is insufficient because the pipe may already contain
several complete requests. In-flight accounting retains its existing drain
semantics; a later buffered request cannot restart a closed session.

The design follows [JSON-RPC response correlation](https://www.jsonrpc.org/specification#response_object)
and preserves the successful negotiation shapes from
[BIP 310](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki).
These references do not prescribe rollback for Stratum work notifications;
terminal handling is the explicit compatibility choice here. Existing Stratum
JSON-RPC/NiceHash response formatting is preserved.

## Diagnostics and reconnect behavior

Each terminal publication failure emits at most one `AssignmentPublicationFailure`
diagnostic and one `miningcore_stratum_admission_total` outcome `publication-failure`
for its connection. The diagnostic uses a server-generated connection ID, a bounded
failure category and an optional allowlisted code. It excludes exception text,
request IDs, credentials, payout addresses and raw payloads. Metrics use configured
pool and fixed outcome labels, never miner-selected labels. At the post-response
and VarDiff boundaries, cancellation owned by the operation's shutdown token does
not emit publication-failure telemetry. Existing transport
diagnostics may separately describe teardown. See [Stratum diagnostics](stratum-diagnostics.md).

Firmware/proxies may see success, a notification prefix, EOF or TCP reset. They
must discard the incomplete assignment, reconnect with their normal backoff,
renegotiate extensions, subscribe and authorize, and wait for fresh complete work.
They must not interpret the disconnect as a share rejection or replay old jobs on
the replacement session. Repeated publication failures require operator
investigation; aggressive reconnects remain subject to
[connection admission](stratum-connection-admission.md). No new firmware-specific
compatibility or physical ASIC certification is claimed.

## Regression coverage

`BitcoinPublicationFailureTests` drives canonical production dispatch over real
loopback TCP. Barriers hold handlers or the sender while subsequent requests or
queue saturation are established. Tests cover recoverable errors, reused IDs,
subscribe/static authorization/configure failures, response and notification
enqueue failures, a completed queue, and accepted-share VarDiff failure. Response
attempt assertions use the sequence counter independently of TCP delivery; accepted
accounting and invalid-share statistics are asserted separately. The receive-loop
test preloads one pipe read with multiple complete lines to prove the boundary
without relying on OS packet coalescing. Existing BLAKE2b tests protect its override
and cancellation behavior.

`BitcoinPublicationRegtestTests` obtains real Bitcoin Core templates and validates
real non-block proofs through the production job manager over TCP, in custodial
and direct-SOLO modes. It checks accepted accounting, version-rolling negotiation,
immutable direct payout binding, terminal VarDiff failure, and a replacement
connection's fresh extranonce and complete assignment. Existing block submission,
confirmation and PostgreSQL suites cover the separate durable candidate paths.

Run the Bitcoin/Stratum suites, and opt into the canonical Bitcoin Core and
PostgreSQL direct-SOLO integration suites using the isolated
[Windows/WSL lab](merged-mining-regtest-validation.md). Daemon fixtures create
temporary regtest data directories and independent ports; database fixtures use
isolated schemas. Never point these tests at production.
