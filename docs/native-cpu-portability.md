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
use `ARCH=default`, retaining upstream runtime dispatch and JIT behavior.

Optional instructions are confined to dispatched implementations. For example,
BLAKE3 includes AVX-512 assembly, RandomX includes its AVX2 Argon2 implementation,
and Dero builds HighwayHash's AVX2 translation unit with `-mavx2`. The latter's
dispatcher also requires OS XSAVE support before selecting AVX2. Searching a
whole shared object for AVX-512 instructions would reject legitimate, guarded
implementations and would not prove that ordinary entry points are portable.

## Regression gate

Install `qemu-user` and run against the actual built library directory:

```bash
bash scripts/release/test-linux-native-build-fail-fast.sh
bash scripts/release/test-native-cpu-portability.sh src/Miningcore/bin/Release/net10.0
```

The build-driver fixture advertises every CPU feature, including AVX-512, and
checks that the driver still supplies the fixed baseline. The native probe runs
RandomX and RandomARQ known-answer hashes, Panthera and SCash cross-CPU hash
comparisons, CryptoNote integrated-address decoding, GhostRider, Argon2d250,
BLAKE3 and HighwayHash vectors. It runs on the host and under QEMU's
`Nehalem-v1,+aes,+pclmulqdq` model, without AVX, AVX2 or AVX-512. A separate
HighwayHash run uses `Haswell-v1,-xsave` to test disabled OS XSAVE support
(AVX2 emulation requires QEMU 7.2 or newer; Ubuntu 22.04's QEMU 6.2 additionally
masks AVX2). An AVX-512 negative control must terminate with SIGILL; otherwise
the gate fails. Crashes, timeouts, missing symbols/libraries and differing hashes
also fail the gate.

Both the normal Linux CI build and each Ubuntu release-package build run this
gate before the complete managed test suite. Emulation complements the real
AVX2 compatibility lab; it does not substitute for all full-suite testing or
prove coverage of every native instruction path.

## Issue #188 investigation

The reproduction used the documented Ubuntu 22.04 WSL2 compatibility lab on an
AMD Ryzen 9 5950X (family 25, model 33), with AVX2, AES and PCLMUL but no AVX-512.
The kernel was `6.18.33.2-microsoft-standard-WSL2`; GCC was Ubuntu 11.4.0 and GDB
12.1. The original artifact came from [Release run 35376490779](https://github.com/NINJAK1DD/miningcore/actions/runs/35376490779),
artifact ID `10561065505`, for source
`42d9edcebebdd84596508a6d429b49537d65ee9c`.

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

### References

- [GCC x86 options](https://gcc.gnu.org/onlinedocs/gcc/x86-Options.html):
  `-march` authorizes instructions for a target; `native` inherits the builder.
- [Intel instruction-set reference](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html):
  `VMOVDQA64` feature and encoding requirements.
- [Pinned RandomX build rules](https://github.com/tevador/RandomX/blob/102f8acf90a7649ada410de5499a7ec62e49e1da/CMakeLists.txt):
  default versus native builds and per-source optimized Argon2 compilation.
- [HighwayHash vectors](https://github.com/google/highwayhash/blob/master/highwayhash/highwayhash_test.cc):
  independent expected output for the Dero dispatch probe.
