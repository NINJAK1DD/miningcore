#define _POSIX_C_SOURCE 200809L
#include <dlfcn.h>
#include <fenv.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

/* Never link this probe with fast-math: its own startup must preserve IEEE mode. */
struct controls {
#if defined(__x86_64__)
    unsigned short x87;
    unsigned int mxcsr;
#elif defined(__aarch64__)
    uint64_t fpcr;
    uint64_t fpsr;
#else
#error Unsupported floating-point environment: add an explicit architecture probe
#endif
};

static struct controls capture(void)
{
    struct controls state;
#if defined(__x86_64__)
    __asm__ volatile("fnstcw %0" : "=m"(state.x87));
    __asm__ volatile("stmxcsr %0" : "=m"(state.mxcsr));
#else
    __asm__ volatile("mrs %0, fpcr" : "=r"(state.fpcr));
    __asm__ volatile("mrs %0, fpsr" : "=r"(state.fpsr));
#endif
    return state;
}

static int unchanged(const char *path, const char *phase, struct controls before)
{
    struct controls after = capture();
#if defined(__x86_64__)
    /* Sticky exception status records prior arithmetic, not the caller's mode. */
    if(before.x87 == after.x87 && (before.mxcsr & ~0x3fu) == (after.mxcsr & ~0x3fu))
        return 1;
    fprintf(stderr, "%s: %s changed FP state: x87 %04x -> %04x, MXCSR %08x -> %08x\n",
        path, phase, before.x87, after.x87, before.mxcsr, after.mxcsr);
#else
    if(before.fpcr == after.fpcr)
        return 1;
    fprintf(stderr, "%s: %s changed FP state: FPCR %016llx -> %016llx, FPSR %016llx -> %016llx\n",
        path, phase, (unsigned long long)before.fpcr, (unsigned long long)after.fpcr,
        (unsigned long long)before.fpsr, (unsigned long long)after.fpsr);
#endif
    return 0;
}

int main(int argc, char **argv)
{
    static const int rounding[] = { FE_TONEAREST, FE_DOWNWARD, FE_UPWARD, FE_TOWARDZERO };
    if((argc != 3 && argc != 4) || argv[2][0] < '0' || argv[2][0] > '7' || argv[2][1] != '\0' ||
        (argc == 4 && strcmp(argv[3], "--cryptonote-vectors") != 0))
    {
        fprintf(stderr, "usage: native-load-fenv LIBRARY MODE(0..7) [--cryptonote-vectors]\n");
        return 64;
    }
    const unsigned int mode = (unsigned int)(argv[2][0] - '0');
    fenv_t original;
    if(fegetenv(&original) || fesetenv(FE_DFL_ENV) || fesetround(rounding[mode % 4]))
    {
        fprintf(stderr, "Cannot establish floating-point probe environment\n");
        return 1;
    }
    if(mode >= 4)
    {
#if defined(__x86_64__)
        unsigned int mxcsr = capture().mxcsr | 0x8040u;
        __asm__ volatile("ldmxcsr %0" : : "m"(mxcsr));
#else
        uint64_t fpcr = capture().fpcr | (UINT64_C(1) << 24);
        __asm__ volatile("msr fpcr, %0" : : "r"(fpcr));
#endif
    }
    const struct controls before = capture();
    void *library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if(!library)
    {
        fprintf(stderr, "%s: dlopen failed: %s\n", argv[1], dlerror());
        (void)fesetenv(&original);
        return 1;
    }
    int ok = unchanged(argv[1], "dlopen", before);
    const char *name = strrchr(argv[1], '/');
    name = name ? name + 1 : argv[1];
    if(argc == 4 && strcmp(name, "libcryptonote.so") == 0)
    {
        typedef void (*fast_hash)(const char *, unsigned char *, uint32_t);
        fast_hash hash = (fast_hash)dlsym(library, "cn_fast_hash_export");
        static const unsigned char empty_hash[32] = {
            0xc5, 0xd2, 0x46, 0x01, 0x86, 0xf7, 0x23, 0x3c,
            0x92, 0x7e, 0x7d, 0xb2, 0xdc, 0xc7, 0x03, 0xc0,
            0xe5, 0x00, 0xb6, 0x53, 0xca, 0x82, 0x27, 0x3b,
            0x7b, 0xfa, 0xd8, 0x04, 0x5d, 0x85, 0xa4, 0x70
        };
        unsigned char output[32];
        if(!hash)
        {
            fprintf(stderr, "%s: missing cn_fast_hash_export\n", argv[1]);
            ok = 0;
        }
        else
        {
            hash("", output, 0);
            if(memcmp(output, empty_hash, sizeof output))
            {
                fprintf(stderr, "%s: empty-input Keccak vector mismatch\n", argv[1]);
                ok = 0;
            }
            if(!unchanged(argv[1], "fast hash", before))
                ok = 0;
            memset(output, 0xa5, sizeof output);
            hash(NULL, output, 1); /* Native input rejection must preserve state too. */
            static const unsigned char zero[32] = { 0 };
            if(memcmp(output, zero, sizeof output))
            {
                fprintf(stderr, "%s: rejected-input hash did not clear output\n", argv[1]);
                ok = 0;
            }
            if(!unchanged(argv[1], "rejected fast hash", before))
                ok = 0;
        }
    }
    if(dlclose(library))
    {
        fprintf(stderr, "%s: dlclose failed: %s\n", argv[1], dlerror());
        ok = 0;
    }
    if(!unchanged(argv[1], "dlclose", before))
        ok = 0;
    if(fesetenv(&original))
        ok = 0;
    return ok ? 0 : 1;
}
