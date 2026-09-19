#!/bin/bash

set -euo pipefail

# Make's += assignments retain environment flags; a later -march does not
# cancel explicit -m feature switches. Reject overrides before invoking tools.
# Do not print their values (MAKEFLAGS/MAKEFILES may contain arbitrary input).
for native_override in CFLAGS CXXFLAGS CPPFLAGS ASFLAGS LDFLAGS LDLIBS \
    MAKEFLAGS MFLAGS GNUMAKEFLAGS MAKEOVERRIDES MAKEFILES \
    CC CXX CPP AS LD AR RANLIB CMAKE_TOOLCHAIN_FILE; do
  if [[ -n "${!native_override:-}" ]]; then
    echo "Portable native build rejects inherited $native_override; unset it and rerun the native build" >&2
    exit 64
  fi
done

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
    make -f Makefile clean
    make -f Makefile "$@"
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

# A release must not inherit the build runner's optional instruction sets.
# x86-64-v2 + AES + PCLMUL is the supported Linux x64 native baseline.
# Optional higher-ISA implementations must perform runtime CPU/OS dispatch.
if [[ "$UNAME_P" != x86_64 ]]; then
  echo "Linux native builds currently support x86_64 only (got $UNAME_P)" >&2
  exit 64
fi
export CPU_FLAGS="-march=x86-64-v2 -mtune=generic -maes -mpclmul"
export HAVE_FEATURE="-DHAVE_SSE2 -DHAVE_SSE3 -DHAVE_SSSE3 -DHAVE_PCLMUL"
echo "Native CPU baseline: $CPU_FLAGS"

bash "$ScriptDir/../../scripts/release/verify-pinned-source-files.sh" \
  "$NativeDir" "$NativeDir/libodocrypt/upstream.sha256"
build_native_library libmultihash libmultihash.so \
  CPU_FLAGS="$CPU_FLAGS" HAVE_FEATURE="$HAVE_FEATURE"
build_native_library libodocrypt libodocrypt.so
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
bash "$ScriptDir/../../scripts/release/verify-pinned-source-files.sh" \
  "$NativeDir" "$NativeDir/libdero/highwayhash.sha256"
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
      "-DCMAKE_C_FLAGS=-fPIC $CPU_FLAGS" \
      -DSECP256K1_ENABLE_MODULE_RECOVERY=OFF \
      -DSECP256K1_ENABLE_COVERAGE=OFF \
      -DSECP256K1_ENABLE_MODULE_SCHNORR=ON
    cmake --build build
    cd "$NativeDir/libnexapow"
    cp "${TMPDIR:-/tmp}/secp256k1/build/libsecp256k1.a" .
    make -f Makefile clean
    make -f Makefile
  )
}

build_randomx_family() {
  local repository=$1
  local checkout=$2
  local source_name=$3
  local component=$4
  local build_target=${5:-}
  local source_patch=${6:-}
  local source_manifest=${7:-}

  if [[ -n "$source_patch" && -z "$source_manifest" ]]; then
    echo "Pinned source patch '$source_patch' requires a SHA-256 manifest" >&2
    return 64
  fi

  (
    cd "${TMPDIR:-/tmp}"
    rm -rf "$source_name"
    git clone "$repository" "$source_name"
    cd "$source_name"
    git checkout "$checkout"
    if [[ -n "$source_patch" ]]; then
      bash "$ScriptDir/../../scripts/release/verify-pinned-source-files.sh" \
        . "$ScriptDir/$source_manifest"
      git apply --check "$ScriptDir/$source_patch"
      git apply "$ScriptDir/$source_patch"
    fi
    # All four pinned forks share this CPU detector. Validate its source before
    # requiring CPU AVX/XSAVE, OSXSAVE and XCR0 XMM/YMM state for AVX2 dispatch.
    bash "$ScriptDir/../../scripts/release/verify-pinned-source-files.sh" \
      . "$ScriptDir/patches/randomx-cpu-os-state.sha256"
    git apply --check "$ScriptDir/patches/randomx-cpu-os-state.patch"
    git apply "$ScriptDir/patches/randomx-cpu-os-state.patch"
    cmake -S . -B build \
      -DARCH=default \
      "-DCMAKE_C_FLAGS=-Wa,--noexecstack $CPU_FLAGS" \
      "-DCMAKE_CXX_FLAGS=-Wa,--noexecstack $CPU_FLAGS"
    if [[ -n "$build_target" ]]; then
      cmake --build build --target "$build_target" -j"$(nproc)"
    else
      cmake --build build -j"$(nproc)"
    fi
    cd "$NativeDir/$component"
    cp "${TMPDIR:-/tmp}/$source_name/build/librandomx.a" .
    make -f Makefile clean
    make -f Makefile
  )
}

build_external_native_library libnexapow libnexapow.so build_nexapow
build_external_native_library librandomx librandomx.so \
  build_randomx_family https://github.com/tevador/RandomX tags/v1.2.1 RandomX librandomx '' \
  patches/randomx-cmake-policy-floor.patch \
  patches/randomx-cmake-policy-floor.sha256
build_external_native_library librandomarq librandomarq.so \
  build_randomx_family https://github.com/arqma/RandomARQ \
  3bcb6bafe63d70f8e6f78a0d431e71be2b638083 RandomARQ librandomarq randomx \
  patches/randomarq-cmake-policy-floor.patch \
  patches/randomarq-cmake-policy-floor.sha256
build_external_native_library libpanthera libpanthera.so \
  build_randomx_family https://github.com/scala-network/Panthera \
  cc7425f468d935ba328fba5bbb05f8227f4f22d7 Panthera libpanthera randomx \
  patches/panthera-build-status-warnings.patch \
  patches/panthera-build-status-warnings.sha256
build_external_native_library librandomxscash librandomxscash.so \
  build_randomx_family https://github.com/scashnetwork/RandomX \
  0b3e0ded68b95491516fe974e3db784ca2742ca7 RandomXSCash librandomxscash randomx \
  patches/randomxscash-cmake-policy-floor.patch \
  patches/randomxscash-cmake-policy-floor.sha256
