# Bitcoin job notification snapshots

Issue [#153](https://github.com/NINJAK1DD/miningcore/issues/153) fixes ownership of
the generic Bitcoin-family `mining.notify` parameters. `StratumConnection.SendAsync`
queues a payload; `SendMessage` serializes it later. Previously,
`BitcoinJob.GetJobParams` changed the cached array's final `clean_jobs` field and
returned that same array. A subsequent call with the opposite flag could alter
an already queued notification.

## Contract and caller audit

| Path | Construction and consumption |
| --- | --- |
| Custodial Bitcoin and Bitcoin-family templates, including Litecoin | `BitcoinJob.Init` caches the nine fields. `BitcoinJobManager.GetJobParamsForStratum` publishes them; `BitcoinPool.CreateWorkerJob` also calls `GetJobParams` on the current shared job. |
| Direct Bitcoin SOLO | `BitcoinJobManager.GetDirectJobForStratum` constructs a worker-specific job with `InitDirect`. `BitcoinPool.CreateWorkerJob` checks the authorization generation, records the job in the worker context, and obtains its notification. |
| Litecoin/Dogecoin merged mining | `MergedMiningBitcoinJob.InitMerged` calls the base initializer and inherits `GetJobParams`. Its manager and pool use the Bitcoin notification contract. |
| Satoshicash | `SatoshicashJob.Init` builds the same nine-field array and inherits `GetJobParams`. Its own manager and pool read the same final flag. |
| BLAKE2b | `BitcoinBlake2bJob` overrides `GetJobParams` and already copies its different payload. It does not call this base implementation. |
| ProgPoW and its derived jobs | `ProgpowJob` hides the base method and maintains its own `ProgpowJobParams` object. `ProgpowJobManager` calls that separate method. |

The Bitcoin and Satoshicash pools retain the manager notification, read job ID
at index 0 and `clean_jobs` at index 8, and issue worker notifications during
broadcast, subscription and difficulty changes. Direct SOLO also issues work
after authorization. No audited caller requires reference identity of the
returned array or writes back through it to update the job.

The generic method now clones the outer array and its Merkle-branch `string[]`
before setting the flag **on the clone**. Field order, values and CLR types stay
the same: job ID, previous hash, coinbase prefix, coinbase suffix, Merkle branches,
version, bits, time, and Boolean `clean_jobs`. Both arrays belong to that call.
Replacing any returned field or branch cannot change another notification or
the cached job. Immutable strings, including branch hashes, remain cached and
shared; coinbase construction, hashing and hexadecimal conversion are not repeated.

Copying only the outer array would leave the nested branch array writable through
every notification. This follows .NET's documented
[shallow-copy semantics of `Array.Clone`](https://learn.microsoft.com/en-us/dotnet/api/system.array.clone?view=net-10.0).
The separate notification implementations in the table retain their existing
behavior. Job initialization still completes before publication; this change
does not make reinitializing a published job supported.

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
