#!/usr/bin/env bash

# Resolve optional assembly metadata for a source build. Development and feature
# branches intentionally retain GitVersion's calculated version. A clean checkout
# at one exact SemVer release tag receives the same identity as the release workflow.
miningcore_resolve_source_build_identity() {
  local repository_root="$1"
  local output_variable="$2"
  local -n output_arguments="$output_variable"
  local candidate
  local source_commit
  local -a release_tags=()

  output_arguments=()

  if ! git -C "$repository_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
    return 0
  fi

  while IFS= read -r candidate; do
    if [[ "$candidate" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
      release_tags+=("$candidate")
    fi
  done < <(git -C "$repository_root" tag --points-at HEAD --list 'v*')

  if (( ${#release_tags[@]} == 0 )); then
    return 0
  fi

  if (( ${#release_tags[@]} > 1 )); then
    printf 'Cannot assign a source-build release identity: HEAD has multiple SemVer tags: %s\n' \
      "${release_tags[*]}" >&2
    return 1
  fi

  if [[ -n "$(git -C "$repository_root" status --porcelain --untracked-files=normal)" ]]; then
    printf 'Cannot assign release identity %s to a dirty source checkout.\n' \
      "${release_tags[0]}" >&2
    return 1
  fi

  source_commit="$(git -C "$repository_root" rev-parse 'HEAD^{commit}')"
  output_arguments+=(
    "-p:MiningcoreReleaseVersion=${release_tags[0]#v}"
    "-p:MiningcoreSourceCommit=$source_commit"
  )

  printf 'Embedding release identity %s [%s]\n' "${release_tags[0]#v}" "$source_commit"
}
