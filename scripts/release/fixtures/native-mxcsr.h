#ifndef MININGCORE_NATIVE_MXCSR_H
#define MININGCORE_NATIVE_MXCSR_H

#if defined(__x86_64__)
#include <stdint.h>
#include <string.h>

static inline unsigned int native_mxcsr_flush_bits(unsigned int mask)
{
    /* Intel's zero-mask fallback reserves DAZ (bit 6). */
    return (mask ? mask : 0x0000ffbfu) & 0x8040u;
}

static inline unsigned int native_supported_flush_bits(void)
{
    /* FXSAVE is part of the x86-64 baseline; its area must be aligned/zeroed. */
    _Alignas(16) unsigned char image[512] = { 0 };
    __asm__ volatile("fxsave %0" : "+m"(image));
    uint32_t mask;
    memcpy(&mask, image + 28, sizeof mask);
    return native_mxcsr_flush_bits(mask);
}
#endif
#endif
