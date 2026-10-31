#if !defined(_M_X64)
#error This fixture requires a Windows x64 compiler
#endif

#include "../../../src/Native/libmultihash/xelishash/xelishashv1.cpp"
#include <cstdio>

static bool verify(const char* text, const unsigned char* expected)
{
    alignas(8) unsigned char original[112] = {};
    std::memcpy(original, text, std::strlen(text));
    for(int repeat = 0; repeat < 2; repeat++)
    {
        alignas(8) unsigned char state[200] = {};
        unsigned char hash[32] = {};
        std::memcpy(state, original, sizeof(original));
        xelis_hash(state, sizeof(state), hash);
        if(std::memcmp(hash, expected, sizeof(hash)) != 0) return false;
    }
    return true;
}

int main()
{
    // xelis-project/xelis-hash f48262b931438fdbf2932711613e020c02f1ea04 src/v1.rs.
    const unsigned char zero[32] = {
        0x0e,0xbb,0xbd,0x8a,0x31,0xed,0xad,0xfe,0x09,0x8f,0x2d,0x77,0x0d,0x84,0xb7,0x19,
        0x58,0x86,0x75,0xab,0x88,0xa0,0xa1,0x70,0x67,0xd0,0x0a,0x8f,0x36,0x18,0x22,0x65 };
    const unsigned char text[32] = {
        0x6a,0x6a,0xad,0x08,0xcf,0x3b,0x76,0x6c,0xb0,0xc4,0x09,0x7c,0xfa,0xc3,0x03,0x3d,
        0x1e,0x92,0xee,0xb6,0x58,0x53,0x73,0x51,0x8b,0x38,0x03,0x1c,0xb0,0x56,0x44,0x15 };
    if(!verify("", zero) || !verify("xelis-hashing-algorithm", text)) return 1;
    std::puts("Windows source-built Xelis v1 upstream vectors and repeatability passed");
    return 0;
}
