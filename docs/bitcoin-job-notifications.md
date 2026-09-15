# Bitcoin job notification snapshots

The fix for [#153](https://github.com/NINJAK1DD/miningcore/issues/153) establishes ownership of
the generic Bitcoin-family `mining.notify` parameters. `StratumConnection.SendAsync`
queues a payload; `SendMessage` serializes it later. Previously,
`BitcoinJob.GetJobParams` changed the cached array's final `clean_jobs` field and
returned that same array. A subsequent call with the opposite flag could alter
an already queued notification.

## Generic Bitcoin contract and caller audit

| Path | Construction and consumption |
| --- | --- |
| Custodial Bitcoin and Bitcoin-family templates, including Litecoin | `BitcoinJob.Init` caches the nine fields. `BitcoinJobManager.GetJobParamsForStratum` publishes them; `BitcoinPool.CreateWorkerJob` also calls `GetJobParams` on the current shared job. |
| Direct Bitcoin SOLO | `BitcoinJobManager.GetDirectJobForStratum` constructs a worker-specific job with `InitDirect`. `BitcoinPool.CreateWorkerJob` checks the authorization generation, records the job in the worker context, and obtains its notification. |
| Litecoin/Dogecoin merged mining | `MergedMiningBitcoinJob.InitMerged` calls the base initializer and inherits `GetJobParams`. Its manager and pool use the Bitcoin notification contract. |
| Satoshicash | `SatoshicashJob.Init` builds the same nine-field array and inherits `GetJobParams`. Its own manager and pool read the same final flag. |
| BLAKE2b | `BitcoinBlake2bJob` overrides `GetJobParams` and already copies its different payload. It does not call this base implementation. |
| ProgPoW and its derived jobs | `ProgpowJob` overrides the base method and returns a new `ProgpowJobParams` object for each call, including calls through a `BitcoinJob` reference. Its pool projects a separate seven-field wire array. See the sibling audit below. |

The Bitcoin and Satoshicash pools retain the manager notification, read job ID
at index 0 and `clean_jobs` at index 8, and issue worker notifications during
broadcast, subscription and difficulty changes. Direct SOLO also issues work
after authorization. No audited caller requires reference identity of the
returned array or writes back through it to update the job.

The generic method now clones the outer array and each nested `string[]`
before setting the flag **on the clone**. Field order, values and CLR types stay
the same: job ID, previous hash, coinbase prefix, coinbase suffix, Merkle branches,
version, bits, time, and Boolean `clean_jobs`. Both arrays belong to that call.
Replacing any returned field or branch cannot change another notification or
the cached job. Immutable strings, including branch hashes, remain cached and
shared; coinbase construction, hashing and hexadecimal conversion are not repeated.
Branch containers are identified by type, without coupling snapshot creation to
slot 4. The existing Bitcoin/Satoshicash initializers still put branches in slot 4.

Copying only the outer array would leave the nested branch array writable through
every notification. This follows .NET's documented
[shallow-copy semantics of `Array.Clone`](https://learn.microsoft.com/en-us/dotnet/api/system.array.clone?view=net-10.0).
Job initialization still completes before publication; this change does not make
reinitializing a published job supported. The generic layout contains only strings,
string arrays and a Boolean; this is not an arbitrary object-graph deep copier.

## Sibling-family audit

The review of PR #173 expanded the original Bitcoin-only scope, tracked in
[#174](https://github.com/NINJAK1DD/miningcore/issues/174). The following
paths have their own notification contracts and do not inherit the generic fix.
They are fixed explicitly in this update:

| Path | Snapshot and broadcast contract |
| --- | --- |
| Equihash and inherited layouts, including Bitcoin Gold | Copy the outer array before setting the last flag. Other fields are immutable strings. |
| Veruscoin | Copy the outer array before setting the penultimate flag; preserve the trailing solution string. |
| Xelis and Warthog | Copy the outer array before setting the last flag; reuse immutable strings. |
| Ergo | Copy at job issuance, then fill the per-worker target in that connection's owned snapshot before queueing. No second pool-level copy is needed. Copying only at target projection would leave the earlier interval unprotected. |
| ProgPoW, Firo, Kiiro, Realichain and Telestai | Return a new two-field parameter object through virtual dispatch. Capture each broadcast's flag from its own notification before invoking miner callbacks, so a newer `currentJobParams` cannot change an in-progress fan-out. |

Bitcoin, ProgPoW, Satoshicash, Equihash/Veruscoin, Xelis, Warthog and Ergo
difficulty-only updates explicitly request `clean_jobs=false`. This simplifies
their existing always-false logic, preserves in-flight work and avoids reading
a pool broadcast that may not yet exist when the manager already has a job.

Other inspected paths are **not covered by a blanket caller-ownership guarantee**:
BLAKE2b already clones its outer array and has an empty Merkle array; Kaspa
constructs a per-worker wire array from cached job data; Nexa and Handshake return
cached arrays without changing a per-call `clean_jobs` flag. Their lack of this
specific flag race does not establish that every nested field is caller-owned.

## Allocation trade-off

The generic nine-field layout requires a new outer array and a new Merkle array
per call, including the empty-branch case. Branch strings stay shared. Returning
two cached mutable arrays for true/false would prevent the flag overwrite while
restoring cross-caller mutation of fields and branches; it would violate the
ownership contract and acceptance tests. Keep the copies. If very large fan-out
shows measurable allocation/Gen0 pressure, profile representative broadcasts
before considering an immutable wire representation as a separate design change.

## Regression validation

`BitcoinJobNotificationTests` initializes real Bitcoin, direct-SOLO, Litecoin,
merged-mining and Satoshicash jobs, each with zero or three transactions. It checks:

- Both `true/false/true` and `false/true/false` issuance sequences, with JSON-RPC
  serialization deliberately deferred until after the opposing call.
- Mutation of every outer slot and every nonempty branch, followed by checks of
  an outstanding notification and a future notification.
- Nine-field JSON order and types, coinbase parsing, branch contents, independent
  mutable containers, and reuse of immutable string instances.
- Manager broadcasts interleaved with the production custodial and direct-SOLO
  worker issuance paths, including direct payout destination and generation.
- Parallel issuance with unique outer/branch containers and the requested flag
  for every result; no timing-based assertion or probabilistic race detector.
- Two actual notifications enqueued before starting `StratumConnection`'s send
  pump, followed by interfering calls/mutations, then validation of the emitted
  JSON lines over loopback TCP. Queueing before consumer startup fixes the ordering.
  Teardown cancels and drains dispatch before closing the client stream; any wire
  assertion failure takes precedence over a simultaneous teardown failure.
- String-array copying in alternate slots and preservation of null fields.

`JobNotificationSnapshotTests` exercises the sibling implementations with
alternating flags, caller mutation and parallel issuance. Ergo and ProgPoW use
their real initializers; Equihash/Veruscoin, Xelis and Warthog use representative
notification caches to isolate the ownership boundary without claiming native
proof or live-daemon validation. ProgPoW tests also cover all four derived job
classes and calls through a `BitcoinJob` reference.

Pool regressions exercise Satoshicash, Equihash/Veruscoin, Xelis, Warthog and Ergo
difficulty updates with an absent, true or false previous broadcast. They inspect
the queued difficulty/target and job messages for two workers, verify independent
non-clean notifications and unchanged caches, and check Ergo's distinct worker
targets. Protected notification caches use test subclasses; private pool/manager
state uses checked reflection without changing the production visibility.

Run the deterministic matrix without a daemon:

```powershell
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -p:BuildOdoCryptWindows=false --filter FullyQualifiedName~BitcoinJobNotificationTests
```

The Windows managed-only build option is sufficient for these notification tests.
It is not a production publish option. Existing `BitcoinDirectSoloRegtestTests`
and `BitcoinVersionRollingRegtestTests` provide supplementary daemon-backed
coinbase and share validation when `MININGCORE_TEST_BITCOIND` points to a test
binary. Their fixtures create disposable regtest directories and loopback RPC
ports. CI provisions the pinned Bitcoin Core binary for these tests.

This fix concerns notification ownership. Initial-job/forced-refresh coordination
is tracked separately by [#141](https://github.com/NINJAK1DD/miningcore/issues/141).
