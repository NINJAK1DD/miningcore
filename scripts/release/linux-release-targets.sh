#!/usr/bin/env bash

# One release-target contract shared by workflow matrix generation, package
# assembly, and cross-job artifact collection. Keep the primary target first.
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
    26.04) printf '%s\n' ubuntu-26.04 ;;
    # Build Jammy in its own userspace on a maintained hosted runner. This
    # preserves the archive after GitHub retires the ubuntu-22.04 runner image.
    22.04) printf '%s\n' ubuntu-24.04 ;;
    *) return 1 ;;
  esac
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

  printf '{"include":['

  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    role=$(miningcore_linux_release_target_role "$target")
    runner=$(miningcore_linux_release_target_runner "$target")
    printf '%s{"ubuntu":"%s","role":"%s","runner":"%s"}' \
      "$separator" "$target" "$role" "$runner"
    separator=,
  done

  printf ']}\n'
}
