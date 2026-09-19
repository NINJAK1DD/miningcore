# HighwayHash dispatch maintenance

This vendored HighwayHash subset is Apache-2.0 licensed; source files retain their
upstream notices. `highwayhash.sha256` records the reviewed dispatcher, CPU-query
provider and their headers, and the Linux driver verifies them before building Dero.

The vendored dispatcher requires CPU XSAVE, OSXSAVE and XCR0 XMM/YMM state
before selecting AVX2. It retains SSE4.1 when XSAVE is unavailable.

The recommendation to submit the missing-OSXSAVE fix upstream was checked against
[Google HighwayHash at f8381f3331d9c56a9792f9b4a35f61c41108c39e](https://github.com/google/highwayhash/blob/f8381f3331d9c56a9792f9b4a35f61c41108c39e/highwayhash/instruction_sets.cc).
The feature-selection body now matches that upstream revision verbatim. Local
differences are only the two include paths and moving `ReadXCR0` out of the
anonymous namespace so hardware observations can be interposed in the fixture.
The redundant local FMA clearing and comment changes have been removed. No
duplicate upstream bug-fix PR is needed.

`scripts/release/fixtures/highwayhash-osxsave.cpp` links the real dispatcher with a
controlled CPUID/XCR0 provider. Nine separate processes cover missing XSAVE,
OSXSAVE, AVX, XMM, YMM, both state bits or AVX2, plus two valid AVX2 cases. They
verify that XGETBV is never queried without CPU/OS XSAVE and avoid the dispatcher's
process-lifetime capability cache. Only the hardware observations are replaced;
the upstream feature-selection body is unchanged.
