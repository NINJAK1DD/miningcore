// Execute real exported entry points: merely loading or scanning a DSO misses
// compiler-generated instructions in ordinary hashing and initialization code.
#include <dlfcn.h>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>
#include "zano-genesis.h"
#include "verus-v22.h"

static void require(bool ok, const char* message)
{
    if(!ok) { std::fprintf(stderr, "%s\n", message); std::exit(1); }
}

template<typename T> static T symbol(void* library, const char* name)
{
    auto result = reinterpret_cast<T>(dlsym(library, name));
    require(result != nullptr, name);
    return result;
}

static void* open_library(const char* directory, const char* name)
{
    const auto path = std::string(directory) + "/" + name;
    auto result = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    if(!result) { std::fprintf(stderr, "%s: %s\n", path.c_str(), dlerror()); std::exit(1); }
    return result;
}

static std::vector<uint8_t> unhex(const char* text)
{
    std::vector<uint8_t> result;
    for(size_t i = 0; i < std::strlen(text); i += 2) {
        unsigned value;
        require(std::sscanf(text + i, "%2x", &value) == 1, "invalid vector");
        result.push_back(static_cast<uint8_t>(value));
    }
    return result;
}

static void check_hash(const uint8_t* actual, const char* expected)
{
    const auto bytes = unhex(expected);
    require(bytes.size() == 32 && std::memcmp(actual, bytes.data(), 32) == 0,
        "hash differs from known answer");
}

static void randomx(const char* directory, const char* name, const char* expected)
{
    std::printf("Testing %s\n", name);
    auto lib = open_library(directory, name);
    auto get_flags = symbol<int (*)()>(lib, "randomx_get_flags");
    auto alloc = symbol<void* (*)(int)>(lib, "randomx_alloc_cache");
    auto init = symbol<void (*)(void*, const void*, size_t)>(lib, "randomx_init_cache");
    auto release = symbol<void (*)(void*)>(lib, "randomx_release_cache");
    auto create_vm = symbol<void* (*)(int, void*, void*)>(lib, "randomx_create_vm");
    auto destroy_vm = symbol<void (*)(void*)>(lib, "randomx_destroy_vm");
    auto hash = symbol<void (*)(void*, const void*, size_t, void*)>(lib, "randomx_calculate_hash");
    const int flags = get_flags();
    auto cache = alloc(flags);
    require(cache != nullptr, "cache allocation failed");
    // Encoding.UTF8.GetBytes("test key 000") from the managed known-answer tests.
    init(cache, "test key 000", 12);
    auto vm = create_vm(flags, cache, nullptr);
    require(vm != nullptr, "VM creation failed");
    uint8_t output[32];
    hash(vm, "This is a test", 14, output);
    if(expected) check_hash(output, expected);
    for(auto byte : output) std::printf("%02x", byte);
    std::puts("");
    destroy_vm(vm);
    release(cache);
    dlclose(lib);
}

static void highwayhash(const char* directory)
{
    std::puts("Testing Dero HighwayHash dispatch");
    auto lib = open_library(directory, "libdero.so");
    auto highway = symbol<uint64_t (*)(const uint64_t*, const char*, uint64_t)>(lib, "HighwayHash64");
    const uint64_t key[] = {0x0706050403020100ULL, 0x0f0e0d0c0b0a0908ULL,
        0x1716151413121110ULL, 0x1f1e1d1c1b1a1918ULL};
    // Google's HighwayHash test vector for an empty input and this key.
    require(highway(key, "", 0) == 0x907a56de22c26e53ULL, "HighwayHash differs");
    dlclose(lib);
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    require(argc == 2, "usage: native-cpu-portability LIBRARY_DIRECTORY");
    const char* directory = argv[1];
    randomx(directory, "librandomx.so", "639183aae1bf4c9a35884cb46b09cad9175f04efd7684e7262a0ac1c2f0b4e3f");
    randomx(directory, "librandomarq.so", "27f66e4650eb5657513e76c140e09e59336786f21fbef1ed6ff40fc21538221e");
    // These forks have different consensus hashes. Compare their outputs
    // between the native host and emulated CPU below rather than borrowing
    // RandomX vectors that do not apply to them.
    randomx(directory, "libpanthera.so", nullptr);
    randomx(directory, "librandomxscash.so", nullptr);

    std::puts("Testing CryptoNote integrated address");
    auto lib = open_library(directory, "libcryptonote.so");
    auto decode = symbol<uint64_t (*)(const char*, unsigned)>(lib, "decode_integrated_address_export");
    const char* address = "4BrL51JCc9NGQ71kWhnYoDRffsDZy7m1HUU7MRU4nUMXAHNFBEJhkTZV9HdaL4gfuNBxLPc3BeMkLGaPbF5vWtANQsGwTGg55Kq4p3ENE7";
    require(decode(address, std::strlen(address)) == 19, "integrated address differs");
    dlclose(lib);

    std::puts("Testing GhostRider");
    lib = open_library(directory, "libcryptonight.so");
    auto alloc = symbol<void* (*)()>(lib, "alloc_context_export");
    auto release = symbol<void (*)(void*)>(lib, "free_context_export");
    auto hash = symbol<bool (*)(const uint8_t*, size_t, char*, int, uint64_t, void*)>(lib, "cryptonight_export");
    auto input = unhex("000000208c246d0b90c3b389c4086e8b672ee040d64db5b9648527133e217fbfa48da64c0f3c0a0b0e8350800568b40fbb323ac3ccdf2965de51b9aaeb939b4f11ff81c49b74a16156ff251c00000000");
    auto context = alloc();
    require(context != nullptr, "context allocation failed");
    uint8_t output[32];
    require(hash(input.data(), input.size(), reinterpret_cast<char*>(output), 0x6c150000, 0, context), "GhostRider failed");
    check_hash(output, "84402e62b6bedafcd65f6ba13b59ff19ad7f273900c59fa49bfbb5f67e10030f");
    std::puts("Testing CryptoNight Chukwa default implementation");
    auto impl = symbol<const char* (*)()>(lib, "argon2_get_impl_name");
    require(std::strcmp(impl(), "default") == 0, "unexpected CryptoNight Argon2 implementation");
    auto chukwa = symbol<bool (*)(const uint8_t*, size_t, char*, int, uint64_t, void*)>(lib, "argon_export");
    input = unhex("0305a0dbd6bf05cf16e503f3a66f78007cbf34144332ecbfc22ed95c8700383b309ace1923a0964b00000008ba939a62724c0d7581fce5761e9d8a0e6a1c3f924fdd8493d1115649c05eb601");
    require(chukwa(input.data(), input.size(), reinterpret_cast<char*>(output), 0x61130000, 10, context), "Chukwa failed");
    check_hash(output, "c158a105ae75c7561cfd029083a47a87653d51f914128e21c1971d8b10c49034");
    require(std::strcmp(impl(), "default") == 0, "CryptoNight Argon2 selector changed during hashing");
    release(context);
    dlclose(lib);

    std::puts("Testing multihash Argon2 and BLAKE3 dispatch");
    lib = open_library(directory, "libmultihash.so");
    auto argon = symbol<void (*)(const char*, char*, uint32_t)>(lib, "argon2d250_export");
    input.assign(80, 0x80);
    argon(reinterpret_cast<const char*>(input.data()), reinterpret_cast<char*>(output), input.size());
    check_hash(output, "5b75d9a75f843872e975ae322e6011d3b2598b6eadb6c0c0df150b4e0604ff0a");
    auto blake3 = symbol<void (*)(const char*, char*, uint32_t, const char*, uint32_t)>(lib, "blake3_export");
    blake3("", reinterpret_cast<char*>(output), 0, nullptr, 0);
    check_hash(output, "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262");
    dlclose(lib);
    highwayhash(directory);

    std::puts("Testing VerusHash 2.2 Haraka/CLHash dispatch");
    lib = open_library(directory, "libverushash.so");
    auto verus = symbol<void (*)(char*, char*, int)>(lib, "verushash2b2o_export");
    input.resize(80);
    for(size_t i = 0; i < input.size(); ++i) input[i] = static_cast<uint8_t>(i);
    verus(reinterpret_cast<char*>(input.data()), reinterpret_cast<char*>(output), input.size());
    check_hash(output, "b670b52b5d8afbbe5418a2e1a6465f58f1ccf6a9d3a167fb888c956505d2d3a7");
    // Upstream PBaaS block vector uses header canonicalization before hashing.
    auto verus_block = symbol<void (*)(char*, char*, int)>(lib, "verushash2b2_export");
    input.assign(verus_v22_pbaas_header, verus_v22_pbaas_header + sizeof(verus_v22_pbaas_header));
    verus_block(reinterpret_cast<char*>(input.data()), reinterpret_cast<char*>(output), input.size());
    check_hash(output, "3501fee3bb23f5858d4dc9469677d9ad5aace1b64bd74668856e740989000000");
    dlclose(lib);

    std::puts("Testing Cortex header and SipHash proof rejection");
    lib = open_library(directory, "libcortexcuckoocycle.so");
    auto cortex = symbol<int32_t (*)(const char*, int, const char*)>(lib, "cortexcuckoocycle_export");
    uint32_t edges[42] = {};
    char header[32] = {};
    // Edge zero is hashed before the repeated second edge is rejected.
    require(cortex(header, sizeof(header), reinterpret_cast<const char*>(edges)) == 3,
        "Cortex duplicate-edge rejection differs");
    dlclose(lib);

    std::puts("Testing Zano malformed-block parsing through all managed exports");
    lib = open_library(directory, "libzanonote.so");
    const char malformed[] = {static_cast<char>(0xff)}; // truncated varint
    uint8_t blob[256] = {};
    unsigned blob_size = sizeof(blob);
    auto convert = symbol<bool (*)(const char*, unsigned, uint8_t*, unsigned*)>(lib, "convert_blob_export");
    require(!convert(malformed, sizeof(malformed), blob, &blob_size) && blob_size == 0,
        "Zano malformed hashing-blob rejection differs");
    auto convert_block = symbol<bool (*)(const char*, unsigned, uint8_t*, unsigned*, uint64_t)>(lib, "convert_block_export");
    blob_size = sizeof(blob);
    require(!convert_block(malformed, sizeof(malformed), blob, &blob_size, 0) && blob_size == 0,
        "Zano malformed block rejection differs");
    for(const char* name : {"get_blob_id_export", "get_block_id_export"}) {
        auto id = symbol<bool (*)(const char*, unsigned, uint8_t*)>(lib, name);
        require(!id(malformed, sizeof(malformed), output), "Zano malformed block ID rejection differs");
    }
    std::puts("Testing Zano genesis serialization and hashing");
    input = unhex(zano_genesis_hex);
    auto mining_id = symbol<bool (*)(const char*, unsigned, uint8_t*)>(lib, "get_blob_id_export");
    auto block_id = symbol<bool (*)(const char*, unsigned, uint8_t*)>(lib, "get_block_id_export");
    require(mining_id(reinterpret_cast<const char*>(input.data()), input.size(), output), "Zano genesis mining hash failed");
    check_hash(output, "2f4667d8e7190a7fcc1e5de7e93d6c97fe9e883caed5ab35281f9a9deef5c595");
    require(block_id(reinterpret_cast<const char*>(input.data()), input.size(), output), "Zano genesis block ID failed");
    check_hash(output, "cc608f59f8080e2fbfe3c8c80eb6e6a953d47cf2d6aebd345bada3a1cab99852");
    const auto expected_blob = unhex("019db2b984170000000000000000000000000000000000000000000000000000000000000000000000000000a3f7f244c40d44eaf987d11a66e88b513785768b9675b535ce398f90913acf7301");
    blob_size = sizeof(blob);
    require(convert(reinterpret_cast<const char*>(input.data()), input.size(), blob, &blob_size) &&
        blob_size == expected_blob.size() && std::memcmp(blob, expected_blob.data(), blob_size) == 0,
        "Zano genesis hashing blob differs");
    std::vector<uint8_t> serialized(input.size());
    blob_size = serialized.size();
    require(convert_block(reinterpret_cast<const char*>(input.data()), input.size(), serialized.data(),
        &blob_size, 101011010205ULL) && blob_size == input.size() && serialized == input,
        "Zano genesis block round trip differs");
    dlclose(lib);
    std::puts("Native CPU portability vectors passed");
}
