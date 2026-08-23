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

# Keep publication visibility retries deterministic and fast in this hermetic
# test. The production script still invokes the real sleep command.
sleep() {
  :
}

fake_has_argument() {
  local wanted=$1
  shift
  local argument

  for argument in "$@"; do
    [[ "$argument" == "$wanted" ]] && return 0
  done
  return 1
}

fake_argument_after() {
  local wanted=$1
  shift

  while [[ $# -gt 0 ]]; do
    if [[ "$1" == "$wanted" ]]; then
      [[ $# -ge 2 ]] || fail "Fake command option '$wanted' lacks a value"
      printf '%s\n' "$2"
      return
    fi
    shift
  done
  return 1
}

reset_fake_services() {
  local scenario=$1
  local release_tag=${2:-v1.2.3}

  FAKE_ROOT="$test_root/$scenario"
  FAKE_RELEASE_STATE=absent
  FAKE_GH_VERSION=2.79.0
  FAKE_GH_VERSION_FAILURE=false
  FAKE_GH_VERSION_WARNING=false
  FAKE_RELEASE_LATEST=false
  FAKE_RELEASE_LAST_PUBLISH_LATEST=
  FAKE_RELEASE_PRERELEASE=
  FAKE_DRAFT_VISIBILITY_LAG_AFTER_CREATE=0
  FAKE_RELEASE_LIST_ABSENT_READS_REMAINING=0
  FAKE_RELEASE_LIST_DRAFT_READS_REMAINING=0
  FAKE_ASSET_VISIBILITY_LAG_AFTER_UPLOAD=0
  FAKE_RELEASE_LIST_ASSET_READS_REMAINING=0
  FAKE_LAST_UPLOADED_ASSET=
  FAKE_ASSET_HIDDEN_THIS_READ=false
  FAKE_API_FAILURE=false
  FAKE_API_NOT_FOUND_FAILURE=false
  FAKE_REGISTRY_FAILURE=false
  FAKE_UPLOAD_FAILURE=false
  FAKE_ASSET_DIGESTS_AVAILABLE=true
  FAKE_DUPLICATE_CURRENT_RELEASE=false
  FAKE_OTHER_RELEASE_TAGS=()
  FAKE_RELEASE_LIST_CALLS=0
  FAKE_RELEASE_ID_GET_CALLS=0
  mkdir -p "$FAKE_ROOT/assets" "$FAKE_ROOT/candidate"

  export GITHUB_REPOSITORY=example/miningcore
  export GITHUB_REF_NAME=$release_tag
  export MININGCORE_SOURCE_COMMIT=0123456789abcdef0123456789abcdef01234567
  export MININGCORE_IMAGE=ghcr.io/example/miningcore
  export MININGCORE_RELEASE_ASSET_DIR="$FAKE_ROOT/candidate"
  export MININGCORE_RELEASE_NOTES_FILE="$FAKE_ROOT/release-notes.md"
  export GITHUB_OUTPUT="$FAKE_ROOT/github-output"
  # Exercise GitHub's stateless installation-token shape plus every
  # punctuation character permitted by the Bearer credential grammar. The
  # publisher must treat the token as opaque rather than pinning its format.
  export GH_TOKEN='ghs_12345_eyJhbGciOi.test-._~+/='

  FAKE_RELEASE_NAME="Miningcore $release_tag"
  FAKE_EXPECTED_DRAFT_MARKER="<!-- miningcore-release-publication:v1"
  FAKE_EXPECTED_DRAFT_MARKER+=" repository=$GITHUB_REPOSITORY tag=$release_tag"
  FAKE_EXPECTED_DRAFT_MARKER+=" source=${MININGCORE_SOURCE_COMMIT,,} -->"
  FAKE_RELEASE_BODY=$'release notes\n\n'"$FAKE_EXPECTED_DRAFT_MARKER"

  printf 'ubuntu 26.04 archive\n' > \
    "$FAKE_ROOT/candidate/miningcore-$release_tag-linux-x64-ubuntu-26.04.tar.gz"
  printf 'ubuntu 22.04 archive\n' > \
    "$FAKE_ROOT/candidate/miningcore-$release_tag-linux-x64-ubuntu-22.04.tar.gz"
  printf 'checksums\n' > "$FAKE_ROOT/candidate/SHA256SUMS"
  printf 'release notes\n' > "$MININGCORE_RELEASE_NOTES_FILE"
  : > "$GITHUB_OUTPUT"

  declare -gA FAKE_REFERENCES=()
  unset MININGCORE_CONTAINER_DIGEST
}

fake_asset_json() {
  local asset_name=$1
  local asset_file=$FAKE_ROOT/assets/$asset_name
  local asset_index
  local digest
  local size

  asset_index=$(find "$FAKE_ROOT/assets" -maxdepth 1 -type f -printf '%f\n' |
    sort | grep -nFx "$asset_name" | cut -d: -f1)
  [[ -n "$asset_index" ]] || fail "Unknown fake release asset: $asset_name"
  digest=sha256:$(sha256sum "$asset_file" | awk '{print $1}')
  if [[ "$FAKE_ASSET_DIGESTS_AVAILABLE" != true ]]; then
    digest=
  fi
  size=$(stat -c '%s' "$asset_file")
  jq -n --arg name "$asset_name" --arg digest "$digest" \
    --argjson id "$((asset_index + 99))" --argjson size "$size" \
    '{name: $name, id: $id, state: "uploaded", size: $size,
      digest: (if $digest == "" then null else $digest end)}'
}

fake_release_json() {
  local visible_state=${1:-$FAKE_RELEASE_STATE}
  local draft=false
  local prerelease=false
  local assets_json

  [[ "$visible_state" == draft ]] && draft=true
  [[ "$GITHUB_REF_NAME" == *-* ]] && prerelease=true
  if [[ -n "$FAKE_RELEASE_PRERELEASE" ]]; then
    prerelease=$FAKE_RELEASE_PRERELEASE
  fi

  assets_json=$(while IFS= read -r asset_name; do
    if [[ -n "$asset_name" &&
        ! ( "$FAKE_ASSET_HIDDEN_THIS_READ" == true &&
          "$asset_name" == "$FAKE_LAST_UPLOADED_ASSET" ) ]]; then
      fake_asset_json "$asset_name"
    fi
  done < <(find "$FAKE_ROOT/assets" -maxdepth 1 -type f -printf '%f\n' | sort) | jq -s '.')

  jq -n --arg tag "$GITHUB_REF_NAME" --argjson draft "$draft" \
    --argjson prerelease "$prerelease" --argjson latest "$FAKE_RELEASE_LATEST" \
    --arg name "$FAKE_RELEASE_NAME" --arg body "$FAKE_RELEASE_BODY" \
    --argjson assets "$assets_json" \
    '{id: 1, tag_name: $tag, name: $name, draft: $draft,
      prerelease: $prerelease, make_latest: $latest, body: $body, assets: $assets}'
}

fake_other_release_json() {
  local tag=$1

  jq -n --arg tag "$tag" \
    '{id: 2, tag_name: $tag, name: $tag, draft: false, prerelease: false,
      body: "other release", assets: []}'
}

fake_prepare_visible_release() {
  FAKE_VISIBLE_RELEASE_STATE=$FAKE_RELEASE_STATE
  FAKE_INCLUDE_CURRENT_RELEASE=true
  FAKE_ASSET_HIDDEN_THIS_READ=false

  if [[ "$FAKE_RELEASE_STATE" == draft &&
      "$FAKE_RELEASE_LIST_ABSENT_READS_REMAINING" -gt 0 ]]; then
    FAKE_INCLUDE_CURRENT_RELEASE=false
    FAKE_RELEASE_LIST_ABSENT_READS_REMAINING=$((
      FAKE_RELEASE_LIST_ABSENT_READS_REMAINING - 1))
  fi

  if [[ "$FAKE_RELEASE_STATE" == draft &&
      "$FAKE_RELEASE_LIST_ASSET_READS_REMAINING" -gt 0 ]]; then
    FAKE_ASSET_HIDDEN_THIS_READ=true
    FAKE_RELEASE_LIST_ASSET_READS_REMAINING=$((
      FAKE_RELEASE_LIST_ASSET_READS_REMAINING - 1))
  fi

  if [[ "$FAKE_RELEASE_STATE" == published &&
      "$FAKE_RELEASE_LIST_DRAFT_READS_REMAINING" -gt 0 ]]; then
    FAKE_VISIBLE_RELEASE_STATE=draft
    FAKE_RELEASE_LIST_DRAFT_READS_REMAINING=$((
      FAKE_RELEASE_LIST_DRAFT_READS_REMAINING - 1))
  fi
}

fake_release_list() {
  local other_tag
  local page_file=$FAKE_ROOT/release-page.jsonl

  FAKE_RELEASE_LIST_CALLS=$((FAKE_RELEASE_LIST_CALLS + 1))
  fake_prepare_visible_release

  : > "$page_file"
  if [[ "$FAKE_RELEASE_STATE" != absent &&
      "$FAKE_INCLUDE_CURRENT_RELEASE" == true ]]; then
    fake_release_json "$FAKE_VISIBLE_RELEASE_STATE" >> "$page_file"
    if [[ "$FAKE_DUPLICATE_CURRENT_RELEASE" == true ]]; then
      fake_release_json "$FAKE_VISIBLE_RELEASE_STATE" >> "$page_file"
    fi
  fi
  for other_tag in "${FAKE_OTHER_RELEASE_TAGS[@]}"; do
    fake_other_release_json "$other_tag" >> "$page_file"
  done
  jq -s '[.]' "$page_file"
}

gh() {
  local asset_id
  local asset_index
  local asset_name
  local draft_value
  local endpoint
  local latest_value
  local source
  local title

  if [[ "$1" == --version ]]; then
    if [[ "$FAKE_GH_VERSION_FAILURE" == true ]]; then
      echo 'simulated GitHub CLI version probe failure' >&2
      return 1
    fi
    if [[ "$FAKE_GH_VERSION_WARNING" == true ]]; then
      echo 'simulated non-fatal GitHub CLI warning' >&2
    fi
    echo "gh version $FAKE_GH_VERSION (test double)"
    return
  fi

  if [[ "$1" == api ]]; then
    endpoint=${*: -1}
    if [[ "$FAKE_API_NOT_FOUND_FAILURE" == true ]]; then
      echo 'HTTP 404: Not Found' >&2
      return 1
    fi
    if [[ "$FAKE_API_FAILURE" == true ]]; then
      echo 'HTTP 503: service unavailable' >&2
      return 1
    fi

    if [[ "$endpoint" == "repos/$GITHUB_REPOSITORY/releases?per_page=100" ]]; then
      fake_release_list
      return
    fi

    # Match GitHub's contract: tag lookup sees published releases, while the
    # authenticated release list is required to discover drafts.
    if [[ "$endpoint" == "repos/$GITHUB_REPOSITORY/releases/tags/$GITHUB_REF_NAME" ]]; then
      if [[ "$FAKE_RELEASE_STATE" != published ]]; then
        echo 'HTTP 404: Not Found' >&2
        return 1
      fi
      fake_release_json published
      return
    fi

    if [[ "$endpoint" == repos/$GITHUB_REPOSITORY/releases/assets/* ]]; then
      asset_id=${endpoint##*/}
      asset_index=$((asset_id - 99))
      asset_name=$(find "$FAKE_ROOT/assets" -maxdepth 1 -type f -printf '%f\n' |
        sort | sed -n "${asset_index}p")
      [[ -n "$asset_name" ]] || fail "Unknown fake release asset id: $asset_id"
      cat "$FAKE_ROOT/assets/$asset_name"
      return
    fi

    if [[ "$endpoint" == "repos/$GITHUB_REPOSITORY/releases/1" ]]; then
      if fake_has_argument --method "$@"; then
        [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Only a draft may be published'
        [[ $(fake_argument_after --method "$@") == PATCH ]] ||
          fail 'Release publication did not use PATCH by release id'
        draft_value=$(fake_argument_after -F "$@")
        latest_value=$(fake_argument_after -f "$@")
        [[ "$draft_value" == draft=false ]] || fail 'Release publication did not set draft=false'
        [[ "$latest_value" == make_latest=* ]] || fail 'Release publication omitted make_latest'
        latest_value=${latest_value#make_latest=}
        [[ "$latest_value" == true || "$latest_value" == false ]] ||
          fail 'Release publication supplied an invalid make_latest value'
        if [[ "$GITHUB_REF_NAME" == *-* && "$latest_value" != false ]]; then
          fail 'Prerelease publication attempted to become latest'
        fi
        FAKE_RELEASE_STATE=published
        FAKE_RELEASE_LATEST=$latest_value
        FAKE_RELEASE_LAST_PUBLISH_LATEST=$latest_value
        fake_release_json published
        return
      fi

      FAKE_RELEASE_ID_GET_CALLS=$((FAKE_RELEASE_ID_GET_CALLS + 1))
      fake_prepare_visible_release
      if [[ "$FAKE_INCLUDE_CURRENT_RELEASE" != true ]]; then
        echo 'HTTP 404: Not Found' >&2
        return 1
      fi
      fake_release_json "$FAKE_VISIBLE_RELEASE_STATE"
      return
    fi

    fail "Unexpected fake gh api call: $*"
  fi

  if [[ "$1 $2" == 'release create' ]]; then
    [[ "$FAKE_RELEASE_STATE" == absent ]] || fail 'Draft was created twice'
    [[ "$3" == "$GITHUB_REF_NAME" ]] || fail 'Draft used the wrong release tag'
    fake_has_argument --draft "$@" || fail 'Draft creation omitted --draft'
    fake_has_argument --verify-tag "$@" || fail 'Draft creation omitted --verify-tag'
    fake_has_argument --generate-notes "$@" || fail 'Draft creation omitted --generate-notes'
    fake_has_argument --latest=false "$@" || fail 'Draft creation did not force --latest=false'
    ! fake_has_argument --latest=true "$@" || fail 'Draft creation requested latest=true'
    title=$(fake_argument_after --title "$@")
    [[ "$title" == "Miningcore $GITHUB_REF_NAME" ]] || fail 'Draft used wrong release title'
    source=$(fake_argument_after --notes-file "$@")
    [[ "$source" != "$MININGCORE_RELEASE_NOTES_FILE" ]] ||
      fail 'Draft ownership marker was not isolated in a generated notes file'
    grep -Fq "$FAKE_EXPECTED_DRAFT_MARKER" "$source" ||
      fail 'Draft notes omitted the workflow ownership marker'
    grep -Fq 'release notes' "$source" || fail 'Draft notes omitted the release preamble'
    if [[ "$GITHUB_REF_NAME" == *-* ]]; then
      fake_has_argument --prerelease "$@" || fail 'Prerelease draft omitted --prerelease'
    else
      ! fake_has_argument --prerelease "$@" || fail 'Stable draft was marked prerelease'
    fi
    FAKE_RELEASE_NAME=$title
    FAKE_RELEASE_BODY=$(< "$source")
    FAKE_RELEASE_STATE=draft
    FAKE_RELEASE_LIST_ABSENT_READS_REMAINING=$FAKE_DRAFT_VISIBILITY_LAG_AFTER_CREATE
    return
  fi

  if [[ "$1 $2" == 'release upload' || "$1 $2" == 'release edit' ]]; then
    fail 'Release mutation must use the retained release id, not a tag-based gh command'
  fi

  fail "Unexpected fake gh call: $*"
}

curl() {
  local config_file
  local escaped_token
  local expected_authorization_line
  local output_file
  local source
  local url=${*: -1}
  local asset_name=${url##*name=}

  [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Asset upload targeted a non-draft release'
  fake_has_argument --fail-with-body "$@" || fail 'Asset upload omitted fail-with-body'
  fake_has_argument --silent "$@" || fail 'Asset upload omitted silent mode'
  fake_has_argument --show-error "$@" || fail 'Asset upload omitted error reporting'
  ! fake_has_argument --location "$@" || fail 'Asset upload follows authenticated redirects'
  ! fake_has_argument --data-binary "$@" || fail 'Asset upload buffers the archive as form data'
  [[ $(fake_argument_after --retry "$@") == 4 ]] || fail 'Asset upload retry count changed'
  [[ $(fake_argument_after --retry-delay "$@") == 2 ]] || fail 'Asset retry delay changed'
  [[ $(fake_argument_after --retry-max-time "$@") == 120 ]] ||
    fail 'Asset retry budget changed'
  [[ $(fake_argument_after --connect-timeout "$@") == 20 ]] ||
    fail 'Asset connect timeout changed'
  [[ $(fake_argument_after --max-time "$@") == 300 ]] || fail 'Asset timeout changed'
  [[ $(fake_argument_after --write-out "$@") == '%{http_code}' ]] ||
    fail 'Asset upload omitted its HTTP status contract'
  [[ $(fake_argument_after --request "$@") == POST ]] ||
    fail 'Release asset upload did not use POST'
  config_file=$(fake_argument_after --config "$@")
  output_file=$(fake_argument_after --output "$@")
  source=$(fake_argument_after --upload-file "$@")

  [[ "$url" == \
    "https://uploads.github.com/repos/$GITHUB_REPOSITORY/releases/1/assets?name=$asset_name" ]] ||
    fail 'Release asset upload did not use the retained release id'
  escaped_token=$(publication_escape_curl_config_value "$GH_TOKEN")
  expected_authorization_line="header = \"Authorization: Bearer $escaped_token\""
  grep -Fxq "$expected_authorization_line" "$config_file" ||
    fail 'Release asset upload did not isolate its bearer token in the private curl config'
  [[ "${source##*/}" == "$asset_name" ]] || fail 'Asset upload name changed'
  if [[ "$FAKE_UPLOAD_FAILURE" == true ]]; then
    printf '{"message":"simulated GitHub upload rejection"}\n' > "$output_file"
    echo 'curl: (22) The requested URL returned error: 503' >&2
    printf '503'
    return 22
  fi
  [[ ! -e "$FAKE_ROOT/assets/$asset_name" ]] || fail 'Asset overwrite was attempted'
  cp -- "$source" "$FAKE_ROOT/assets/$asset_name"
  FAKE_LAST_UPLOADED_ASSET=$asset_name
  FAKE_RELEASE_LIST_ASSET_READS_REMAINING=$FAKE_ASSET_VISIBILITY_LAG_AFTER_UPLOAD
  fake_asset_json "$asset_name" > "$output_file"
  printf '201'
}

docker() {
  local prefer_index=false
  local reference
  local source=
  local target=

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
    shift 3
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --prefer-index=false)
          prefer_index=true
          shift
          ;;
        --tag)
          [[ $# -ge 2 ]] || fail 'Promotion --tag lacks a value'
          target=$2
          shift 2
          ;;
        --*)
          fail "Unexpected promotion option: $1"
          ;;
        *)
          [[ -z "$source" ]] || fail 'Promotion supplied multiple sources'
          source=$1
          shift
          ;;
      esac
    done
    [[ "$prefer_index" == true ]] || fail 'Promotion omitted --prefer-index=false'
    [[ -n "$target" ]] || fail 'Promotion omitted its target tag'
    [[ "$source" == "$MININGCORE_IMAGE@"* ]] || fail "Unexpected promotion source: $source"
    FAKE_REFERENCES[$target]=${source#*@}
    return
  fi

  fail "Unexpected fake docker call: $*"
}

run_command() {
  # Each Actions step invokes a new shell process. Clear production state that
  # would otherwise leak between these in-process hermetic command calls.
  unset PUBLICATION_RELEASE_ID PUBLICATION_RELEASE_STATE
  unset PUBLICATION_ASSET_ID PUBLICATION_ASSET_DIGEST
  unset PUBLICATION_ASSET_SIZE PUBLICATION_ASSET_STATE
  unset PUBLICATION_RECORDED_DIGEST PUBLICATION_INSPECTED_DIGEST
  unset PUBLICATION_MAY_PROMOTE_LINE_ALIAS PUBLICATION_MAY_PROMOTE_LATEST
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

complete_publication() {
  run_command prepare
  FAKE_REFERENCES["$PUBLICATION_STAGING_REFERENCE"]=$expected_digest
  export MININGCORE_CONTAINER_DIGEST=$expected_digest
  run_command record
  run_command publish
  run_command promote
}

scenario_interrupted_publication_resumes() (
  reset_fake_services interrupted
  FAKE_DRAFT_VISIBILITY_LAG_AFTER_CREATE=2
  FAKE_ASSET_VISIBILITY_LAG_AFTER_UPLOAD=2
  FAKE_REFERENCES["$MININGCORE_IMAGE:1.2"]=$previous_digest
  FAKE_REFERENCES["$MININGCORE_IMAGE:latest"]=$previous_digest

  run_command prepare
  [[ "$FAKE_RELEASE_STATE" == draft ]] || fail 'Prepare did not create a draft'
  grep -Fxq 'needs_container_build=true' "$GITHUB_OUTPUT"

  if gh api "repos/$GITHUB_REPOSITORY/releases/tags/$GITHUB_REF_NAME" \
      > /dev/null 2>&1; then
    fail 'Fake tag endpoint exposed a draft release'
  fi

  # Model docker/build-push-action completing and then failing after record,
  # before the draft is published.
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
  FAKE_RELEASE_LIST_DRAFT_READS_REMAINING=2
  run_command publish
  run_command promote

  [[ "$FAKE_RELEASE_STATE" == published ]] || fail 'Retry did not publish the draft'
  [[ "$FAKE_RELEASE_LAST_PUBLISH_LATEST" == true ]] ||
    fail 'Newest stable release was not marked latest at durable publication'
  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$expected_digest"
  [[ "$FAKE_RELEASE_ID_GET_CALLS" -gt 0 ]] ||
    fail 'Retained release ids were not used after list-based discovery'
)

scenario_older_rerun_preserves_newer_surfaces() (
  reset_fake_services older-rerun
  FAKE_OTHER_RELEASE_TAGS=(v1.2.4 v2.0.0)
  FAKE_REFERENCES["$MININGCORE_IMAGE:1.2"]=$conflicting_digest
  FAKE_REFERENCES["$MININGCORE_IMAGE:latest"]=$conflicting_digest

  complete_publication

  [[ "$FAKE_RELEASE_LAST_PUBLISH_LATEST" == false ]] ||
    fail 'Older stable release moved GitHub latest backward'
  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$conflicting_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$conflicting_digest"
)

scenario_prerelease_never_moves_mutable_surfaces() (
  reset_fake_services prerelease v1.2.3-rc.1
  FAKE_REFERENCES["$MININGCORE_IMAGE:1.2"]=$previous_digest
  FAKE_REFERENCES["$MININGCORE_IMAGE:latest"]=$previous_digest

  complete_publication

  [[ "$FAKE_RELEASE_LAST_PUBLISH_LATEST" == false ]] ||
    fail 'Prerelease became GitHub latest'
  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3-rc.1" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2" "$previous_digest"
  assert_reference_digest "$MININGCORE_IMAGE:latest" "$previous_digest"
)

scenario_completed_release_survives_pruned_stage_and_metadata_edits() (
  reset_fake_services completed-rerun
  complete_publication

  unset 'FAKE_REFERENCES[$PUBLICATION_STAGING_REFERENCE]'
  FAKE_RELEASE_NAME='Human-edited title'
  FAKE_RELEASE_BODY='Human-edited notes after durable publication'
  : > "$GITHUB_OUTPUT"

  run_command prepare
  grep -Fxq 'needs_container_build=false' "$GITHUB_OUTPUT"
  grep -Fxq "staging_digest=$expected_digest" "$GITHUB_OUTPUT"
  run_command record
  run_command publish
  run_command promote

  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
)

scenario_legacy_assets_without_server_digests_use_byte_fallback() (
  reset_fake_services legacy-asset-digests
  FAKE_ASSET_DIGESTS_AVAILABLE=false

  complete_publication

  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
)

scenario_completed_release_recovers_from_one_immutable_tag() (
  reset_fake_services one-immutable-tag
  complete_publication

  unset 'FAKE_REFERENCES[$PUBLICATION_STAGING_REFERENCE]'
  unset 'FAKE_REFERENCES[$MININGCORE_IMAGE:1.2.3]'
  : > "$GITHUB_OUTPUT"

  run_command prepare
  grep -Fxq 'needs_container_build=false' "$GITHUB_OUTPUT"
  run_command promote

  assert_reference_digest "$MININGCORE_IMAGE:$GITHUB_REF_NAME" "$expected_digest"
  assert_reference_digest "$MININGCORE_IMAGE:1.2.3" "$expected_digest"
)

scenario_missing_asset_clears_stale_state() (
  reset_fake_services stale-asset-state
  FAKE_RELEASE_STATE=draft
  publication_validate_environment
  publication_load_candidate_assets
  PUBLICATION_WORK_DIR=$(mktemp -d)
  trap 'rm -rf -- "$PUBLICATION_WORK_DIR"' EXIT
  export PUBLICATION_WORK_DIR
  publication_refresh_release

  PUBLICATION_ASSET_ID=999
  PUBLICATION_ASSET_DIGEST=$expected_digest
  PUBLICATION_ASSET_SIZE=123
  PUBLICATION_ASSET_STATE=uploaded
  if publication_asset_id absent-asset; then
    fail 'Missing asset lookup unexpectedly succeeded'
  fi
  [[ -z "$PUBLICATION_ASSET_ID" && -z "$PUBLICATION_ASSET_DIGEST" &&
      -z "$PUBLICATION_ASSET_SIZE" && -z "$PUBLICATION_ASSET_STATE" ]] ||
    fail 'Missing asset lookup retained stale asset metadata'
)

scenario_opaque_token_config_escaping_is_injection_safe() (
  reset_fake_services opaque-token
  GH_TOKEN='opaque"token\value'

  publication_validate_environment
  [[ $(publication_escape_curl_config_value "$GH_TOKEN") == \
      'opaque\"token\\value' ]] ||
    fail 'Opaque token escaping did not protect curl config syntax'
  run_command prepare
)

scenario_control_character_token_fails_closed() (
  reset_fake_services control-token
  GH_TOKEN=$'opaque-token\nheader = "Injected: value"'
  publication_validate_environment
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

scenario_release_list_404_is_not_absence() (
  reset_fake_services api-hidden-not-found
  FAKE_API_NOT_FOUND_FAILURE=true
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

scenario_duplicate_release_tag_is_ambiguous() (
  reset_fake_services duplicate-release
  FAKE_RELEASE_STATE=draft
  FAKE_DUPLICATE_CURRENT_RELEASE=true
  run_command prepare
)

scenario_foreign_same_tag_draft_fails_closed() (
  reset_fake_services foreign-draft
  FAKE_RELEASE_STATE=draft
  FAKE_RELEASE_NAME='Foreign release title'
  run_command prepare
)

scenario_same_tag_draft_without_marker_fails_closed() (
  reset_fake_services foreign-marker
  FAKE_RELEASE_STATE=draft
  FAKE_RELEASE_BODY='Unrelated release notes'
  run_command prepare
)

scenario_same_tag_draft_with_both_mismatches_fails_closed() (
  reset_fake_services foreign-both
  FAKE_RELEASE_STATE=draft
  FAKE_RELEASE_NAME='Foreign release title'
  FAKE_RELEASE_BODY='Unrelated release notes'
  run_command prepare
)

scenario_prerelease_mismatch_has_specific_diagnostic() (
  reset_fake_services prerelease-mismatch
  FAKE_RELEASE_STATE=draft
  FAKE_RELEASE_PRERELEASE=true
  run_command prepare
)

scenario_upload_failure_preserves_service_diagnostic() (
  reset_fake_services upload-failure
  FAKE_UPLOAD_FAILURE=true
  run_command prepare
)

scenario_old_github_cli_fails_cleanly() (
  reset_fake_services old-gh
  FAKE_GH_VERSION=2.50.0
  run_command prepare
)

scenario_failed_github_cli_probe_fails_cleanly() (
  reset_fake_services failed-gh-probe
  FAKE_GH_VERSION_FAILURE=true
  run_command prepare
)

scenario_github_cli_warning_does_not_change_version_parse() (
  reset_fake_services gh-version-warning
  FAKE_GH_VERSION_WARNING=true
  run_command prepare
)

scenario_draft_visibility_timeout_fails_closed() (
  reset_fake_services draft-visibility-timeout
  FAKE_DRAFT_VISIBILITY_LAG_AFTER_CREATE=10
  run_command prepare
)

scenario_asset_visibility_timeout_fails_closed() (
  reset_fake_services asset-visibility-timeout
  FAKE_ASSET_VISIBILITY_LAG_AFTER_UPLOAD=10
  run_command prepare
)

scenario_interrupted_publication_resumes
scenario_older_rerun_preserves_newer_surfaces
scenario_prerelease_never_moves_mutable_surfaces
scenario_completed_release_survives_pruned_stage_and_metadata_edits
scenario_legacy_assets_without_server_digests_use_byte_fallback
scenario_completed_release_recovers_from_one_immutable_tag
scenario_missing_asset_clears_stale_state
scenario_opaque_token_config_escaping_is_injection_safe
scenario_github_cli_warning_does_not_change_version_parse

for scenario in \
  scenario_conflicting_version_tag_fails_closed \
  scenario_changed_asset_is_not_overwritten \
  scenario_non_authoritative_api_failure_is_fatal \
  scenario_release_list_404_is_not_absence \
  scenario_non_authoritative_registry_failure_is_fatal \
  scenario_stage_without_release_is_ambiguous \
  scenario_duplicate_release_tag_is_ambiguous \
  scenario_foreign_same_tag_draft_fails_closed \
  scenario_same_tag_draft_without_marker_fails_closed \
  scenario_same_tag_draft_with_both_mismatches_fails_closed \
  scenario_prerelease_mismatch_has_specific_diagnostic \
  scenario_upload_failure_preserves_service_diagnostic \
  scenario_old_github_cli_fails_cleanly \
  scenario_failed_github_cli_probe_fails_cleanly \
  scenario_control_character_token_fails_closed \
  scenario_draft_visibility_timeout_fails_closed \
  scenario_asset_visibility_timeout_fails_closed; do
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

grep -Fq 'does not have the expected workflow title' \
  "$test_root/scenario_foreign_same_tag_draft_fails_closed.out" ||
  fail 'Foreign draft title failure did not identify the title contract'
grep -Fq 'does not contain the expected workflow collision marker' \
  "$test_root/scenario_same_tag_draft_without_marker_fails_closed.out" ||
  fail 'Foreign draft notes failure did not identify the collision-marker contract'
grep -Fq 'fails both workflow collision checks' \
  "$test_root/scenario_same_tag_draft_with_both_mismatches_fails_closed.out" ||
  fail 'Combined foreign draft failure did not identify both collision checks'
grep -Fq "title is 'Miningcore v1.2.3'" \
  "$test_root/scenario_same_tag_draft_with_both_mismatches_fails_closed.out" ||
  fail 'Combined foreign draft failure omitted the expected title'
grep -Fq 'repository/tag/source collision marker is missing or mismatched' \
  "$test_root/scenario_same_tag_draft_with_both_mismatches_fails_closed.out" ||
  fail 'Combined foreign draft failure omitted the marker mismatch'
for scenario_root in foreign-draft foreign-marker foreign-both; do
  if find "$test_root/$scenario_root/assets" -maxdepth 1 -type f -print -quit |
      grep -q .; then
    fail "$scenario_root received trusted release assets before draft validation"
  fi
done
grep -Fq 'prerelease classification does not match its tag' \
  "$test_root/scenario_prerelease_mismatch_has_specific_diagnostic.out" ||
  fail 'Prerelease mismatch did not provide a specific diagnostic'
grep -Fq 'simulated GitHub upload rejection' \
  "$test_root/scenario_upload_failure_preserves_service_diagnostic.out" ||
  fail 'Upload failure discarded GitHub response detail'
grep -Fq 'GitHub CLI 2.51 or newer is required' \
  "$test_root/scenario_old_github_cli_fails_cleanly.out" ||
  fail 'Old GitHub CLI failure did not identify the required version'
grep -Fq 'HUMAN ACTION REQUIRED: could not execute the GitHub CLI version probe' \
  "$test_root/scenario_failed_github_cli_probe_fails_cleanly.out" ||
  fail 'Failed GitHub CLI version probe bypassed the standard diagnostic'
grep -Fq 'GH_TOKEN contains a control character' \
  "$test_root/scenario_control_character_token_fails_closed.out" ||
  fail 'Control-character token failure did not identify the safe header boundary'
if grep -Fq 'Injected: value' \
    "$test_root/scenario_control_character_token_fails_closed.out"; then
  fail 'Control-character token failure exposed secret content'
fi

# Pin both ordering boundaries: public tag promotion follows durable release
# publication, and all tag publications share one non-cancelling FIFO queue.
workflow="$repository_root/.github/workflows/release.yml"
publish_line=$(grep -nF 'name: Publish and verify durable GitHub Release' "$workflow" | cut -d: -f1)
promote_line=$(grep -nF 'name: Promote and verify release container tags' "$workflow" | cut -d: -f1)
if [[ -z "$publish_line" || -z "$promote_line" || "$promote_line" -le "$publish_line" ]]; then
  fail 'Release workflow no longer orders tag promotion after durable publication'
fi
grep -Fq 'group: release-publication-${{ github.repository }}' "$workflow" ||
  fail 'Release publication no longer uses a repository-wide concurrency group'
grep -Fq 'cancel-in-progress: false' "$workflow" ||
  fail 'Release publication may cancel an active publication'
grep -Fq 'queue: max' "$workflow" ||
  fail 'Release publication no longer preserves multiple queued versions'
grep -Fq 'timeout-minutes: 60' "$workflow" ||
  fail 'Release publication no longer bounds repository-wide queue blocking'

echo 'Recoverable release publication state tests passed'
