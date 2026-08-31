CFLAGS += -Wall -Wextra -Werror -Wno-unused-function -fPIC -fvisibility=hidden -O2
CXXFLAGS += -Wall -Wextra -Werror -fPIC -fvisibility=hidden -O2 -std=c++11
LDFLAGS += -shared -Wl,--no-undefined
TARGET = libodocrypt.so

OBJECTS = exports.o odocrypt.o KeccakP-800-reference.o

all: $(TARGET)

$(TARGET): $(OBJECTS)
	$(CXX) $(LDFLAGS) -o $@ $^

exports.o: exports.cpp ../libmultihash/odocrypt.h ../libmultihash/KeccakP-800-SnP.h
	$(CXX) $(CXXFLAGS) -c $< -o $@

odocrypt.o: ../libmultihash/odocrypt.cpp ../libmultihash/odocrypt.h
	$(CXX) $(CXXFLAGS) -c $< -o $@

KeccakP-800-reference.o: ../libmultihash/KeccakP-800-reference.c ../libmultihash/KeccakP-800-SnP.h ../libmultihash/brg_endian.h
	$(CC) $(CFLAGS) -c $< -o $@

.PHONY: clean

clean:
	$(RM) $(TARGET) $(OBJECTS)
