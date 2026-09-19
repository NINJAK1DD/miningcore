# HighwayHash dispatch maintenance

This vendored HighwayHash subset is Apache-2.0 licensed; source files retain their
upstream notices. `highwayhash.sha256` records the reviewed dispatcher, CPU-query
provider and their headers, and the Linux driver verifies them before building Dero.

The vendored dispatcher now requires CPU XSAVE, OSXSAVE and XCR0 XMM/YMM state
before allowing AVX2/FMA. It retains SSE4.1 when XSAVE is unavailable. The local
change explicitly clears FMA together with AVX/AVX2 and keeps the existing local
include paths and supported-target interface.

The recommendation to submit the missing-OSXSAVE fix upstream was checked against
[Google HighwayHash at f8381f3331d9c56a9792f9b4a35f61c41108c39e](https://github.com/google/highwayhash/blob/f8381f3331d9c56a9792f9b4a35f61c41108c39e/highwayhash/instruction_sets.cc).
Upstream already checks both CPU and OS XSAVE and rejects AVX2 without XMM/YMM
state. No duplicate upstream bug-fix PR is needed. This vendored copy predates
that behavior; future updates must preserve the guard and its regression fixture.

`scripts/release/fixtures/highwayhash-osxsave.cpp` links the real dispatcher with a
controlled CPUID provider. Separate processes test missing OSXSAVE and missing
CPU XSAVE, avoiding the dispatcher's process-lifetime capability cache.
