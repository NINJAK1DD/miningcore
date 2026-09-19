CFLAGS += -g -Wall -c -O3 -fPIC -DNDEBUG -Wno-deprecated-declarations -Wno-maybe-uninitialized -flax-vector-conversions -Wno-unused-variable -Wunused-but-set-variable -Wno-unused-function $(CPU_FLAGS) $(HAVE_FEATURE)
CXXFLAGS += -g -Wall -O3 -fPIC -fpermissive -Wuninitialized -DNDEBUG -Wno-deprecated-declarations -Wno-maybe-uninitialized -flax-vector-conversions -Wno-unused-variable -Wunused-but-set-variable -Wno-unused-function -Wno-sign-compare -std=c++17 $(CPU_FLAGS) $(HAVE_FEATURE)

ifneq ($(UNAME_P),x86_64)
        $(error libdero Linux builds support x86_64 only)
endif
CXXFLAGS += -m64

LDFLAGS += -shared -Wl,--no-undefined
LDLIBS = -lboost_atomic -lboost_context -lboost_chrono -lboost_program_options -lboost_thread -lpthread -lssl -lcrypto
TARGET = libdero.so

OBJECTS = include/highwayhash/arch_specific.o include/highwayhash/c_bindings.o include/highwayhash/hh_portable.o include/highwayhash/instruction_sets.o include/highwayhash/scalar_sip_tree_hash.o include/highwayhash/sip_hash.o \
        include/libkeccak/digest.o include/libkeccak/libkeccak_behex_lower.o include/libkeccak/libkeccak_behex_upper.o include/libkeccak/libkeccak_cshake_initialise.o include/libkeccak/libkeccak_cshake_suffix.o \
        include/libkeccak/libkeccak_degeneralise_spec.o include/libkeccak/libkeccak_generalised_sum_fd.o include/libkeccak/libkeccak_hmac_copy.o include/libkeccak/libkeccak_hmac_create.o include/libkeccak/libkeccak_hmac_destroy.o \
        include/libkeccak/libkeccak_hmac_digest.o include/libkeccak/libkeccak_hmac_duplicate.o include/libkeccak/libkeccak_hmac_fast_destroy.o include/libkeccak/libkeccak_hmac_fast_digest.o include/libkeccak/libkeccak_hmac_fast_free.o \
        include/libkeccak/libkeccak_hmac_fast_update.o include/libkeccak/libkeccak_hmac_free.o include/libkeccak/libkeccak_hmac_initialise.o include/libkeccak/libkeccak_hmac_marshal.o include/libkeccak/libkeccak_hmac_reset.o \
        include/libkeccak/libkeccak_hmac_set_key.o include/libkeccak/libkeccak_hmac_unmarshal.o include/libkeccak/libkeccak_hmac_update.o include/libkeccak/libkeccak_hmac_wipe.o include/libkeccak/libkeccak_keccaksum_fd.o \
        include/libkeccak/libkeccak_rawshakesum_fd.o include/libkeccak/libkeccak_sha3sum_fd.o include/libkeccak/libkeccak_shakesum_fd.o include/libkeccak/libkeccak_spec_check.o include/libkeccak/libkeccak_spec_rawshake.o \
        include/libkeccak/libkeccak_spec_sha3.o include/libkeccak/libkeccak_state_copy.o include/libkeccak/libkeccak_state_create.o include/libkeccak/libkeccak_state_destroy.o include/libkeccak/libkeccak_state_duplicate.o \
        include/libkeccak/libkeccak_state_fast_destroy.o include/libkeccak/libkeccak_state_fast_free.o include/libkeccak/libkeccak_state_free.o include/libkeccak/libkeccak_state_initialise.o include/libkeccak/libkeccak_state_marshal.o \
        include/libkeccak/libkeccak_state_reset.o include/libkeccak/libkeccak_state_unmarshal.o include/libkeccak/libkeccak_state_wipe.o include/libkeccak/libkeccak_state_wipe_message.o include/libkeccak/libkeccak_state_wipe_sponge.o \
        include/libkeccak/libkeccak_unhex.o include/libkeccak/libkeccak_zerocopy_chunksize.o \
        include/libsais/libsais.o 

# Dispatch references both implementations even on a non-AVX2 builder.
# Optional code generation is scoped to these translation units.
OBJECTS += include/highwayhash/hh_avx2.o include/highwayhash/hh_sse41.o
include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2
include/highwayhash/hh_sse41.o: CXXFLAGS += -msse4.1

OBJECTS += astro_aarch64.o astrobwtv3.o exports.o 

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^ $(LDLIBS)

.PHONY: clean

clean:
	$(RM) $(TARGET) $(OBJECTS)
