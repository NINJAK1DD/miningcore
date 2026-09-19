// ELF interposition supplies only hardware observations. Cpu::Cpu is the real
// pinned source in a separately linked shared object, compiled without inlining.
#include "cpu.hpp"
#include <cstdio>

static int features;
static bool avx2;
static unsigned xcr0;
static unsigned reads;

void cpuid(int info[4], int level)
{
    info[0] = info[1] = info[2] = info[3] = 0;
    if(level == 0) info[0] = 7;
    if(level == 1) info[2] = features | (1 << 9) | (1 << 25); // SSSE3 + AES
    if(level == 7 && avx2) info[1] = 1 << 5;
}

namespace randomx {
unsigned readXcr0() { ++reads; return xcr0; }
}

int main()
{
    const int xsave = 1 << 26, osxsave = 1 << 27, avx = 1 << 28;
    const int all = xsave | osxsave | avx;
    struct Case { const char* name; int features; unsigned state; bool avx2, expected; unsigned reads; };
    const Case cases[] = {
        {"no OSXSAVE", xsave | avx, 6, true, false, 0},
        {"no XSAVE", osxsave | avx, 6, true, false, 0},
        {"no AVX", xsave | osxsave, 6, true, false, 0},
        {"no XMM or YMM", all, 0, true, false, 1},
        {"no YMM", all, 2, true, false, 1},
        {"no XMM", all, 4, true, false, 1},
        {"no CPU AVX2", all, 6, false, false, 1},
        {"AVX2 enabled", all, 6, true, true, 1},
        {"additional XCR0 state", all, 0xe6, true, true, 1},
    };
    for(const auto& test : cases) {
        features = test.features; xcr0 = test.state; avx2 = test.avx2; reads = 0;
        randomx::Cpu cpu;
        if(cpu.hasAvx2() != test.expected || reads != test.reads || !cpu.hasAes() || !cpu.hasSsse3()) {
            std::fprintf(stderr, "RandomX CPU dispatch failed: %s (AVX2=%d, XCR0 reads=%u)\n",
                test.name, cpu.hasAvx2(), reads);
            return 1;
        }
    }
    std::puts("RandomX CPU/OS-state cases passed (9 cases)");
}
