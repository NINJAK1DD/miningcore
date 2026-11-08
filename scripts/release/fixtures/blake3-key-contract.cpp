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

using Hash = void (*)(const char*, char*, uint32_t, const char*, uint32_t);

static void require(bool condition, const char* message)
{
    if(!condition) { std::fprintf(stderr, "%s\n", message); std::exit(1); }
}

static void check_hash(Hash hash, const char* key, uint32_t length, const char* expected)
{
    unsigned char output[32];
    std::memset(output, 0xa5, sizeof(output));
    hash("\0\1\2", reinterpret_cast<char*>(output), 3, key, length);
    char encoded[65];
    for(size_t i = 0; i < sizeof(output); ++i) std::snprintf(encoded + 2 * i, 3, "%02x", output[i]);
    require(std::strcmp(encoded, expected) == 0, "Blake3 valid-mode known-answer mismatch");
}

static void expect_rejection(Hash hash, const char* key, uint32_t length)
{
    const pid_t child = fork();
    require(child >= 0, "fork failed");
    if(child == 0)
    {
        char output[32];
        std::memset(output, 0xa5, sizeof(output));
        hash("\0\1\2", output, 3, key, length);
        _exit(0); // Returning any output for an invalid key violates the void-ABI contract.
    }
    int status = 0;
    require(waitpid(child, &status, 0) == child, "waitpid failed");
    require(WIFSIGNALED(status) && WTERMSIG(status) == SIGABRT,
        "Invalid Blake3 key must abort before an out-of-bounds read or returning a hash");
}

int main(int argc, char** argv)
{
    require(argc == 2, "usage: blake3-key-contract LIBMULTIHASH_PATH");
    const rlimit no_core = {0, 0};
    require(setrlimit(RLIMIT_CORE, &no_core) == 0, "cannot disable core dumps");
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    require(library != nullptr, "cannot load libmultihash");
    const auto hash = reinterpret_cast<Hash>(dlsym(library, "blake3_export"));
    require(hash != nullptr, "missing blake3_export");

    const long page = sysconf(_SC_PAGESIZE);
    require(page > 32, "invalid page size");
    char* memory = static_cast<char*>(mmap(nullptr, 2 * page, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0));
    require(memory != MAP_FAILED, "mmap failed");
    require(mprotect(memory + page, page, PROT_NONE) == 0, "mprotect failed");
    const char* unkeyed = "e1be4d7a8ab5560aa4199eea339849ba8e293d55ca0a81006726d184519e647f";
    check_hash(hash, nullptr, 0, unkeyed);
    check_hash(hash, memory + page, 0, unkeyed); // Zero length must not read even an inaccessible key pointer.
    std::memcpy(memory + page - 32, "whats the Elvish word for friend", 32);
    check_hash(hash, memory + page - 32, 32, "39e67b76b5a007d4921969779fe666da67b5213b096084ab674742f0d5ec62b9");
    const uint32_t invalid_lengths[] = {1, 31, 33};
    for(const auto length : invalid_lengths)
        expect_rejection(hash, memory + page - length, length);
    const uint32_t null_lengths[] = {1, 31, 32, 33, UINT32_MAX};
    for(const auto length : null_lengths) expect_rejection(hash, nullptr, length);
    expect_rejection(hash, memory + page, UINT32_MAX);
    require(munmap(memory, 2 * page) == 0, "munmap failed");
    dlclose(library);
    std::puts("Blake3 key contract: valid vectors passed; all invalid keys aborted before access");
}
