# Xelis difficulty target correction (#158)

## Reviewed upstream contract

The reviewed daemon **and bundled official miner** are Xelis v1.25.0, commit
[`db59b5c246ab0e40386d6c241dcd77f0aea84b10`](https://github.com/xelis-project/xelis-blockchain/tree/db59b5c246ab0e40386d6c241dcd77f0aea84b10).
The following source files define the contract:

- [`xelis_common/src/difficulty.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/difficulty.rs):
  `floor(U256::MAX / difficulty)`; a zero divisor is invalid. Hashes are unsigned
  big-endian integers, and equality with the target is accepted (`hash <= target`).
- [`xelis_common/src/varuint.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/varuint.rs):
  difficulty is an unsigned 256-bit integer, serialized in JSON as a decimal string.
- [`xelis_miner/src/main.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_miner/src/main.rs):
  the maintained miner calls those same target-construction and comparison functions.
- [`xelis_common/src/crypto/hash.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/crypto/hash.rs):
  v1 copies work into a private, zero-filled 200-byte aligned state before hashing.

Miningcore now preserves RPC difficulty as `BigInteger` and divides before
serializing the positive target to exactly 32 bytes, in big-endian order with
leading zero padding. Modern decimal strings and legacy JSON integer tokens
are accepted. Missing miner-work difficulty, zero, negative, fractional,
floating-point/exponent, nonnumeric, and values above `2^256-1` fail deliberately.
Decimal strings are limited to 78 ASCII digits before parsing. The legacy
`double` helper accepts only integer values from 1 through `2^53-1`; callers
needing the full daemon range must use the exact integer overload.

## Operator compatibility

Difficulty 1 produces `ff` repeated 32 times. Difficulty 2 produces `7f` followed
by 31 `ff` bytes. The 31/32-byte transition is between difficulties 255 and 256;
257 produces the asymmetric target `00ff` repeated 16 times. The former signed
little-endian conversion either produced zero for full-width/sign-extended
targets or reversed the significant bytes of shorter targets. The former
multiply/divide-by-255 approximation also disagreed with daemon integer rounding.
All of these are corrected, so block-candidate classification and its existing
target-derived credit ceiling can change to match the daemon. Ordinary worker
difficulty, share admission tolerance, stored-share units and payout formulas
retain their existing policies. Historical shares are not rewritten.

No configuration or database migration is required. Restart Miningcore to
replace cached jobs. Monitor accepted blocks and low-difficulty/rejected-block
rates after deploying. If rollback is necessary, stop the affected pool and
restore the previous application build; reverting also restores the defective
target construction, so investigate before resuming mining.

The live-test work also exposed a prerequisite memory-safety defect in the v1
managed wrapper: its native function mutates 200 bytes regardless of the supplied
length, while the wrapper passed 112-byte MinerWork directly. The wrapper now
copies into a private aligned 200-byte state and rejects larger inputs. This
prevents out-of-bounds access and mutation of caller work, including the work
used later for block identity. Independent upstream zero-input and text vectors
are pinned to [`xelis-hash` commit `f48262b931438fdbf2932711613e020c02f1ea04`](https://github.com/xelis-project/xelis-hash/blob/f48262b931438fdbf2932711613e020c02f1ea04/src/v1.rs).
They verify native output, padding, caller immutability and repeatability. No
native algorithm implementation or v2 hashing behavior is changed.

This is a focused child correction of [#120](https://github.com/NINJAK1DD/miningcore/issues/120).
It does **not** certify current-mainnet Xelis support: this runtime has v1/v2
hashers, while maintained Xelis also has v3 and subsequent protocol upgrades.
Algorithm modernization, payout certification, and a production soak remain
separate #120 work. The live fixture below uses isolated **stagenet height 1**
(`xel/v1`, difficulty 30,000), with real PoW validation enabled.

## Reproducible validation

`XelisDifficultyTests` contains independently calculated fixed vectors for
difficulties 1, 2, 3, 128, 255, 256, 257, 1024, 71,135,336,520,
`2^53-1`, `2^53+1`, and `2^256-1`. Reproduce any vector independently with Python:

```python
difficulty = 71135336520
print((((1 << 256) - 1) // difficulty).to_bytes(32, 'big').hex())
```

Tests verify full-width RPC integers/strings, malformed inputs, exact job target
initialization, inclusive candidate boundaries, low-difficulty rejection,
ordinary share credit, candidate credit caps and duplicate rejection. The
controlled-digest tests substitute only hash results, leaving initialization,
issuance and accounting on their production paths. RPC consumer fixtures cover
difficulty 1 and integers above double's consecutive-integer range.

For live validation, use the documented Ubuntu WSL test lab with a separate
Linux test copy. Obtain the official v1.25.0
[`x86_64-unknown-linux-gnu.tar.gz`](https://github.com/xelis-project/xelis-blockchain/releases/download/v1.25.0/x86_64-unknown-linux-gnu.tar.gz)
and verify its SHA-256 against the upstream release checksums before extraction:

```text
SHA-256: 424ac65de320a835b4cbe2c2a8b9140b12e8bab86cf5ebda89ffee024947135e
```

From the Linux checkout, after a normal native build:

```bash
export MININGCORE_TEST_XELISD=/absolute/path/to/v1.25.0/xelis_daemon
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~XelisDifficultyTests|FullyQualifiedName~XelisDaemonTests|FullyQualifiedName~XelisMinerWork'
```

`XelisDaemonTests` creates a unique temporary data directory, binds RPC to a
dynamic loopback port, disables P2P, and uses a published test address without
wallet keys. It acquires real work, initializes a job, rejects weak proof,
searches at most two million nonces within five minutes, accepts and submits a
real candidate, checks daemon height/topoheight, rejects duplicate proof, signals
clean shutdown, and removes its data directory. No simulator or skipped-PoW
option is used. With the environment variable absent, the fixture explicitly
skips; a skipped fixture is not live validation evidence.

## Observed lab results, 25 October 2026

Ubuntu WSL lab, native libraries built from source, official checksum-verified
v1.25.0 daemon:

| Check | Result |
| --- | --- |
| Managed build | Zero warnings and zero errors |
| Complete native build | Completed; no compiler warnings found in the build log |
| Focused Xelis target, RPC, buffer and live-daemon tests | **82 passed, zero failed, zero skipped** |
| Live scenario | Real difficulty-30,000 v1 proof accepted by stagenet; weak and duplicate proofs rejected; daemon shut down cleanly |
| Documentation links | 47 Markdown files validated |
| Full lab suite with coin daemons, PostgreSQL and Xelis enabled | 5,106 passed, 20 failed, 16 skipped |
| Clean `dev` baseline at `28f358825` | All 20 failing tests reproduced in a separate checkout (4 + 7 + 9 test runs) |

The complete suite is **not green**. The baseline failures concern existing
PostgreSQL/PPS migration fixtures: the migration requires a separate
administrator identity and some ownership/role fixtures lack privileges;
transaction cleanup can mask the original error with SQLSTATE `25P02`.
No Xelis test failed. The accounting/migration source and these baseline tests
are unchanged by this correction. Results are recorded as a regression limit,
not waived or represented as successful financial validation.

The first live attempt exposed the v1 buffer defect described above and aborted
the test host. After correcting the wrapper, both the focused run and the full
suite's live Xelis fixture passed with actual PoW verification enabled.
