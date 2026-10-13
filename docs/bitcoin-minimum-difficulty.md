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
telemetry once per changed assignment/chain limit. Lower port/static/device
floors before operating a pooled reward scheme in that state. VarDiff cannot
raise difficulty above the current chain limit; an incompatible configured
minimum is not silently weakened and can prevent automatic retargeting.

PPLNS and PPLNSBF bound finite legacy ratios before decimal conversion. PROP
rescales all round weights together rather than capping individual shares,
preserving relative allocations even when the quotient overflows or underflows
a double. Relative weights below decimal precision round to zero. All three
keep diagnostic totals as doubles with bounded unit formatting. These numeric
protections do not determine whether old financial credits were legitimate.
Already persisted liabilities are not rewritten; deployments
that accepted shares on older code must audit historical credits before stable rollout.

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
