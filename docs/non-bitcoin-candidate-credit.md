# Non-Bitcoin candidate accounting credit

This is the implementation and deployment audit for [issue #218](https://github.com/NINJAK1DD/miningcore/issues/218).
Candidate admission and accounting credit are separate decisions. A genuine block
candidate can bypass the issued share target, but its payout weight cannot exceed
the work sampled by that candidate target. The rule is
`credit = min(immutable issued credit, job candidate threshold)` in the validator's
units, followed by the family's existing stored-share conversion. Apply this to
**every** candidate, including a proof stronger than the assigned target. A cap
only on below-assignment proofs permits selective submission of strong candidates.
Ordinary shares retain issued/grace credit and the existing 1% admission tolerance.

## Inventory and units

`T` is the job's actual candidate target; `m` is the configured share multiplier.
The threshold is read from the submitted job, never current worker difficulty,
current network statistics or a newly arrived manager template. This inventory
covers all non-Bitcoin `ValidateShareDifficulty`/`TryValidateShareDifficulty`
candidate exceptions, including inherited custom validators.

| Family and exception paths | Candidate ceiling in validator units | Stored credit and settlement normalization |
| --- | --- | --- |
| Equihash; Veruscoin; inherited Bitcoin Gold and other Equihash jobs | job `Difficulty`, using that network's `Diff1BValue / T` | Stored directly; identity adjustment |
| ProgPoW; Firo, Kiiro, Realichain, Telestai; inherited variants | concrete validator's `Diff1 / T * m` | Divide by `m`; identity adjustment |
| Kaspa; Astrix, Karlsen, Spectre, WagLayla; inherited variants | `KaspaConstants.Diff1b / T * m` (Spectre uses the same numerator) | Divide by `m`; Kaspa payout adjustment uses `2^31 * MinHash`; Spectre uses `2^7 * MinHash` |
| Ergo | `Target(B).Difficulty`, in the same units as the Autolykos proof validator | Divide by `ErgoConstants.ShareMultiplier`; payout handler multiplies it back |
| Ethereum and Cortex | `BigMaxValue / T / 2^32` | Multiply by `2^32`; identity adjustment |
| CryptoNote and Conceal | template difficulty used by candidate admission | Whole issued difficulty; identity adjustment |
| Zano | template difficulty used by candidate admission | Unit multiplier required; identity adjustment |
| Handshake | job difficulty times `m` | Divide by `m`; identity adjustment |
| Nexa and Satoshicash | job difficulty times `m` | Divide by `m`; identity adjustment |
| Xelis | job difficulty (unit multiplier required) | Stored directly; identity adjustment |
| Alephium | job difficulty divided by `Pow2xDiff1TargetNumZero` | Existing stored conversion; payout handler multiplies by that power of two |
| Warthog Janus score candidates | minimum of job difficulty and immutable encoded `WarthogTarget.Difficulty()` | Existing Janus-number share units; identity adjustment |
| Warthog legacy SHA256d candidates | conservative ceiling of 1 | Legacy admission has a different hash domain from share scoring; unit credit avoids assigning a Janus-score ceiling to a SHA256d exception |

ProgPoW's difficulty-one numerators are **not all equal**. In particular, the
Ravencoin/Telestai numerator differs from Firo's. The policy follows the concrete
production validator, including Kiiro and Realichain's use of Firo's numerator.
Kaspa's displayed network difficulty is in another unit system; multiplying that
number by the share multiplier would leave a large excess-credit allowance.

Xelis and Zano previously accepted arbitrary custom multipliers while their wire
targets, candidate comparisons and/or stored weights did not apply them
consistently. These families now reject `shareMultiplier != 1` at pool startup and
at direct job construction. Their shipped unit-multiplier contracts are unchanged.
Do not deploy a custom non-unit configuration by bypassing this validation; it
needs a separately reviewed wire-target and historical-accounting migration.

The source equations are authoritative for this implementation. Primary protocol
references include [Alephium mining integration](https://docs.alephium.org/integration/mining/),
[Kaspa compact target conversion](https://github.com/kaspanet/kaspad/blob/master/util/difficulty/difficulty.go),
[Ergo Autolykos](https://github.com/ergoplatform/ergo/tree/master/src/main/scala/org/ergoplatform/mining),
and [Warthog acceptance-region analysis](https://docs.warthog.network/unique-features/janushash/analysis/preliminaries/).
The Warthog ceiling preserves the repository's existing score-credit contract;
it does not claim that every historical Janus epoch has a new linear hash-rate model.

## Assignment producers and protocol behavior

All families use the shared endpoint initialization, deferred VarDiff and worker
assignment gate. All 15 affected handlers read static password and NiceHash hints. Nexa and
Satoshicash additionally implement `mining.suggest_difficulty`; the other affected
handlers do not implement that producer. None implements BIP310
`minimum-difficulty` configure negotiation; Bitcoin's negotiation remains separate.
Those explicit constraints keep their existing eligibility and representability
rules. Difficulty validation, short-target quantization, monotonic grace timing,
accepted-proof duplicate sets and candidate block ownership remain in place.

Each affected pool obtains its automatic ceiling from its current job's policy.
VarDiff retargets cannot exceed it. Before deferred assignments are applied, the
pool also handles falling chain ceilings, even without a share-rate retarget.
Endpoint dynamic seeds are bounded before the connection is published. Embedded
target protocols (CryptoNote, Conceal, Beam, Ergo, Alephium and Ethereum V1) apply compatible
changes before snapshot construction and target projection. Other protocols keep
their existing difficulty-before-job publication boundary.

Automatic bounds honor configured minima, any existing protocol/device minima,
and integer target requirements. An empty range pauses retargeting and emits a warning/telemetry;
it does not lower an explicit floor. Static or device assignments above the
candidate threshold remain in force and warn that candidate credit is capped,
honest work can be under-credited and firmware can discard candidates. Warnings
are bounded per worker/assignment or incompatible-floor episode. Operators must
lower incompatible port/static/device floors before public pooled deployment.

## Payout exposure and replay

PROP, PPLNS and PPLNSBF consume credited difficulty after each handler's adjustment.
An inflated candidate can distort a PROP round or occupy an excessive PPLNS
window; a finder bonus remains governed by PPLNSBF's separate percentage.
SOLO does not allocate a found reward by accumulated share weight, though displayed
work/effort can still have been overstated.

**PPS remains restricted to Bitcoin and Bitcoin BLAKE2b.** The affected families
have no supported PPS accepting contract. This change does not enable PPS for
them. Both new liability evidence and recovery with a known non-Bitcoin template
must continue to fail closed. Do not infer PPS support from a generic scheme enum.

Ethereum/Cortex settlement metadata now uses the submitted job's network difficulty,
so newer network statistics cannot reprice an older accepted proof.

The credit change happens before accepted-proof commitment and immutable share
persistence. Existing accounting IDs, payload hashes and receipt replay rules
carry the bounded value through restart/recovery. Replaying a receipt does not
reinsert statistical shares after a PROP round consumes them. Transaction failure
must roll back balance changes and consumed shares together. This patch does not
change payout-manager settlement leases or transaction ownership.

## Deployment and historical reconciliation

Before upgrading a public pool, record the deployed commit, pool family, payout
scheme, port/static/device floors, custom multiplier, network/target units,
relay origins, retention periods and any imported share contract. Historical
exposure cannot be established from these source call sites alone. An endpoint
above its candidate threshold is a sufficient configuration risk signal, but
actual liability depends on issued work, accepted proofs and deployed schemes.

Use the existing [read-only settlement audit](../scripts/release/audit-settlement-shares.sql)
and [Bitcoin historical audit guidance](bitcoin-minimum-difficulty.md) as screening
infrastructure. Convert each stored difficulty into the **same** units as its
recorded network difficulty first, using the table above and the exact deployed
handler/constants. For custom imports and old forks, reconstruct those equations
from the deployed source rather than applying a modern default factor.

A raw `shares.difficulty > shares.networkdifficulty` comparison is insufficient
for Kaspa, Alephium and Ergo. Ethereum stores full-work rather than Stratum units.
Warthog legacy score domains and non-unit Xelis/Zano deployments require separate
reconstruction. The stored shares table does not retain every proof, candidate
flag or old template target. Correlate candidate/block records, accounting IDs,
issued-work logs, relay journals, template archives and backups where available.
Retained statistics alone cannot prove exact historical overpayment; pruned
receipts, missing templates and changed floors limit reconstruction.

For each affected pool, produce a read-only report of suspect work, settlement
round/window effects, paid/unpaid balances, and uncertainty. A screening estimate
is not permission to debit miners. Freeze any proposed adjustments into a
separately reviewed migration with ledger snapshots, exact rounding rules,
rollback/reconciliation procedures and operator approval. This patch performs
**no** repricing, balance rewrite, deletion, liability migration or automatic
historical repair. Existing recovery evidence retains its recorded amounts.

## Validation

`NonBitcoinCandidateCreditTests` enumerates concrete production policies and
exercises immutable issued credit, weak/strong candidates, ordinary credit,
retired work, session closure and automatic bounds. Production ProgPoW, Kaspa and
Ethereum regressions cover real validator/mapping/duplicate boundaries with
controlled final hashes (accounting vectors, not consensus proofs). Existing
native CryptoNote, Cortex, Autolykos and captured physical Beam proof tests
continue to verify proof identity and ordinary credit.

`NonBitcoinCandidateSettlementTests` uses an isolated PostgreSQL schema to run
all concrete policies through receipt insertion/replay, settlement rollback,
PROP/PPLNS/PPLNSBF balance writes and post-settlement replay. It tests PPS denial
for every affected family. The documented Ubuntu WSL lab supplies PostgreSQL and
source-built native libraries. There are no documented live non-Bitcoin daemon
labs; no non-Bitcoin mainnet payout or external wallet submission is claimed.
The final PR records the executed build/test counts and any skips.
