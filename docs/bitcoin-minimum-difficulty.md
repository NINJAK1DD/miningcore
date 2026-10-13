# Bitcoin minimum-difficulty configuration

Issue [#211](https://github.com/NINJAK1DD/miningcore/issues/211) completes the
canonical/merged Bitcoin configure contract. The assignment gate and publication
hook introduced by [#210](difficulty-assignment-grace.md) are retained; configure
input is now validated before negotiation and every requested extension receives
an explicit result.

The protocol reference is [BIP310](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki).
It defines the configure envelope, extension results and repeated minimum requests.
The following admission decisions enforce the device floor:

| Request | Result and state |
| --- | --- |
| Finite positive minimum at/above the configured port difficulty | Accept and retain the session minimum; cancel deferred work. Disable VarDiff only if this raises the current assignment. |
| Minimum equal to the port/current difficulty, or below a current dynamic assignment | Keep VarDiff running above the retained floor; preserve the current assignment without publishing duplicate work. |
| Same accepted minimum again | Return true and clear superseded pending work, without retiring the assignment or publishing another job. |
| Minimum below the configured port difficulty | Return an extension error string without mutation. |
| Zero | Return an explicit unsupported-disable error string. Reconnect to clear the session minimum and restore configured VarDiff. |
| Missing, malformed, nonfinite or unrepresentable minimum | Return an extension error string without mutation, including on BLAKE2b. |
| Suggestion below the negotiated minimum | Return a recoverable error without publishing or lowering the retained minimum. |
| NiceHash autodiff below the negotiated minimum | Preserve the retained floor; subscription announces difficulty and work at that floor. |
| Another valid minimum configure request | Explicitly replace the session minimum. Other difficulty producers cannot replace it. |
| Unknown extension | Return false, without publishing work for that extension. |
| Version rolling only | Retain the existing negotiated mask response, without republishing difficulty/work. |
| Malformed envelope or missing request ID | Send one recoverable protocol error before mutation. Valid-ID envelopes requesting minimum difficulty consume admission even when malformed. Missing IDs never consume difficulty admission. |
| Configure rate-limit refusal | Return a minimum-difficulty error string and an explicit result for each other extension, without negotiation or work publication. |

JSON numeric strings remain supported for firmware compatibility. They use
invariant floating-point syntax, so `"3.5"` means 3.5 in every locale. Boolean,
container and null values, grouping separators, NaN and infinities are rejected.
The envelope requires an array of nonempty extension strings and a parameter
object, with at most 32 extension entries; ignored trailing parameters remain compatible. Duplicate extensions are
negotiated once. The reserved response field `version-rolling.mask` cannot be
requested as an extension because its result would collide with the negotiated
mask and leave worker/miner policy inconsistent.

The worker context validates all assignment producers, including suggestions,
password static difficulty, NiceHash, explicit configure and deferred VarDiff.
Canonical/merged difficulty divided by the coin's share multiplier must be
finite, positive and at most Bitcoin's Diff1 target (a proof target of one).
Higher values cannot produce a usable 256-bit target and are refused before any
assignment mutation. **Diff1 is a target representability limit, not an economic
safety bound.** Candidate credit has a separate chain-target cap described below.
BLAKE2b retains its network and compact/short-target bounds
from [its immutable-target policy](bitcoin-blake2b.md). Configured VarDiff
`MinDiff`/`MaxDiff` bound dynamic retargeting. Automatic retargeting also stops at
the easiest current parent/auxiliary chain difficulty times the share multiplier.
The configured port difficulty remains the inclusive admission floor. A minimum
that raises a dynamic assignment selects an exact static assignment and disables
VarDiff; a minimum already satisfied by the dynamic assignment retains VarDiff.
Explicit configuration may lower a previous static assignment down to that port floor.
The separately retained minimum constrains subsequent producers until another
accepted configure request or a new connection.

For a changed subscribed canonical or merged assignment, enqueue order is:

1. Configure response confirming acceptance.
2. `mining.set_difficulty` with the exact enforced value.
3. Usable replacement `mining.notify`, with `clean_jobs=false`, when work is authorized.

Direct SOLO sends the response and difficulty while subscribed but unauthorized,
and withholds the job until payout authorization succeeds. Authorized direct
jobs retain their immutable payout destination and authorization generation.

This entire transition owns the same worker gate as share/idle VarDiff,
static-difficulty authorization, subscription and broadcasts. No broadcast can
split a difficulty/job pair, and no deferred dynamic assignment can overwrite an
accepted static one. Before subscription, configuration responds and prepares the
assignment; subscription announces difficulty and issues work. Direct SOLO still
withholds work until payout authorization succeeds.

Any response, difficulty or job enqueue/construction failure, including
cancellation after assignment starts, closes the assignment session and job
registry before releasing the gate. Abortive disconnect can discard queued bytes;
enqueue success does not prove delivery. No second response is attempted and
buffered requests cannot revive the connection. Cancellation before gate entry
does not commit an assignment.

Retained jobs preserve [original proof eligibility and accounting credit](difficulty-assignment-grace.md).
Canonical/merged in-flight work uses the existing bounded monotonic grace.
BLAKE2b keeps its immutable target, notification count, terminal admission latch
and accepted-proof ownership. Configure does not retry or reprice accepted shares.

## Candidate credit

A candidate may be admitted below the assigned share target so a real block is
not discarded. Its credit is now `min(issued difficulty, qualifying chain
difficulty × share multiplier)` before accounting normalization. Merged work uses
the easiest qualifying parent/auxiliary target. This cap applies to **all**
candidates, including proofs exceeding the assigned target: capping only the
exception path or using actual proof difficulty still rewards selective submission.
Ordinary proofs retain issued credit and the existing grace policy. Candidate
ownership, duplicate identity, settlement and original credit after acceptance
remain unchanged by later difficulty or publication failures.

This closes the candidate-only PPS/PPLNS inflation path across configure,
suggestion, password and NiceHash assignments. Explicit assignments above the
easiest chain difficulty remain representable, but honest miners can receive
less credit than their actual work and firmware can discard candidates below
the assigned target. The pool emits a warning and `assignment-above-network`
telemetry once per changed oversized assignment difficulty, rather than on every
chain-target movement. Lower port/static/device
floors before operating a pooled reward scheme in that state. VarDiff cannot
raise difficulty above the current chain limit; an incompatible configured
minimum is not silently weakened and can prevent automatic retargeting.
On a broadcast, compatible dynamic assignments (including queued work) are
lowered to a falling chain ceiling before `set_difficulty` and `notify`, even
when the miner's share rate is inside the target-time variance band. Static
assignments and negotiated/configured minima remain enforced. A port `MinDiff`
above the chain ceiling emits one warning and `vardiff-floor-above-network`
telemetry per incompatibility episode per port; it is not silently lowered.
On testnet4 minimum-difficulty templates, compatible VarDiff miners can drop to
difficulty one. When ordinary templates return, difficulty rises under normal
retarget timing and `MaxDelta`, rather than immediately restoring the earlier
assignment. Expect temporarily increased share/block rates; a higher port floor
instead pauses retargeting and warns. Monitor this during commissioning.

PPLNS and PPLNSBF bound finite legacy ratios before decimal conversion. PROP
rescales all round weights together rather than capping individual shares,
preserving relative allocations even when the quotient overflows or underflows
a double. Relative weights below decimal precision round to zero. All three
keep diagnostic totals as doubles with bounded unit formatting. These numeric
protections do not determine whether old financial credits were legitimate.
Already persisted liabilities are not rewritten; deployments
that accepted shares on older code must audit historical credits before stable rollout.

## Pre-upgrade settlement audit — all coin families

The shared PROP, PPLNS and PPLNSBF calculations require **finite positive stored,
adjusted and network difficulty**, for every coin family. A corrupt legacy row
or an `AdjustShareDifficulty` override returning zero stops that pool's settlement
cycle; later blocks stay unpaid until the cause is reconciled. Previously
unvalidated zero/negative legacy share rows now fail closed. No row is silently
skipped or deleted, and this policy also applies to non-Bitcoin pools.

Before upgrading to v0.3.0, back up retained shares and financial receipts, then
run the [read-only audit](../scripts/release/audit-settlement-shares.sql) against
each deployment database with a role allowed to read both `shares` and
`pps_share_credits`. Both tables and their accounting identity/role columns must
exist; a missing schema makes the audit
incomplete and the script fails rather than reporting a clean empty ledger.
Use the [versioned database upgrade procedure](database.md#upgrade-an-existing-database)
or an appropriately restored backup, not a newly created empty receipt table.

```sh
psql "$MININGCORE_AUDIT_DSN" --set=ON_ERROR_STOP=1 --file=scripts/release/audit-settlement-shares.sql
```

The script uses a repeatable-read, read-only transaction. On large deployments,
run it on a suitably current replica or during a quiet maintenance period: its
sequential scans hold a snapshot until completion and can delay vacuum cleanup.
Record the replica's replay position/lag when interpreting its coverage; a lagging
replica cannot clear newer primary receipts. See PostgreSQL's
[vacuum guidance](https://www.postgresql.org/docs/current/routine-vacuuming.html).
Its invalid-share summary
uses this predicate, with explicit special-value checks:

```sql
SELECT poolid, count(*) AS invalid_rows
FROM shares
WHERE difficulty IS NULL OR networkdifficulty IS NULL
   OR difficulty <= 0 OR networkdifficulty <= 0
   OR difficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR networkdifficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
GROUP BY poolid;
```

PostgreSQL treats `NaN > 0` as true and considers NaNs equal, so positivity alone
cannot validate these rows; see its [floating-point special-value rules](https://www.postgresql.org/docs/current/datatype-numeric.html#DATATYPE-FLOAT).
The script also returns bounded identifying samples, checks invalid PPS receipt
difficulties and monetary amounts, and reports positive finite raw difficulty
above recorded network difficulty without dividing potentially overflowing
values. PPS receipt counts and the `oversized_calculated_amount` and
`oversized_credited_amount` sums cover **only flagged oversized receipts**, not
all retained PPS liabilities or credits. They are independent of retained
statistical shares. These are historical receipt totals, not the current unpaid
balance or proven overpayment. Invalid receipts are excluded from those
monetary sums and reported separately, so a numeric NaN cannot poison a total.
Oversized rows require economic review, not automatic
deletion: one inflated legacy share can dominate a PROP round. An empty invalid
summary does not establish that historical credits or payouts were legitimate.
The stored-value audit cannot detect an adjustment override rounding otherwise
valid work to zero; review each deployed handler's adjustment policy as well.

The direct PPS report also provides `estimated_oversized_excess`: for each flagged
receipt, `calculatedamount * (difficulty - networkdifficulty) / difficulty`,
summed using numeric arithmetic and rounded to the receipt's 24-decimal precision.
Under a linear PPS contract this estimates the
calculated amount above a cap at that row's recorded network difficulty. It is
a screening estimate with conversion/rounding limits, not proven overpayment,
an exact migration amount or an upper bound for the complete ledger. Qualifying
auxiliary targets can imply a lower cap; historical reward bases, fees, carry,
applied credits and payouts still need reconciliation.

**Merged pools must be reviewed as paired accounting events in both directions.**
An auxiliary-only candidate can have `auxiliary network difficulty < credited
difficulty <= parent network difficulty`; a parent-only candidate can expose the
reverse case when the auxiliary chain is harder. An oversized projection makes
its paired peer suspect even though that peer's raw comparison is clean. The
share reports link `accountingid` across opposite parent role `2` and auxiliary
role `3`, returning `paired_nonoversized_rows` and a bounded identifying sample
including the flagged row's role. The PPS reports independently link receipts across different pools by
`accountingid`, returning `paired_nonoversized_receipts`, `paired_calculated_amount`,
`paired_credited_amount` and a bounded sample. PPS receipts do not store roles,
but either direction deserves review without a topology lookup. Deployment
configuration and archived roles still inform financial reconciliation.
Each nonoversized peer is counted once even if an import has several
matching oversized peers. Rows already oversized remain in the direct report.
The paired monetary sums are the full amounts of the flagged nonoversized peers,
not their estimated excess and not totals of all retained receipts.

Both sides must have valid inputs and common difficulty units for these raw
pairing reports. Invalid peers are reported separately, not used in sums.
Missing identities/roles, mismatched payout schemes, pruned counterparts or
missing archives make pairing coverage incomplete. Review both ledgers and the
configured pool relationships; an empty per-pool or paired report cannot clear
either pool when the corresponding evidence is unavailable. No accounting ID or
relationship is inferred from miner addresses or coincident timestamps.

**Raw oversized comparisons only apply to common difficulty units**, where
`AdjustShareDifficulty` is the identity. Pool IDs do not identify a coin family,
so the script cannot infer the required scale. Its invalid/nonfinite checks apply
to all families, but an empty `raw_oversized_rows` report cannot clear Alephium,
Ergo, Kaspa or custom adjustment handlers. Current integrated Bitcoin-family PPS
receipts use common units; imported/custom PPS contracts must verify this too.
The current handler factors are:

| Handler | Multiply stored share difficulty by |
| --- | --- |
| Alephium | `2^30 = 1073741824` |
| Ergo | `256` |
| Kaspa handler, symbol `SPR` | `2^7 × 1 = 128` |
| Kaspa handler, other symbols | `2^31 × 2 = 4294967296` |

Confirm the factors against the deployed version and any custom handler. For an
Ergo pool, a diagnostic comparison can use numeric multiplication to avoid an
overflowing binary64 quotient:

```sql
SELECT poolid, count(*) AS normalized_oversized_rows
FROM shares
WHERE poolid = 'your-ergo-pool'
  AND difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND difficulty::numeric * 256 > networkdifficulty::numeric
GROUP BY poolid;
```

This is a review flag, not replacement payout arithmetic; compare boundary cases
with the deployed handler and immutable receipts before reconciling anything.

PPS evidence retention is separate from statistical-share retention. The
[accounting retention policy](database.md#share-accounting-retention-and-sizing)
prunes both `pps_share_credits` **and PPS `balance_changes`**. Archive receipts,
changes and accounting groups before that horizon. If receipts have gone, retained
or archived `balance_changes` with `usage = 'PPS share credit'` can establish applied
credits, but do not reconstruct per-share difficulty or the full calculated
fractional liability by themselves. Use receipt archives/backups and remainder
evidence for reconciliation. If both receipts and changes were pruned without
archives, current balances and an empty audit cannot establish historical safety.

Invalid settlement logs and exceptions contain JSON-escaped pool/miner identity,
the UTC creation timestamp, share and settlement block heights, and stored,
adjusted and network difficulties. They omit IP/agent fields and exception
stacks. Use those fields to locate the row or adjustment failure, reconcile the
cause and historical liabilities under a reviewed plan, and retain an audit
trail before resuming payouts. This change supplies no automatic repair or
liability rewrite.

Configure, subscribe, suggestions, static authorization and direct job publication
share terminal assignment cleanup. A failure before a response attempt is logged
as `AssignmentMutationFailure`; a failure after a response attempt retains
`AssignmentPublicationFailure`. Logging never retries a response or changes proof ownership.

## Validation

`BitcoinMinimumDifficultyTests` drives real newline-delimited TCP and the
production canonical/merged dispatchers with real job snapshots. It checks
ordering, usable work, duplicate/no-op/pre-subscription behavior, malformed
envelopes and values, reserved-field collisions, deferred-work cancellation,
broadcast serialization and terminal failures at all three enqueue stages.
It also checks retained minima, explicit replacement, NiceHash overrides, direct
SOLO authorization generations, extension limits and admission-refusal response maps.
Malformed minimum envelopes exhaust admission and reach terminal refusal; port-equal
minima retain VarDiff, retargets use the current easiest chain ceiling, and
floor/ceiling no-ops do not republish work. Warnings and suppressed-failure labels
have regressions, as do extreme PROP/PPLNSBF allocations and quantity formatting.
Broadcast regressions cover falling parent/auxiliary/BLAKE2b ceilings, queued
assignments, incompatible device/port floors and bounded warning frequency.
The PostgreSQL audit fixture exercises zero, negative, NaN, both infinities and
nullable imported fields in shares and PPS receipts, invalid numeric amounts,
receipt-only liabilities, a nonidentity Ergo scale, linked parent/auxiliary
events, duplicate-peer protection, invalid/unrelated/same-pool/role-mismatched
exclusions, and numeric excess estimates including extreme ratios. Complete row
snapshots remain unchanged.
`BitcoinBlake2bMinimumDifficultyTests` pins single publication and retained targets.
Existing VarDiff/static-authorization contention, publication closure and
accepted-credit tests exercise the shared gate and failure boundaries.

Required live evidence uses the [documented lab](vardiff-monotonic.md#regression-and-live-validation):
Bitcoin Core templates and real in-flight SHA256d proofs with PostgreSQL PPS
settlement, Litecoin/Dogecoin merged daemon fixtures, and the reviewed Knots
29.4.2 BLAKE2b TCP/proof/settlement fixtures. Enable all documented daemon and
PostgreSQL variables; skipped live fixtures do not count as passing evidence.
`BitcoinCandidateCreditTests` exercises canonical and merged parent-only,
auxiliary-only and dual candidate boundaries with controlled hash outcomes.
`CandidateOnlyHighMinimum_RealBlockCannotInflatePpsCreditAndReplayIsIdempotent`
uses a genuine Bitcoin Core proof/block and PostgreSQL PPS accounting.
