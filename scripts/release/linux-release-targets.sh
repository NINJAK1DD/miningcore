#!/usr/bin/env bash

# One release-target contract shared by workflow matrix generation, package
# assembly, and cross-job artifact collection. Keep the primary target first.
if [[ -n ${MININGCORE_LINUX_RELEASE_TARGETS+x} ]]; then
  return 0
fi

readonly MININGCORE_LINUX_RELEASE_TARGETS=(26.04 22.04)

miningcore_linux_release_target_role() {
  case "$1" in
    26.04) printf '%s\n' primary ;;
    22.04) printf '%s\n' compatibility ;;
    *) return 1 ;;
  esac
}

miningcore_linux_release_target_runner() {
  case "$1" in
    # The pinned job-container image supplies the target userspace and native
    # toolchain. Keep release orchestration on a stable hosted runner.
    26.04|22.04) printf '%s\n' ubuntu-24.04 ;;
    *) return 1 ;;
  esac
}

miningcore_linux_release_target_image_digest() {
  case "$1" in
    # Docker Official Image manifest-list digests resolved from Docker Hub on
    # 2026-08-22. Updating either image is a reviewed source change.
    26.04)
      printf '%s\n' \
        'sha256:2260313b31c8c011cd2eebe728008efac1b3982be73eb71348ea2648d2c0e09b'
      ;;
    22.04)
      printf '%s\n' \
        'sha256:2edbbc5dc405e9612ba3584ce95480277e3eb374407b5505fe26f17df77c7dbc'
      ;;
    *) return 1 ;;
  esac
}

miningcore_linux_release_target_image() {
  local target=$1
  local digest

  digest=$(miningcore_linux_release_target_image_digest "$target")
  printf 'ubuntu:%s@%s\n' "$target" "$digest"
}

miningcore_linux_release_target_supported() {
  local candidate=$1
  local target

  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    if [[ "$candidate" == "$target" ]]; then
      return 0
    fi
  done

  return 1
}

miningcore_linux_release_target_list() {
  local joined=
  local target

  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    joined+="${joined:+, }$target"
  done

  printf '%s\n' "$joined"
}

miningcore_linux_release_matrix_json() {
  local separator=
  local target
  local role
  local runner
  local image

  printf '{"include":['

  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    role=$(miningcore_linux_release_target_role "$target")
    runner=$(miningcore_linux_release_target_runner "$target")
    image=$(miningcore_linux_release_target_image "$target")
    printf '%s{"ubuntu":"%s","role":"%s","runner":"%s","image":"%s"}' \
      "$separator" "$target" "$role" "$runner" "$image"
    separator=,
  done

  printf ']}\n'
}
