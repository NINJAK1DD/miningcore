#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include "highwayhash/instruction_sets.h"

static uint32_t features, xcr0, reads;
static bool avx2;

namespace highwayhash {
// Interpose only the hardware observations, leaving the real dispatch body intact.
uint32_t ReadXCR0() { ++reads; return xcr0; }
void Cpuid(uint32_t level, uint32_t, uint32_t* abcd)
{
    abcd[0] = abcd[1] = abcd[2] = abcd[3] = 0;
    switch(level) {
    case 0: abcd[0] = 7; break;
    case 1:
        abcd[2] = (1U << 0) | (1U << 9) | (1U << 12) | (1U << 19) | (1U << 20) | features;
        abcd[3] = (1U << 25) | (1U << 26);
        break;
    case 7: abcd[1] = (1U << 3) | (1U << 8) | (avx2 ? (1U << 5) : 0); break;
    case 0x80000001U: abcd[2] = 1U << 5; break;
    }
}
}

int main(int argc, char** argv)
{
    const uint32_t xsave = 1U << 26, osxsave = 1U << 27, avx = 1U << 28;
    const uint32_t all = xsave | osxsave | avx;
    struct Case { const char* name; uint32_t flags, state; bool avx2, expected; uint32_t reads; };
    const Case cases[] = {
        {"no OSXSAVE", xsave | avx, 6, true, false, 0},
        {"no XSAVE", osxsave | avx, 6, true, false, 0},
        {"no AVX", xsave | osxsave, 6, true, false, 1},
        {"no XMM or YMM", all, 0, true, false, 1},
        {"no YMM", all, 2, true, false, 1},
        {"no XMM", all, 4, true, false, 1},
        {"no CPU AVX2", all, 6, false, false, 1},
        {"AVX2 enabled", all, 6, true, true, 1},
        {"additional XCR0 state", all, 0xe6, true, true, 1},
    };
    if(argc != 2 || argv[1][0] < '0' || argv[1][0] > '8' || argv[1][1] != '\0') return 2;
    const auto& test = cases[argv[1][0] - '0'];
    features = test.flags; xcr0 = test.state; avx2 = test.avx2;
    const auto supported = highwayhash::InstructionSets::Supported();
    if(((supported & HH_TARGET_AVX2) != 0) != test.expected ||
        (supported & HH_TARGET_SSE41) == 0 || reads != test.reads) {
        std::fprintf(stderr, "HighwayHash dispatch failed: %s (targets=%u, XCR0 reads=%u)\n",
            test.name, static_cast<unsigned>(supported), reads);
        return 1;
    }
    std::printf("HighwayHash CPU/OS-state case passed: %s\n", test.name);
}
