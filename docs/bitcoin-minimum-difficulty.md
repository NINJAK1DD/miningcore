# Bitcoin minimum-difficulty configuration

Issue [#211](https://github.com/NINJAK1DD/miningcore/issues/211) completes the
canonical/merged Bitcoin configure contract. The assignment gate and publication
hook introduced by [#210](difficulty-assignment-grace.md) are retained; configure
input is now validated before negotiation and every requested extension receives
an explicit result.

The protocol reference is [BIP310](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki).
It defines the configure envelope, extension results and repeated minimum requests.
The following admission decisions retain Miningcore's static-difficulty policy:

| Request | Result and state |
| --- | --- |
| Finite positive minimum above the configured port difficulty | Accept, disable VarDiff and cancel deferred difficulty. Assign this exact requested difficulty. |
| Same accepted minimum again | Return true and clear superseded pending work, without retiring the assignment or publishing another job. |
| Minimum at/below the configured port difficulty, including zero | Return false without mutation. Zero does not restore VarDiff; reconnect to restore configured dynamic difficulty. |
| Missing, malformed, nonfinite or unrepresentable minimum | Canonical/merged return false for the extension without mutation. BLAKE2b retains its stricter recoverable request-error/range-error policy. |
| Unknown extension | Return false, without publishing work for that extension. |
| Version rolling only | Retain the existing negotiated mask response, without republishing difficulty/work. |
| Malformed envelope or missing request ID | Send one recoverable protocol error before mutation. |

JSON numeric strings remain supported for firmware compatibility. They use
invariant floating-point syntax, so `"3.5"` means 3.5 in every locale. Boolean,
container and null values, grouping separators, NaN and infinities are rejected.
The envelope requires an array of nonempty extension strings and a parameter
object; ignored trailing parameters remain compatible. Duplicate extensions are
negotiated once. The reserved response field `version-rolling.mask` cannot be
requested as an extension because its result would collide with the negotiated
mask and leave worker/miner policy inconsistent.

Canonical/merged difficulty divided by the coin's share multiplier must be
finite, positive and at most Bitcoin's Diff1 target (a proof target of one).
Higher values cannot produce a usable 256-bit target and are refused before any
assignment mutation. BLAKE2b retains its network and compact/short-target bounds
from [its immutable-target policy](bitcoin-blake2b.md). Configured VarDiff
`MinDiff`/`MaxDiff` bound dynamic retargeting, not explicit static assignments:
an accepted minimum still disables VarDiff, as before. The configured port
difficulty remains the admission floor.

For a changed subscribed canonical or merged assignment, enqueue order is:

1. Configure response confirming acceptance.
2. `mining.set_difficulty` with the exact enforced value.
3. Usable replacement `mining.notify`, with `clean_jobs=false`.

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

## Validation

`BitcoinMinimumDifficultyTests` drives real newline-delimited TCP and the
production canonical/merged dispatchers with real job snapshots. It checks
ordering, usable work, duplicate/no-op/pre-subscription behavior, malformed
envelopes and values, reserved-field collisions, deferred-work cancellation,
broadcast serialization and terminal failures at all three enqueue stages.
`BitcoinBlake2bMinimumDifficultyTests` pins single publication and retained targets.
Existing VarDiff/static-authorization contention, publication closure and
accepted-credit tests exercise the shared gate and failure boundaries.

Required live evidence uses the [documented lab](vardiff-monotonic.md#regression-and-live-validation):
Bitcoin Core templates and real in-flight SHA256d proofs with PostgreSQL PPS
settlement, Litecoin/Dogecoin merged daemon fixtures, and the reviewed Knots
29.4.2 BLAKE2b TCP/proof/settlement fixtures. Enable all documented daemon and
PostgreSQL variables; skipped live fixtures do not count as passing evidence.
