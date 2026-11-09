#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <signal.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <unistd.h>

using Hash = void (*)(const char*, char*, uint32_t, uint32_t, const char*, uint32_t);

static void require(bool condition, const char* message)
{
    if(!condition) { std::fprintf(stderr, "%s\n", message); std::exit(1); }
}

static void check_hash(Hash hash, const char* key, uint32_t length, uint32_t output_length, const char* expected)
{
    unsigned char output[80]; // Canary also protects the maximum 64-byte output.
    std::memset(output, 0xa5, sizeof(output));
    hash("\0\1\2", reinterpret_cast<char*>(output), 3, output_length, key, length);
    const size_t size = output_length == UINT32_MAX ? 64 : output_length;
    char encoded[129];
    for(size_t i = 0; i < size; ++i) std::snprintf(encoded + 2 * i, 3, "%02x", output[i]);
    require(std::strcmp(encoded, expected) == 0, "Blake2b valid-mode known-answer mismatch");
    for(size_t i = size; i < sizeof(output); ++i) require(output[i] == 0xa5, "Blake2b overwrote output boundary");
}

static void expect_rejection(Hash hash, const char* key, uint32_t length, uint32_t output_length,
    const char* input = "\0\1\2", bool null_output = false)
{
    const pid_t child = fork();
    require(child >= 0, "fork failed");
    if(child == 0)
    {
        char output[64];
        std::memset(output, 0xa5, sizeof(output));
        hash(input, null_output ? nullptr : output, 3, output_length, key, length);
        _exit(0);
    }
    int status = 0;
    require(waitpid(child, &status, 0) == child, "waitpid failed");
    require(WIFSIGNALED(status) && WTERMSIG(status) == SIGABRT,
        "Invalid Blake2b arguments must abort instead of returning stale output or faulting");
}

int main(int argc, char** argv)
{
    require(argc == 2, "usage: blake2b-key-contract LIBMULTIHASH_PATH");
    const rlimit no_core = {0, 0};
    require(setrlimit(RLIMIT_CORE, &no_core) == 0, "cannot disable core dumps");
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(library != nullptr, "cannot load libmultihash");
    const auto hash = reinterpret_cast<Hash>(dlsym(library, "blake2b_export"));
    require(hash != nullptr, "missing blake2b_export");
    const long page = sysconf(_SC_PAGESIZE);
    require(page > 64, "invalid page size");
    char* memory = static_cast<char*>(mmap(nullptr, 2 * page, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0));
    require(memory != MAP_FAILED, "mmap failed");
    require(mprotect(memory + page, page, PROT_NONE) == 0, "mprotect failed");
    const char* unkeyed = "3d8c3d594928271f44aad7a04b177154806867bcf918e1549c0bc16f9da2b09b";
    check_hash(hash, nullptr, 0, 32, unkeyed);
    check_hash(hash, memory + page, 0, 32, unkeyed);
    check_hash(hash, "BlockHash", 9, 32, "6f97f3b44764fcce84e8570a2c84ae99e68f4d2178ed826f04d7c20d051e3636");
    for(int i = 0; i < 64; ++i) memory[page - 64 + i] = static_cast<char>(i);
    check_hash(hash, memory + page - 64, 64, 32, "3e57c5ab79418defd6e252719a380096d9abf1901db38e0be7d404eb7206c0dc");
    const char* full = "40a374727302d9a4769c17b5f409ff32f58aa24ff122d7603e4fda1509e919d4107a52c57570a6d94e50967aea573b11f86f473f537565c66f7039830a85d186";
    check_hash(hash, nullptr, 0, 64, full);
    check_hash(hash, nullptr, 0, UINT32_MAX, full); // Preserve the existing default-size sentinel.
    // Independent hashlib.blake2b vectors for native-only and intermediate sizes.
    check_hash(hash, nullptr, 0, 1, "a6");
    check_hash(hash, "BlockHash", 9, 1, "4a");
    check_hash(hash, nullptr, 0, 31, "22a7ba6021a49f9bdab11150e264ec41eadd133ae481faedacca6942fad22f");
    check_hash(hash, "BlockHash", 9, 31, "9f8147db70300bf7dc7449222b766efa9a919b8a60ab35d3e0de100691c470");
    check_hash(hash, nullptr, 0, 33, "cba85ebc1ed7305fb68a19adfa409b5b813f1500cfdea38856486027102d67f007");
    check_hash(hash, "BlockHash", 9, 33, "57cb04abfa0486888ab43c8d8c8973f1c808590ee57f99582954ef0e9ebcf0ab6a");
    check_hash(hash, nullptr, 0, 63, "90392b28668537eb22c741f22e2d85f38bb9b8d640a2920c325bcdbada4fa80a845b264a64c651d64a3369de2c2575799cbd2dfb45328a4b738015511ee900");
    check_hash(hash, "BlockHash", 9, 63, "5ede1b3078bfd9839d455e55e4e07afa370bdace055c1a36fd43b29f664b18ea3c9d5ffd3cf548a8bf788dc488ae58127d03312ea59318ef423afc132e95c2");
    expect_rejection(hash, memory + page, 65, 32);
    expect_rejection(hash, memory + page, UINT32_MAX, 32);
    expect_rejection(hash, nullptr, 1, 32);
    expect_rejection(hash, nullptr, 64, 32);
    expect_rejection(hash, nullptr, 0, 0);
    expect_rejection(hash, nullptr, 0, 65);
    expect_rejection(hash, nullptr, 0, 32, nullptr);
    expect_rejection(hash, nullptr, 0, 32, "\0\1\2", true);
    require(munmap(memory, 2 * page) == 0, "munmap failed");
    dlclose(library);
    std::puts("Blake2b contract: valid vectors passed; all invalid arguments aborted");
}
