#include <float.h>
#include <stdint.h>
#include <string.h>

#if !defined(__linux__) || !defined(__x86_64__)
#error This test helper requires Linux x86-64
#endif
_Static_assert(LDBL_MANT_DIG == 64, "The x87 probe needs the System V extended format");

unsigned int miningcore_test_mxcsr_mask(void)
{
    _Alignas(16) unsigned char image[512] = { 0 };
    __asm__ volatile("fxsave %0" : "+m"(image));
    uint32_t mask;
    memcpy(&mask, image + 28, sizeof mask);
    return mask ? mask : 0x0000ffbfu; /* Intel's fallback excludes optional DAZ. */
}

/* Intentionally leave one x87 value live, solely to test the scope's guard. */
void miningcore_test_push_one(void)
{
    __asm__ volatile("fld1" : : : "st");
}

int miningcore_test_pop_is_one(void)
{
    long double value;
    __asm__ volatile("fstpt %0" : "=m"(value) : : "st");
    return value == 1.0L;
}
