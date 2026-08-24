#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "aes.hpp"

#if defined(MININGCORE_TEST_AESNI)
#include <wmmintrin.h>
#endif

namespace
{
constexpr std::array<uint8_t, 16> Input = {
    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
};

constexpr std::array<uint8_t, 16> RoundKey = {
    0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7,
    0xf8, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd, 0xfe, 0xff,
};

constexpr std::array<uint8_t, 16> Expected = {
    0x9a, 0x9b, 0xae, 0xb6, 0xd8, 0x98, 0xc5, 0xa6,
    0x48, 0x20, 0xa7, 0x9a, 0xdb, 0x61, 0xdf, 0xa3,
};

void print_block(const uint8_t* block)
{
    for(size_t i = 0; i < Input.size(); i++)
        std::printf("%02x", block[i]);

    std::putchar('\n');
}
}

int main()
{
    alignas(16) auto portable = Input;
    aes_single_round_no_intrinsics(portable.data(), RoundKey.data());

    if(portable != Expected)
    {
        std::fputs("portable=", stderr);
        print_block(portable.data());
        return 1;
    }

#if defined(MININGCORE_TEST_AESNI)
    alignas(16) auto hardware = Input;
    const auto block = _mm_load_si128(reinterpret_cast<const __m128i*>(hardware.data()));
    const auto key = _mm_load_si128(reinterpret_cast<const __m128i*>(RoundKey.data()));
    const auto result = _mm_aesenc_si128(block, key);
    _mm_store_si128(reinterpret_cast<__m128i*>(hardware.data()), result);

    if(hardware != Expected || hardware != portable)
    {
        std::fputs("hardware=", stderr);
        print_block(hardware.data());
        return 1;
    }
#endif

    return 0;
}
