# Bitcoin-family response and work publication failures

Issue [#183](https://github.com/NINJAK1DD/miningcore/issues/183) makes a failed
publication after a response attempt terminal for the connection. Successful wire
ordering is unchanged. There is no attempt to roll back an acknowledgement or a
difficulty notification that may already be visible to the miner.

## Audited paths and compatibility decision

The audit covers `BitcoinPool`, its `MergedMiningBitcoinPool` subclass, and the
overrides in `BitcoinBlake2bPool`. Other algorithms with independent `PoolBase`
dispatchers are outside this Bitcoin handler policy. They share the transport's
disconnect latch and completed-queue rejection. The independent handler audit is
tracked in [#192](https://github.com/NINJAK1DD/miningcore/issues/192).

| Path | Publication boundary and policy |
| --- | --- |
| Subscribe, including canonical resubscribe | Preserve subscriber data, success response, NiceHash lookup/difficulty, then initial notify. A failure after the response attempt closes the session and clears its jobs. Direct-SOLO still withholds work until authorized. This does not change the separate resubscription policy in #181. |
| Authorize, including static password difficulty | Address validation stays before the response. Success precedes static difficulty and direct-SOLO notify. Failure after success is terminal. Invalid address handling retains its existing configured login-ban behavior. |
| Submit and immediate VarDiff | Proof validation/rejection is separate from accounting, acknowledgement, accepted-share bookkeeping and later work publication. Publication failure cannot increment invalid shares or invoke share banning. Already admitted accounting is neither retried nor rolled back. |
| Idle VarDiff | Once the pending difficulty is applied, failure to construct or publish its work invalidates the connection even though no request is active. |
| Configure | Canonical version-rolling/minimum-difficulty negotiation still precedes its sole response, with no new canonical notify. A failed response enqueue or an override failing after responding is terminal. BLAKE2b retains its gated matching difficulty/notify publication. |
| Suggest difficulty | Malformed suggestions retain the existing acknowledged no-op behavior. Valid suggestions still acknowledge before changing difficulty. Publication errors now propagate to the terminal boundary instead of being logged and ignored. |
| Extranonce subscribe and unsupported requests | Each attempts one response. Failure of that attempt closes the connection; recovery does not try another response. Existing ignored methods remain ignored. |
| Merged mining | Managers explicitly mark proof acceptance immediately after validation, before accounting construction, synchronous statistical observers or candidate submission. Later failures cannot enter invalid-proof/ban handling, including a subscriber's `StratumException`. Candidate ownership and the pre-published statistical share retain their existing ordering and deduplication. |
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
There is no ID set or request history. State is bounded per connection: response
and accepted-proof counters, disconnect/transport/job closure flags, and one
publication-report flag held through a weak connection key. All response payloads
must use `RespondAsync`.

Both canonical and merged managers advance the accepted-proof counter immediately
after successful proof validation. Serial dispatch observes that milestone even if
the manager subsequently throws before returning. Failures after acceptance close
the session without a rejection response. Before acceptance, protocol errors retain
their existing recovery and invalid-share policy.

Successful-share counts, accepted telemetry and block-time bookkeeping occur after
`PersistenceAdmission` completes and before acknowledgement admission. Failed wire
enqueue cannot erase those statistics or cause republishing. A merged share already
owned by persistence retains this bookkeeping even if fail-stop closes before the
pool attempts its response. Failed persistence admission does not count as success.

`Disconnect` latches before closing I/O. The receive loop checks the latch before
every buffered line, and cancellation after processing a line prevents dispatch
of the next one. Pool dispatch also checks the latch for already decoded calls.
Closing a socket alone is insufficient because the pipe may already contain
several complete requests. In-flight accounting retains its existing drain
semantics; a later buffered request cannot restart a closed session.

Terminal cleanup closes the worker's job registry under the same monitor used by
canonical and direct-SOLO insertion. A broadcast already constructing work cannot
reinsert it after cleanup. Dispatch and job producers also check the connection
latch before starting work; BLAKE2b retains its assignment gate.

These transport changes apply to every pool family. Queue admission uses synchronous
[`Post`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.dataflow.dataflowblock.post?view=net-10.0)
because the existing BufferBlock is unbounded; declined admission remains observable.
Transport-owned teardown is marked before cancellation and queue completion. Sends
during that teardown throw cancellation, so ordinary peer EOF or host stop does not
become a queue-closed connection failure. Unexpected queue closure still throws I/O
failure. Independent handler failures remain visible and take precedence over
secondary socket errors caused by terminal cleanup. Every successful pipe read is
balanced by `AdvanceTo` in `finally`, following the
[pipeline contract](https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines).

The design follows [JSON-RPC response correlation](https://www.jsonrpc.org/specification#response_object)
and preserves the successful negotiation shapes from
[BIP 310](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki).
These references do not prescribe rollback for Stratum work notifications;
terminal handling is the explicit compatibility choice here. Existing Stratum
JSON-RPC/NiceHash response formatting is preserved.

## Diagnostics and reconnect behavior

Each connection attempts at most one `AssignmentPublicationFailure` diagnostic and
one `miningcore_stratum_admission_total` outcome `publication-failure`. A dedicated
report flag is independent of disconnect and BLAKE2b budget closure: a prior ban or
disconnect cannot suppress a later genuine failure. Cleanup always closes jobs and
attempts disconnect, even if context cleanup, a logger or a telemetry subscriber
throws. Neither sink's failure prevents the other from being attempted; failed sinks
are not retried. Bounded Debug `PublicationCleanupFailure` records describe secondary
cleanup failures without replacing the original exception. The primary diagnostic
uses a server-generated connection ID, a bounded
failure category and an optional allowlisted code. It excludes exception text,
request IDs, credentials, payout addresses and raw payloads. Metrics use configured
pool and fixed outcome labels, never miner-selected labels. At the post-response
and VarDiff boundaries, cancellation owned by the operation's shutdown token does
not emit publication-failure telemetry. Neither does cancellation from already-owned
transport teardown. A distinct non-cancellation failure during teardown still reports.
Both expected and unexpected exception diagnostics remain redacted at every level;
the IP-censor/GDPR flag does not authorize raw exception or credential logging. Existing transport
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

Additional barriers cover peer EOF during an in-flight handler, genuine failure
after a ban/disconnect, simultaneous broadcast insertion and terminal cleanup, and
faulting cleanup/telemetry sinks. Merged pool regressions inject synchronous accounting
subscriber errors after validation and assert candidate persistence remains exactly
once with no invalid-share telemetry or ban. Accepted block/non-block tests saturate
the response queue and verify valid-share counts and block time. Real Ethereum
dispatch is drained through peer EOF and host stop to cover transport-wide behavior.

The unrelated Windows recovery-fixture correction remains in its own test commit.
Its cleanup tolerates a completed provider cancellation, while timeouts still escape
before deleting any directory that could have a live recovery owner.

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
