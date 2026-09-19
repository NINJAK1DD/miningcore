# Linux native CPU portability

Linux x64 native libraries built by `src/Miningcore/build-libs-linux.sh` target
**x86-64-v2 plus AES-NI and PCLMULQDQ**, with generic tuning. This includes SSE3,
SSSE3, SSE4.1, SSE4.2, POPCNT, CMPXCHG16B and LAHF/SAHF. It does not impose an
unconditional AVX, AVX2 or AVX-512 requirement. The operating system and .NET
runtime have their own support requirements. Windows prebuilt libraries are
outside this Linux build contract.

Use the documented source-build driver or release archives. Do not copy native
libraries from a build with different compiler flags, Ubuntu/glibc versions or
source revisions. Direct component Makefile invocations with custom flags are
developer builds, not verified portable release builds. CPU portability does
not make the Ubuntu 26.04 archive usable on Ubuntu 22.04.

The driver passes `-march=x86-64-v2 -mtune=generic -maes -mpclmul` to the native
Makefile components and external CMake builds. It does not inspect the builder's
CPU to choose the distributed instruction set. The CryptoNote, Zano, Cortex and
Dero Makefiles no longer append `-march=native`. All four RandomX-family builds
use `ARCH=default`, retaining runtime dispatch and JIT behavior. A shared patch,
verified against the SHA-256 of each fork's identical pinned `cpu.cpp`, additionally
requires CPU AVX/XSAVE, OSXSAVE and XCR0 XMM/YMM state before AVX2 Argon2 selection.
The upstream CPUID-only detector was insufficient on an OS that disables XSAVE.

Optional instructions are confined to dispatched implementations. For example,
BLAKE3 includes AVX-512 assembly, RandomX includes its AVX2 Argon2 implementation,
and Dero builds HighwayHash's AVX2 translation unit with `-mavx2`, with an explicit
`-msse4.1` on its SSE4.1 unit. Both implementations are linked even on non-AVX2
builders, avoiding unresolved dispatch references. Its dispatcher also requires
CPU/OS XSAVE and XMM/YMM state before selecting AVX2. The reviewed vendored sources
are hash-verified; [maintenance notes](../src/Native/libdero/HIGHWAYHASH.md) record
the corresponding upstream fix, which already exists and needs no duplicate PR.
Searching a whole shared object for AVX-512 instructions would reject legitimate, guarded
implementations and would not prove that ordinary entry points are portable.

CryptoNight's vendored Argon2 is different: `impl-select.c` initializes the selected
implementation to `fill_segment_default`, its automatic selector is disabled with
`#if 0`, and Miningcore does not call the manual by-name selector. The fixed build
leaves the AVX2/AVX-512 translation units as stubs; it does not remove an active
optimized dispatch path or force a new SSSE3 fallback. The Chukwa probe checks the
selected implementation and a known hash on host and baseline CPUs. Enabling those
dormant implementations would require separate selector, thread-safety and vector
validation; it is not part of this portability correction.

Every release archive's `BUILD-INFO` records the declared native CPU baseline beside
the source commit, build image and target. Build-policy checks guard that metadata
against drift from the driver and inspect every top-level native Makefile for
host-specific or unreviewed general AVX flags.

## Regression gate

Install `qemu-user` and run against the actual built library directory:

```bash
bash scripts/release/test-linux-native-build-fail-fast.sh
python3 scripts/release/test-native-cpu-policy.py --self-test
python3 scripts/release/test-native-cpu-policy.py
bash scripts/release/test-randomx-cpu-os-state.sh "${TMPDIR:-/tmp}"
bash scripts/release/test-native-cpu-portability.sh src/Miningcore/bin/Release/net10.0
```

The build-driver fixture fails on any attempt to probe the builder CPU and checks
the fixed baseline on the initial component invocation. The static policy guard
also examines all 25 top-level native Makefiles, allowing reviewed optional flags
only on their specific translation units. The native probe runs
RandomX and RandomARQ known-answer hashes, Panthera and SCash cross-CPU hash
comparisons, CryptoNote integrated-address decoding, GhostRider, Chukwa, Argon2d250,
BLAKE3 and HighwayHash vectors. It also executes Cortex header hashing/SipHash and
duplicate-edge rejection, and all four Zano exports with a malformed block. These
last checks cover rejection paths, not successful Zano consensus hashing or valid
Cortex proofs. In total the probe directly executes ten libraries, including all
four whose Makefiles previously specified `-march=native`. It runs on the host and under QEMU's
`Nehalem-v1,+aes,+pclmulqdq` model, without AVX, AVX2 or AVX-512. The hosted
Ubuntu 26.04 development runner uses `amd64v3` system-library packages, which
cannot start on a pre-AVX CPU; that job explicitly selects `--avx2-userspace`
(`Haswell-v1`, still without AVX-512). The required Ubuntu 24.04 source-build lane
now runs the strict baseline on every CI push and PR, including direct `dev` pushes;
both release containers and the lab also keep that strict gate. There is no silent
fallback from strict testing to the weaker model. A separate fixture compiles the real HighwayHash
dispatcher with a controlled CPUID provider advertising AVX2 without CPU or OS XSAVE,
and asserts that it selects SSE4.1 instead. An AVX-512 negative control (and an
AVX2 `vpbroadcastd` negative control on the strict baseline) must terminate with
SIGILL; otherwise the gate fails. These controls check specific instructions,
not the accuracy of every opcode in the emulator. Crashes, timeouts, missing symbols/libraries and differing hashes
also fail the gate.

The RandomX CPU-state fixture runs nine cases against each fork's actual patched
`Cpu` implementation: missing CPU XSAVE, OSXSAVE or AVX; missing XMM/YMM state;
missing AVX2; and enabled optimized dispatch. It verifies that XGETBV is never
queried without the required CPU/OS capability. ELF interposition replaces only
the CPUID/XCR0 observations; the constructor's decision logic is unchanged. This
avoids QEMU versions that cannot advertise the inconsistent AVX2/OSXSAVE combination.
It uses the four source directories retained by the driver under `TMPDIR`; artifact-only
validation can run the native portability probe without those source directories.
Native execution is bounded to five minutes and emulated execution to ten minutes,
allowing for all four RandomX cache initializations on slower runners.

Both the normal Linux CI build and each Ubuntu release-package build run this
gate before the complete managed test suite. Emulation complements the real
AVX2 compatibility lab; it does not substitute for all full-suite testing or
prove coverage of every native instruction path.

## Issue #188 investigation

The reproduction used the documented Ubuntu 22.04 WSL2 compatibility lab on an
AMD Ryzen 9 5950X (family 25, model 33), with AVX2, AES and PCLMUL but no AVX-512.
The kernel was `6.18.33.2-microsoft-standard-WSL2`; GCC was Ubuntu 11.4.0 and GDB
12.1. The original artifact came from [Release run 35376490779](https://github.com/NINJAK1DD/miningcore/actions/runs/35376490779),
artifact ID `10561065505`, for PR head
`42d9edcebebdd84596508a6d429b49537d65ee9c`. Its embedded `BUILD-INFO` identifies
merge commit `9d803eaa0bddd4ec6203dd77d9ac57d6c2488663`, whose verified parents
are that PR head and base `55a7cf044de358ad4bd813a36bf56fcdaf1a28cf`.

Archive: `miningcore-v0.0.0-ci.35376490779.1-linux-x64-ubuntu-22.04.tar.gz`.
SHA-256: `1c44f2c97f93aa312c21489c5c87cbf2dc0e4eefbf084bfda71a345047528654`.
All 25 native files used for reproduction matched the archive byte-for-byte.

| Original library | SHA-256 |
| --- | --- |
| `librandomx.so` | SHA-256: `824558084a89546cc53e7db1f2a660f9ac38dfd7cfc2e1918f8222e2d512e7d5` |
| `librandomarq.so` | SHA-256: `6f3463b9b43fe91b558133b189bdf2356a985c17347fd46b405b56add4d5ca6c` |

Isolated managed tests for RandomX seed creation, RandomARQ slow hashing,
GhostRider and integrated-address decoding each aborted. A small native harness
calling `randomx_get_flags`, `randomx_alloc_cache` and `randomx_init_cache`
established the precise RandomX/RandomARQ fault under GDB:

```text
SIGILL: randomx_blake2b_init_param + 121
vmovdqa64 ...(%rip), %zmm0
RandomX instruction bytes: 62 f1 fd 48 6f 05 1d 76 00 00

randomx_blake2b_init_param
randomx_blake2b_init
rxa2_initial_hash
randomx_argon2_initialize
randomx::initCache
randomx::initCacheCompile
randomx_init_cache
```

The 512-bit `VMOVDQA64` instruction requires **AVX-512F**. The release logs show
global `-mavx512f`/`HAVE_AVX512F` for Makefile components and `-march=native` for
RandomX. These settings allow unsupported instructions in ordinary code before
runtime dispatch can help. The test-host blame list alone did not establish
which test caused a parallel run to abort; the isolated/native reproductions do.

### Before/after PR #186 comparison

No release run was available for exact base
`55a7cf044de358ad4bd813a36bf56fcdaf1a28cf`. Equivalent targeted RandomX builds
were therefore made from that base and PR revision
`42d9edcebebdd84596508a6d429b49537d65ee9c`, using each revision's pinned patch
and shared-library wrapper. Their native sources, pins and driver are identical.
Both used RandomX v1.2.1 commit `102f8acf90a7649ada410de5499a7ec62e49e1da`,
GCC 11.4.0, CMake Release, `ARCH=default`, and identical explicit C/C++ flags
`-march=skylake-avx512 -Wa,--noexecstack` (plus upstream `-maes` and per-file
Argon2 flags). This is a controlled AVX-512-target comparison, not a claim to
reconstruct the original runner's undisclosed `-march=native` expansion.

Both produced SHA-256: `0918a977031720410b736373cfe3db9dc98412b87ebe4c9e6a9548c55727c0a1` and both
crashed at `blake2b_compress + 435`, instruction `kmovd 0x1c(%r9), %k0`, on the
same Ryzen lab CPU. This byte-identical base/PR failure establishes that the
host-dependent native build requirement **predates PR #186**. The comparison
is deliberately scoped to the affected native library, not two passing full
release suites. Aborted test runs are failures, never full-suite validation.

### Fixed-build lab validation

All 25 native libraries were rebuilt with GCC 11.4.0 using the fixed driver.
The inventory/relocation audit verified all 241 managed native entry points.
The matching managed tests built with zero warnings; all **107 crypto tests
passed**, including both fast and slow RandomX/RandomARQ tests and the previously
crashing GhostRider and CryptoNote tests. For this Windows-mounted checkout only,
the managed test build disabled GitVersion metadata generation because its
worktree pointer uses a Windows path; this does not change native compilation.
The normal CI builds retain GitVersion and execute the full managed suite.

The complete lab test run also finished successfully: **3,284 passed, 48
skipped, 0 failed** (3,332 total). The skipped integration cases require their
separately configured services/daemons; they are not counted as live validation.
The native probe passed on the actual Ryzen and the emulated baseline without
AVX/AVX2/AVX-512, with identical hashes. Its negative control rejected AVX-512,
and the original released libraries failed the new gate as expected.

### Review hardening validation (2026-09-19)

After the review changes, all 25 native libraries were rebuilt in the same Ubuntu
22.04 compatibility lab and passed the 241-entry-point inventory/relocation audit.
The expanded ten-library probe passed on the physical Ryzen CPU and strict
emulated baseline with identical results, including Chukwa, Cortex and Zano.
Both AVX2 and AVX-512 negative controls terminated with SIGILL. The complete
managed suite using these rebuilt libraries passed **3,284 tests, with 48 skipped
and zero failures** (3,332 total).

All 36 RandomX CPU/OS-state cases passed against the four patched source trees;
the same fixture rejected the unpatched detector for missing OSXSAVE. The extended
HighwayHash fixture rejected the previous dispatcher for missing CPU XSAVE and
passed with the current guard. Build failure propagation, all-Makefile policy,
release packaging/metadata, shell lint, workflow pins and documentation links were
also checked. This paragraph describes the local rebuild; the following artifact
evidence is historical and identifies the exact earlier revision it validated.

### CI-built artifact validation

[Release run 35404072578](https://github.com/NINJAK1DD/miningcore/actions/runs/35404072578)
built PR head `f1b8f436b8e13d46df76bf50abc221deebee3dd2` as merge commit
`69c64b0322f6b1bfa0dfdb9070185767d29b2ed0` with base
`4c530abc110eb8c3820dbed0e47b643ce02b8fdb`. Both Ubuntu package jobs passed
the portability gate and 3,300 tests each. The [normal hosted CI run](https://github.com/NINJAK1DD/miningcore/actions/runs/35404072609)
passed its AVX2-only portability gate and 3,357 tests, with one skip.

The Ubuntu 22.04 artifact ID is `10572032386`. Its downloaded ZIP matched the
GitHub artifact SHA-256: `457a8c5ff65de9d9558e8bbb6b9d0fb4cb74b66fe973414cbd2bb80ec3b020a5`.
The contained release archive SHA-256 is `359e4e911250a1f0cc5e3bbdda3ce3eefbdb895c4416df6973d25ee627d11ca2`.
All 25 CI-built libraries passed the inventory/relocation audit. The eight libraries
and entry points covered by that revision's portability probe passed native and
emulated-baseline execution on the original compatibility lab. With those exact
libraries, the complete lab suite passed 3,284 tests with 48 skips.

One preceding complete artifact run had a single `Address already in use`
failure in `RunAsync_BannedClientRejection_AllowsImmediateExclusiveRestart`
(3,283 passed, one failed, 48 skipped). All crypto tests passed. Five isolated
repetitions of that socket test and one complete rerun then passed. This
intermittent Stratum failure is recorded separately; the failed run is not
counted as successful validation, and this PR changes no Stratum code.

### References

- [GCC x86 options](https://gcc.gnu.org/onlinedocs/gcc/x86-Options.html):
  `-march` authorizes instructions for a target; `native` inherits the builder.
- [Intel instruction-set reference](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html):
  `VMOVDQA64` feature and encoding requirements.
- [Pinned RandomX build rules](https://github.com/tevador/RandomX/blob/102f8acf90a7649ada410de5499a7ec62e49e1da/CMakeLists.txt):
  default versus native builds and per-source optimized Argon2 compilation.
- [HighwayHash vectors](https://github.com/google/highwayhash/blob/master/highwayhash/highwayhash_test.cc):
  independent expected output for the Dero dispatch probe.
