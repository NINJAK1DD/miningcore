#!/bin/bash

set -euo pipefail

OutDir=${1:?usage: build-libs-linux.sh OUTPUT_DIRECTORY}
ScriptDir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
NativeDir=$(cd "$ScriptDir/../Native" && pwd)

mkdir -p "$OutDir"
OutDir=$(cd "$OutDir" && pwd)

stage_native_library() {
  local component=$1
  local library=$2
  local source="$NativeDir/$component/$library"

  if [[ ! -f "$source" ]]; then
    echo "Native component '$component' completed without producing $library" >&2
    return 1
  fi

  mv "$source" "$OutDir/"
}

build_native_library() {
  local component=$1
  local library=$2
  shift 2

  echo "Building native component: $component"

  (
    cd "$NativeDir/$component"
    make clean
    make "$@"
  )

  stage_native_library "$component" "$library"
}

build_external_native_library() {
  local component=$1
  local library=$2
  shift 2

  echo "Building native component: $component"

  "$@"

  stage_native_library "$component" "$library"
}

UNAME_S=$(uname -s)
export UNAME_S
UNAME_P=$(uname -m || uname -p)
export UNAME_P

AES=$("$NativeDir/check_cpu.sh" aes && echo -maes || echo)
SSE2=$("$NativeDir/check_cpu.sh" sse2 && echo -msse2 || echo)
SSE3=$("$NativeDir/check_cpu.sh" sse3 && echo -msse3 || echo)
SSSE3=$("$NativeDir/check_cpu.sh" ssse3 && echo -mssse3 || echo)
PCLMUL=$("$NativeDir/check_cpu.sh" pclmul && echo -mpclmul || echo)
AVX=$("$NativeDir/check_cpu.sh" avx && echo -mavx || echo)
AVX2=$("$NativeDir/check_cpu.sh" avx2 && echo -mavx2 || echo)
AVX512F=$("$NativeDir/check_cpu.sh" avx512f && echo -mavx512f || echo)

export CPU_FLAGS="$AES $SSE2 $SSE3 $SSSE3 $PCLMUL $AVX $AVX2 $AVX512F"

HAVE_AES=$("$NativeDir/check_cpu.sh" aes && echo -D__AES__ || echo)
HAVE_SSE2=$("$NativeDir/check_cpu.sh" sse2 && echo -DHAVE_SSE2 || echo)
HAVE_SSE3=$("$NativeDir/check_cpu.sh" sse3 && echo -DHAVE_SSE3 || echo)
HAVE_SSSE3=$("$NativeDir/check_cpu.sh" ssse3 && echo -DHAVE_SSSE3 || echo)
HAVE_PCLMUL=$("$NativeDir/check_cpu.sh" pclmul && echo -DHAVE_PCLMUL || echo)
HAVE_AVX=$("$NativeDir/check_cpu.sh" avx && echo -DHAVE_AVX || echo)
HAVE_AVX2=$("$NativeDir/check_cpu.sh" avx2 && echo -DHAVE_AVX2 || echo)
HAVE_AVX512F=$("$NativeDir/check_cpu.sh" avx512f && echo -DHAVE_AVX512F || echo)

export HAVE_FEATURE="$HAVE_AES $HAVE_SSE2 $HAVE_SSE3 $HAVE_SSSE3 $HAVE_PCLMUL $HAVE_AVX $HAVE_AVX2 $HAVE_AVX512F"

build_native_library libmultihash libmultihash.so \
  CPU_FLAGS="$CPU_FLAGS" HAVE_FEATURE="$HAVE_FEATURE"
build_native_library libbeamhash libbeamhash.so
build_native_library libetchash libetchash.so
build_native_library libethhash libethhash.so
build_native_library libethhashb3 libethhashb3.so -j
build_native_library libubqhash libubqhash.so
build_native_library libcryptonote libcryptonote.so
build_native_library libcryptonight libcryptonight.so
build_native_library libverushash libverushash.so
build_native_library libfiropow libfiropow.so
build_native_library libkawpow libkawpow.so
build_native_library libmeowpow libmeowpow.so
build_native_library libdero libdero.so
build_native_library libcortexcuckoocycle libcortexcuckoocycle.so
build_native_library libprogpowz libprogpowz.so
build_native_library libzanonote libzanonote.so
build_native_library libmerakipow libmerakipow.so
build_native_library libphihash libphihash.so
build_native_library libsccpow libsccpow.so

build_nexapow() {
  (
    cd "${TMPDIR:-/tmp}"
    rm -rf secp256k1
    git clone https://github.com/bitcoin-ABC/secp256k1
    cd secp256k1
    git checkout 04fabb44590c10a19e35f044d11eb5058aac65b2
    cmake -S . -B build -GNinja \
      -DCMAKE_C_FLAGS=-fPIC \
      -DSECP256K1_ENABLE_MODULE_RECOVERY=OFF \
      -DSECP256K1_ENABLE_COVERAGE=OFF \
      -DSECP256K1_ENABLE_MODULE_SCHNORR=ON
    cmake --build build
    cd "$NativeDir/libnexapow"
    cp "${TMPDIR:-/tmp}/secp256k1/build/libsecp256k1.a" .
    make clean
    make
  )
}

build_randomx_family() {
  local repository=$1
  local checkout=$2
  local source_name=$3
  local component=$4
  local build_target=${5:-}
  local source_patch=${6:-}

  (
    cd "${TMPDIR:-/tmp}"
    rm -rf "$source_name"
    git clone "$repository" "$source_name"
    cd "$source_name"
    git checkout "$checkout"
    if [[ -n "$source_patch" ]]; then
      git apply --check "$ScriptDir/$source_patch"
      git apply "$ScriptDir/$source_patch"
    fi
    cmake -S . -B build \
      -DARCH=native \
      -DCMAKE_C_FLAGS=-Wa,--noexecstack \
      -DCMAKE_CXX_FLAGS=-Wa,--noexecstack
    if [[ -n "$build_target" ]]; then
      cmake --build build --target "$build_target" -j"$(nproc)"
    else
      cmake --build build -j"$(nproc)"
    fi
    cd "$NativeDir/$component"
    cp "${TMPDIR:-/tmp}/$source_name/build/librandomx.a" .
    make clean
    make
  )
}

build_external_native_library libnexapow libnexapow.so build_nexapow
build_external_native_library librandomx librandomx.so \
  build_randomx_family https://github.com/tevador/RandomX tags/v1.2.1 RandomX librandomx '' \
  patches/randomx-cmake-policy-floor.patch
build_external_native_library librandomarq librandomarq.so \
  build_randomx_family https://github.com/arqma/RandomARQ \
  3bcb6bafe63d70f8e6f78a0d431e71be2b638083 RandomARQ librandomarq randomx \
  patches/randomarq-cmake-policy-floor.patch
build_external_native_library libpanthera libpanthera.so \
  build_randomx_family https://github.com/scala-network/Panthera \
  cc7425f468d935ba328fba5bbb05f8227f4f22d7 Panthera libpanthera randomx \
  patches/panthera-build-status-warnings.patch
build_external_native_library librandomxscash librandomxscash.so \
  build_randomx_family https://github.com/scashnetwork/RandomX \
  0b3e0ded68b95491516fe974e3db784ca2742ca7 RandomXSCash librandomxscash randomx \
  patches/randomxscash-cmake-policy-floor.patch
