INC_DIRS = -I. -Icontrib/epee/include
CFLAGS = $(CPU_FLAGS) $(INC_DIRS) -fno-exceptions -std=gnu11 -march=native -fPIC -DNDEBUG -Ofast -funroll-loops -fvariable-expansion-in-unroller -ftree-loop-if-convert-stores -fmerge-all-constants -fbranch-target-load-optimize2
CXXFLAGS = $(CPU_FLAGS) $(INC_DIRS) -fexceptions -frtti -std=gnu++14 -march=native -fPIC -DNDEBUG -Ofast -s -funroll-loops -fvariable-expansion-in-unroller -ftree-loop-if-convert-stores -fmerge-all-constants -fbranch-target-load-optimize2
LDFLAGS = -shared -Wl,--no-undefined
LDLIBS = -lboost_date_time -lboost_regex -lcrypto -lsodium -lssl
TARGET  = libcryptonote.so

OBJECTS = exports.o \
	cryptonote_core/cryptonote_format_utils.o \
	offshore/pricing_record.o \
	crypto/tree-hash.o \
	crypto/crypto.o \
	crypto/crypto-ops.o \
	crypto/crypto-ops-data.o \
	crypto/hash.o \
	crypto/keccak.o \
	common/base58.o \
        zephyr_oracle/pricing_record.o \
        salvium_oracle/pricing_record.o \
	miningcore_stubs.o

# Miningcore uses this library only for parsing, block hashing-blob conversion and fast hashing.
# miningcore_stubs.o supplies the upstream range-proof sizing and hex helpers required by those
# paths. Its remaining cn_slow_hash stub is unreachable from the exported API today; every C ABI
# entry point catches native exceptions so a future call cannot unwind across P/Invoke.

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CXX) $(CXXFLAGS) $(CPPFLAGS) $(LDFLAGS) -o $@ $^ $(LDLIBS)

.PHONY: clean

clean:
	$(RM) $(TARGET) $(OBJECTS)
