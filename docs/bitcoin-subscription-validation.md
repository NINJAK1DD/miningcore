# Bitcoin duplicate-subscription validation record

Policy and upgrade guidance live in [subscription compatibility](bitcoin-subscription-policy.md).
This record separates generated baseline observations and dated lab evidence from that policy.

## Regression and lab evidence for issue #181

The [TCP regression suite](../src/Miningcore.Tests/Blockchain/Bitcoin/BitcoinDuplicateSubscriptionTests.cs)
uses the production dispatcher, subscription/job creation and share validation over real
newline-delimited TCP. The ordinary fixtures substitute persistence and background template
acquisition. They cover original-proof credit, representative Bitcoin/LTC/DOGE/BCH assignments,
proxy resume parameters, extranonce extension, ASICBoost response shape, malformed duplicates,
missing/null IDs, independent connections, a pipelined repeated-subscribe burst, direct payout
binding and delayed persistence admission.

Before the fix, the original-proof test failed against `34af171c7`: the first subscription
assigned `f0000001`, the second assigned `f0000002`, and the coinbase transaction hash changed:

- Original coinbase transaction ID (double SHA-256): `f45e61a56a3db6ff201d6c4aa3eba04c3b4167662524e55b7183544236453021`.
- Rotated coinbase transaction ID (double SHA-256): `0bc28fd581ad8ddb924efec27fef10f91453387d47fb5d9e7d80141c4882aa46`.

An outstanding proof with nonce `0000003c`, valid at difficulty `1e-7` under the original assignment, was
rejected with error `23` and reconstructed difficulty `1.0937091875612106E-09`. These are
recorded observations from a generated fixture, not fixed consensus vectors. The same test
now accepts the original proof and retains extranonce, job count and accounting identity.
To repeat the before/after comparison, copy the regression test file into an isolated checkout
of that baseline and run `OutstandingCanonicalProof_RemainsValidAfterDuplicateSubscribe`.

Validation on 2026-10-04:

- Windows .NET 10: 954 relevant Bitcoin-family/transport cases passed; 17 optional external
  integration cases were explicitly skipped. The managed-only test build used
  `-p:BuildOdoCryptWindows=false`; this change does not modify native hashing code.
- Documented Ubuntu 22.04 WSL compatibility lab: all 21 selected duplicate-subscription,
  publication-cleanup, direct-SOLO and version-rolling cases passed, with no skips. The lab ran
  the rebuilt managed test assembly with its existing Linux native libraries, in isolated test
  output. Active pool services, wallets and database configuration were not replaced.
- The new daemon-backed TCP case used Bitcoin Core 28.1, with the official Linux archive
  verified against the repository CI SHA-256 pin
  SHA-256: `07f77afd326639145b9ba9562912b2ad2ccec47b8a305bd075b4f4cb127b7ed7`. Core accepted
  both the outstanding custodial block and direct block after the duplicate warning; decoded
  coinbase IDs and payout scripts matched the original jobs. Persistence is substituted in
  this fixture; it is not a PostgreSQL ledger test.

Run the suite with `dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --filter
FullyQualifiedName~BitcoinDuplicateSubscriptionTests`. Set `MININGCORE_TEST_BITCOIND` to a
real Core binary to enable the daemon case. Existing publication tests now inject failure
during initial subscription or configure rather than relying on an unsafe second subscription.

## Review dispositions

Reviews of PR #206 at `01db4c671` and `5fe5c3e51` approved the state-transition fix
and identified documentation improvements and optional hardening. The decisions below
describe the resulting protocol and testing behavior without depending on review-only labels.

| Finding | Disposition |
| --- | --- |
| Operator release guidance | Added an Unreleased operator entry with upgrade actions, literal telemetry outcomes and a policy link. |
| Initial versus duplicate subscription publication | The subscribe row now describes initial subscription and links the duplicate policy. |
| Independent handler scope | Explicitly name Satoshicash, Nexa, Handshake, Equihash, ProgPoW and Kaspa, distinguish the related #192 audit, and narrow the issue/PR completion claim to the implemented dispatchers. Independent handler safety is not claimed. |
| First-warning observability | Attempt the fixed `duplicate-subscribe-warning` metric outcome once after successfully enqueueing a warning; counter increments depend on successful observer publication. No dedicated log or client labels. The exporter rejects arbitrary outcomes. |
| Subscription override and NiceHash side effects | Dispatch rejects duplicates before virtual subscription hooks; retain the core guard for direct core calls. A throwing override and NiceHash lookup regression proves the early boundary. |
| Terminal job registry cleanup | Latch closure and permanently close jobs before I/O cleanup and guarded observers. Cover throwing observers, delayed broadcast insertion and buffered traffic. |
| Shared Bitcoin/BLAKE2b duplicate policy | Both dispatchers share the worker warning flag, response text, recovery boundary and terminal cleanup. BLAKE2b keeps its difficulty budget and terminal admission latch; terminal difficulty-budget disconnects also close its job registry. |
| Merged-pool and NiceHash coverage | Added real TCP through `MergedMiningBitcoinPool` with its worker context and accepted parent work, plus a canonical NiceHash duplicate. The merged fixture substitutes the job manager; it does not claim auxiliary-daemon or AuxPoW validation. |
| Acknowledgements queued before terminal closure | Document abortive close and possible acknowledgement loss, preserving admitted accounting ownership. Transport drain semantics stay unchanged. |
| Replay subscription instead of rejection | Document why rejection matches BLAKE2b and the accepted issue contract while retaining one recoverable retry. Replaying success would give clients a different retry contract. |
| Snapshot extranonce/version mask per job | Preserve the stable session extranonce; shared job templates must not contain worker-specific values. Apply BIP310's last-received-mask submit rule to repeated configure responses, as defined by Miningcore's policy. Add a TCP mask-change regression and real Core acceptance with latest-mask rolled headers. |
| Baseline-only branch / wording / dated evidence / troubleshooting | Label the old-mismatch reproduction branch, replace the firmware commissioning wording, move dated evidence into this record and add canonical subscription troubleshooting. |
| Queue completion and inspection regression stability | Use the internal `StratumConnection.CompleteSendQueue()` boundary shared with transport teardown and nonblocking `TryReceiveQueuedMessage()` inspection for tests. Publication and notification-snapshot regressions retain their ordering/failure assertions without depending on private queue storage or its field name. Inspection tests omit the sender or hold it at a barrier. |

The version-mask decision follows the primary [BIP310 specification](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki),
which calculates submitted `nVersion` from the job version and the last mask received by the
miner. The TCP regression narrows `00006000` to `00002000`, rejects old `00004000` bits and
accepts the same outstanding job under the current mask. The daemon case also changes the mask
after issuing custodial/direct jobs and checks the exact accepted block version.

## Review follow-up validation

- Windows .NET 10: **976 passed, 17 optional external integrations skipped, 0 failed**
  across Bitcoin-family handlers, transport, bounded metrics and reviewed test collections.
  The managed-only build used `-p:BuildOdoCryptWindows=false`.
- Documented Ubuntu 22.04 WSL lab: **30 passed, 0 skipped, 0 failed** across the selected
  duplicate-subscription, cleanup, direct-SOLO, version-rolling and metrics cases. Core 28.1
  accepted both outstanding custodial/direct blocks with the original coinbase and the
  exact latest-mask version. Existing native libraries and the verified daemon pin were used.
- Two additional Linux queue checks passed on the final assembly: failed duplicate-warning
  enqueue closes canonical/BLAKE2b sessions with one response attempt and one publication
  failure report, without a recovery response or duplicate-warning metric.
- Administrative API, wallet-backup, release-installation, local Markdown link/anchor,
  diagnostic-source and generated PPS migration guards passed. No native hashing, daemon,
  database schema, ledger format or active lab configuration change was required.

## Documentation precision and queue completion validation

After the review at `5fe5c3e51`, clarified best-effort telemetry and Miningcore's
repeated-configure policy, replaced review-only row labels with standalone descriptions,
and documented the literal warning metric and terminal BLAKE2b difficulty cleanup in release
guidance. The duplicate-warning and recovery-response queue tests now use the typed completion
method shared with transport shutdown, retaining their original failure assertions.

- Windows .NET 10: **254 passed, 1 optional daemon case skipped, 0 failed** across transport,
  canonical/BLAKE2b publication and duplicate-subscription regressions, plus bounded metrics.
- Documented Ubuntu 22.04 WSL lab: the same selection with pinned Core 28.1 enabled produced
  **255 passed, 0 skipped, 0 failed**, including custodial/direct block acceptance.
- Documentation, local link/anchor, diagnostic-source and generated migration guards passed.

## Queue inspection tidy-up validation

The optional follow-up from the review at `e5f5d78ee` removes the remaining three private
send-queue reflection sites in BLAKE2b publication and notification-snapshot tests. The internal
nonblocking inspection method returns one queued payload without exposing the queue. Snapshot
tests omit a sender; publication fault injection holds the sender at its existing barrier.
All original payload ordering, empty-queue and publication-failure assertions are retained.

The relevant notification-snapshot, canonical/BLAKE2b publication and transport selection
passed **293 cases on Windows and 293 in the documented Ubuntu 22.04 WSL lab**, with zero
failures or skips on either platform. No test in the project refers to the private send-queue
field after this tidy-up.
