#include <cstdint>
#include <cstdio>
#include "highwayhash/instruction_sets.h"

namespace highwayhash {
// Supply CPU capability without OS support. Link against the actual dispatch
// source, replacing only CPUID, so this also works on amd64v3 system userspace.
void Cpuid(uint32_t level, uint32_t, uint32_t* abcd)
{
    abcd[0] = abcd[1] = abcd[2] = abcd[3] = 0;
    switch(level) {
    case 0: abcd[0] = 7; break;
    case 1:
        // SSE3, SSSE3, FMA, SSE4.1, SSE4.2, AVX; intentionally no OSXSAVE.
        abcd[2] = (1U << 0) | (1U << 9) | (1U << 12) | (1U << 19) | (1U << 20) | (1U << 28);
        abcd[3] = (1U << 25) | (1U << 26);
        break;
    case 7: abcd[1] = (1U << 3) | (1U << 5) | (1U << 8); break; // BMI, AVX2, BMI2
    case 0x80000001U: abcd[2] = 1U << 5; break; // LZCNT
    }
}
}

int main()
{
    const auto supported = highwayhash::InstructionSets::Supported();
    if((supported & HH_TARGET_AVX2) != 0 || (supported & HH_TARGET_SSE41) == 0) {
        std::fputs("HighwayHash must retain SSE4.1 and reject AVX2 without OS XSAVE\n", stderr);
        return 1;
    }
    std::puts("HighwayHash rejects AVX2 without OS XSAVE support");
}
