#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source "$repository_root/scripts/release/publish-release-state.sh"

test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT

expected_digest="sha256:$(printf 'a%.0s' {1..64})"
readonly expected_digest
previous_digest="sha256:$(printf 'b%.0s' {1..64})"
readonly previous_digest
conflicting_digest="sha256:$(printf 'c%.0s' {1..64})"
readonly conflicting_digest

fail() {
  echo "$*" >&2
  exit 1
}

reset_fake_services() {
  local scenario=$1

  FAKE_ROOT="$test_root/$scenario"
  FAKE_RELEASE_STATE=absent
  FAKE_API_FAILURE=false
  FAKE_REGISTRY_FAILURE=false
  FAKE_OTHER_RELEASE_TAGS=()
  mkdir -p "$FAKE_ROOT/assets" "$FAKE_ROOT/candidate"
  printf 'ubuntu 26.04 archive\n' > \
    "$FAKE_ROOT/candidate/miningcore-v1.2.3-linux-x64-ubuntu-26.04.tar.gz"
  printf 'ubuntu 22.04 archive\n' > \
    "$FAKE_ROOT/candidate/miningcore-v1.2.3-linux-x64-ubuntu-22.04.tar.gz"
  printf 'checksums\n' > "$FAKE_ROOT/candidate/SHA256SUMS"
  printf 'release notes\n' > "$FAKE_ROOT/release-notes.md"
  : > "$FAKE_ROOT/github-output"

  declare -gA FAKE_REFERENCES=()
  export GITHUB_REPOSITORY=example/miningcore
  export GITHUB_REF_NAME=v1.2.3
  export MININGCORE_SOURCE_COMMIT=0123456789abcdef0123456789abcdef01234567
  export MININGCORE_IMAGE=ghcr.io/example/miningcore
  export MININGCORE_RELEASE_ASSET_DIR="$FAKE_ROOT/candidate"
  export MININGCORE_RELEASE_NOTES_FILE="$FAKE_ROOT/release-notes.md"
  export GITHUB_OUTPUT="$FAKE_ROOT/github-output"
  unset MININGCORE_CONTAINER_DIGEST
}

fake_release_json() {
  local draft=false
  local assets_json

  if [[ "$FAKE_RELEASE_STATE" == draft ]]; then
    draft=true
  fi
  assets_json=$(find "$FAKE_ROOT/assets" -maxdepth 1 -type f -printf '%f\n' |
    sort | jq -Rsc \
      'split("\n")[:-1] | to_entries | map({name: .value, id: (.key + 100)})')
  jq -n --arg tag "$GITHUB_REF_NAME" --argjson draft "$draft" \
    --arg name "Miningcore $GITHUB_REF_NAME" \
    --rawfile body "$MININGCORE_RELEASE_NOTES_FILE" \
    --argjson assets "$assets_json" \
    '{
      id: 1,
      tag_name: $tag,
      name: $name,
      draft: $draft,
      prerelease: false,
      body: $body,
      assets: $assets
    }'
}

gh() {
  local asset_index
  local asset_name
  local endpoint
  local other_tag
  local source

  if [[ "$1" == api ]]; then
    endpoint=${*: -1}
    if [[ "$FAKE_API_FAILURE" == true ]]; then
      echo 'HTTP 503: service unavailable' >&2
      return 1
    fi

    if [[ "$endpoint" == "repos/$GITHUB_REPOSITORY/releases/tags/$GITHUB_REF_NAME" ]]; then
      if [[ "$FAKE_RELEASE_STATE" == absent ]]; then
        echo 'HTTP 404: Not Found' >&2
        return 1
      fi
      fake_release_json
      return
    fi

    if [[ "$endpoint" == "repos/$GITHUB_REPOSITORY/releases?per_page=100" ]]; then
      {
        fake_release_json
        for other_tag in "${FAKE_OTHER_RELEASE_TAGS[@]}"; do
          jq -n --arg tag "$other_tag" \
            '{tag_name: $tag, draft: false, prerelease: false}'
        done
      } | jq -s '[.]'
      return
    fi

    if [[ "$endpoint" == repos/$GITHUB_REPOSITORY/releases/assets/* ]]; then
      asset_index=$((${endpoint##*/} - 99))
      asset_name=$(find "$FAKE_ROOT/assets" -maxdepth 1 -type f -printf '%f\n' |
        sort | sed -n "${asset_index}p")
      [[ -n "$asset_name" ]] || fail "Unknown fake release asset id: ${endpoint##*/}"
      cat "$FAKE_ROOT/assets/$asset_name"
      return
    fi

    fail "Unexpected fake gh api call: $*"
  fi

  if [[ "$1 $2" == 'release create' ]]; then
    [[ "$FAKE_RELEASE_STATE" == absent ]] || fail 'Draft was created twice'
    FAKE_RELEASE_STATE=draft
    return
  fi

  if [[ "$1 $2" == 'release upload' ]]; then
    [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Asset upload targeted a non-draft release'
    source=${*: -1}
    [[ ! -e "$FAKE_ROOT/assets/${source##*/}" ]] || fail 'Asset overwrite was attempted'
    cp -- "$source" "$FAKE_ROOT/assets/${source##*/}"
    return
  fi

  if [[ "$1 $2" == 'release edit' ]]; then
    [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Only a draft may be published'
    FAKE_RELEASE_STATE=published
    return
  fi

  fail "Unexpected fake gh call: $*"
}

docker() {
  local reference
  local target
  local source

  if [[ "$1 $2 $3" == 'buildx imagetools inspect' ]]; then
    reference=${*: -1}
    if [[ "$FAKE_REGISTRY_FAILURE" == true ]]; then
      echo 'registry request timed out' >&2
      return 1
    fi
    if [[ -z ${FAKE_REFERENCES[$reference]+x} ]]; then
      echo 'manifest unknown' >&2
      return 1
    fi
    jq -n --arg digest "${FAKE_REFERENCES[$reference]}" '$digest'
    return
  fi

  if [[ "$1 $2 $3" == 'buildx imagetools create' ]]; then
    target=$6
    source=$7
    [[ "$source" == "$MININGCORE_IMAGE@"* ]] || fail "Unexpected promotion source: $source"
    FAKE_REFERENCES[$target]=${source#*@}
    return
  fi

  fail "Unexpected fake docker call: $*"
}

run_command() {
  publication_main "$1"
}

assert_reference_absent() {
  local reference=$1
  [[ -z ${FAKE_REFERENCES[$reference]+x} ]] ||
    fail "Reference moved before durable publication: $reference"
}

assert_reference_digest() {
  local reference=$1
  local digest=$2
  [[ ${FAKE_REFERENCES[$reference]:-} == "$digest" ]] ||
    fail "Reference $reference does not point to $digest"
}

scenario_interrupted_publication_resumes() (
  reset_fake_services interrupted
  FAKE_REFERENCES["$MININGCORE_IMAGE:1.2"]=$previous_digest
  FAKE_REFERENCES["$MININGCORE_IMAGE:latest"]=$previous_digest

  run_command prepare
  [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Prepare did not create a draft'
  grep -Fxq 'needs_container_build=true' "$GITHUB_OUTPUT"

  # This models docker/build-push-action completing and the workflow then
  # failing after record, before the draft is published.
  FAKE_REFERENCES["$PUBLICATION_STAGING_REFERENCE"]=$expected_digest
  export MININGCORE_CONTAINER_DIGEST=$expected_digest
  run_command record

  [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Fault point unexpectedly published the release'
  assert_reference_absent "$MININGCORE_IMAGE:$GITHUB_REF_NAME"
  assert_reference_absent "$MININGCORE_IMAGE:1.2.3"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$previous_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$previous_digest"

  : > "$GITHUB_OUTPUT"
  run_command prepare
  grep -Fxq 'needs_container_build=false' "$GITHUB_OUTPUT"
  grep -Fxq "staging_digest=$expected_digest" "$GITHUB_OUTPUT"
  run_command record
  run_command publish
  run_command promote

  [[ "$FAKE_RELEASE_STATE" == published ]] || fail 'Retry did not publish the draft'
  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$expected_digest"
)

scenario_conflicting_version_tag_fails_closed() (
  reset_fake_services conflict
  run_command prepare
  FAKE_REFERENCES["$PUBLICATION_STAGING_REFERENCE"]=$expected_digest
  export MININGCORE_CONTAINER_DIGEST=$expected_digest
  run_command record
  run_command publish
  FAKE_REFERENCES["$MININGCORE_IMAGE:$GITHUB_REF_NAME"]=$conflicting_digest
  run_command promote
)

scenario_changed_asset_is_not_overwritten() (
  reset_fake_services changed-asset
  FAKE_RELEASE_STATE=draft
  printf 'different bytes\n' > \
    "$FAKE_ROOT/assets/miningcore-v1.2.3-linux-x64-ubuntu-26.04.tar.gz"
  run_command prepare
)

scenario_non_authoritative_api_failure_is_fatal() (
  reset_fake_services api-failure
  FAKE_API_FAILURE=true
  run_command prepare
)

scenario_non_authoritative_registry_failure_is_fatal() (
  reset_fake_services registry-failure
  FAKE_REGISTRY_FAILURE=true
  run_command prepare
)

scenario_stage_without_release_is_ambiguous() (
  reset_fake_services orphan-stage
  FAKE_REFERENCES["$MININGCORE_IMAGE:publication-staging-$GITHUB_REF_NAME"]=$expected_digest
  run_command prepare
)

scenario_older_rerun_preserves_newer_aliases() (
  reset_fake_services older-rerun
  run_command prepare
  FAKE_REFERENCES["$PUBLICATION_STAGING_REFERENCE"]=$expected_digest
  export MININGCORE_CONTAINER_DIGEST=$expected_digest
  run_command record
  run_command publish

  FAKE_OTHER_RELEASE_TAGS=(v1.2.4)
  FAKE_REFERENCES["$MININGCORE_IMAGE:1.2"]=$conflicting_digest
  FAKE_REFERENCES["$MININGCORE_IMAGE:latest"]=$conflicting_digest
  run_command promote

  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$conflicting_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$conflicting_digest"
)

scenario_interrupted_publication_resumes
scenario_older_rerun_preserves_newer_aliases

for scenario in \
  scenario_conflicting_version_tag_fails_closed \
  scenario_changed_asset_is_not_overwritten \
  scenario_non_authoritative_api_failure_is_fatal \
  scenario_non_authoritative_registry_failure_is_fatal \
  scenario_stage_without_release_is_ambiguous; do
  set +e
  "$scenario" > "$test_root/$scenario.out" 2>&1
  status=$?
  set -e
  if [[ "$status" -ne 70 ]]; then
    cat "$test_root/$scenario.out" >&2
    fail "$scenario returned $status instead of fail-closed status 70"
  fi
  grep -Fq 'HUMAN ACTION REQUIRED:' "$test_root/$scenario.out" ||
    fail "$scenario did not provide a human-action diagnostic"
done

# Pin the workflow boundary as well as the shell state machine: tag promotion
# must remain textually after durable release publication.
workflow="$repository_root/.github/workflows/release.yml"
publish_line=$(grep -nF 'name: Publish and verify durable GitHub Release' "$workflow" | cut -d: -f1)
promote_line=$(grep -nF 'name: Promote and verify release container tags' "$workflow" | cut -d: -f1)
if [[ -z "$publish_line" || -z "$promote_line" || "$promote_line" -le "$publish_line" ]]; then
  fail 'Release workflow no longer orders tag promotion after durable publication'
fi

echo 'Recoverable release publication state tests passed'
