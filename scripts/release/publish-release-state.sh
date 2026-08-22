#!/usr/bin/env bash

set -euo pipefail

publication_script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
readonly publication_script_dir
publication_repository_root=$(cd "$publication_script_dir/../.." && pwd)
readonly publication_repository_root
readonly publication_manifest_name=CONTAINER-IMAGE.json

publication_usage() {
  cat >&2 <<'EOF'
Usage: publish-release-state.sh <prepare|record|publish|promote>

Required environment:
  GITHUB_REPOSITORY              owner/repository
  GITHUB_REF_NAME                immutable v-prefixed release tag
  MININGCORE_SOURCE_COMMIT      commit named by the release tag
  MININGCORE_IMAGE               lowercase GHCR image name
  MININGCORE_RELEASE_ASSET_DIR   tested archive/checksum directory
  MININGCORE_RELEASE_NOTES_FILE  release-note preamble (prepare only)

GITHUB_OUTPUT is optional. When present, prepare and record append outputs for
later GitHub Actions steps.
EOF
  exit 64
}

publication_die() {
  printf 'HUMAN ACTION REQUIRED: %s\n' "$*" >&2
  exit 70
}

publication_require_tools() {
  local tool

  for tool in gh jq docker cmp find sort realpath; do
    if ! command -v "$tool" >/dev/null 2>&1; then
      publication_die "required publication tool '$tool' is unavailable"
    fi
  done
}

publication_validate_environment() {
  : "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
  : "${GITHUB_REF_NAME:?GITHUB_REF_NAME is required}"
  : "${MININGCORE_SOURCE_COMMIT:?MININGCORE_SOURCE_COMMIT is required}"
  : "${MININGCORE_IMAGE:?MININGCORE_IMAGE is required}"
  : "${MININGCORE_RELEASE_ASSET_DIR:?MININGCORE_RELEASE_ASSET_DIR is required}"

  if [[ ! "$GITHUB_REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
    publication_die "invalid GitHub repository identity '$GITHUB_REPOSITORY'"
  fi

  # Build metadata (+suffix) is intentionally excluded because '+' is not a
  # legal OCI tag character. This also keeps every promoted tag reversible.
  if [[ ! "$GITHUB_REF_NAME" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
    publication_die "release tag '$GITHUB_REF_NAME' is not a supported v-prefixed SemVer tag"
  fi

  if [[ ! "$MININGCORE_SOURCE_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]]; then
    publication_die "MININGCORE_SOURCE_COMMIT is not a full commit identifier"
  fi

  if [[ ! "$MININGCORE_IMAGE" =~ ^ghcr\.io/[a-z0-9_.-]+/[a-z0-9_.-]+$ ]]; then
    publication_die "GHCR image '$MININGCORE_IMAGE' is not a lowercase repository image"
  fi

  if [[ ! -d "$MININGCORE_RELEASE_ASSET_DIR" ]]; then
    publication_die "release asset directory is unavailable: $MININGCORE_RELEASE_ASSET_DIR"
  fi

  MININGCORE_RELEASE_ASSET_DIR=$(realpath "$MININGCORE_RELEASE_ASSET_DIR")
  export MININGCORE_RELEASE_ASSET_DIR

  : "${MININGCORE_RELEASE_NOTES_FILE:?MININGCORE_RELEASE_NOTES_FILE is required}"
  if [[ ! -f "$MININGCORE_RELEASE_NOTES_FILE" ]]; then
    publication_die "release notes file is unavailable: $MININGCORE_RELEASE_NOTES_FILE"
  fi
  MININGCORE_RELEASE_NOTES_FILE=$(realpath "$MININGCORE_RELEASE_NOTES_FILE")
  export MININGCORE_RELEASE_NOTES_FILE

  PUBLICATION_STAGING_TAG="publication-staging-$GITHUB_REF_NAME"
  PUBLICATION_STAGING_REFERENCE="$MININGCORE_IMAGE:$PUBLICATION_STAGING_TAG"
  PUBLICATION_VERSION=${GITHUB_REF_NAME#v}
  export PUBLICATION_STAGING_TAG PUBLICATION_STAGING_REFERENCE PUBLICATION_VERSION
}

publication_append_output() {
  local name=$1
  local value=$2

  if [[ -n ${GITHUB_OUTPUT:-} ]]; then
    printf '%s=%s\n' "$name" "$value" >> "$GITHUB_OUTPUT"
  fi
}

publication_api_get() {
  local endpoint=$1
  local destination=$2
  local error_file
  local status

  error_file=$(mktemp)
  if gh api "$endpoint" > "$destination" 2> "$error_file"; then
    status=0
  else
    status=$?
  fi

  if [[ "$status" -eq 0 ]]; then
    rm -f -- "$error_file"
    return 0
  fi

  if grep -Fq 'HTTP 404' "$error_file"; then
    rm -f -- "$error_file" "$destination"
    return 44
  fi

  cat "$error_file" >&2
  rm -f -- "$error_file" "$destination"
  publication_die "GitHub API inspection failed for '$endpoint'; state is not authoritative"
}

publication_refresh_release() {
  local release_file=${PUBLICATION_WORK_DIR:?}/release.json
  local expected_prerelease=false
  local status

  if [[ "$GITHUB_REF_NAME" == *-* ]]; then
    expected_prerelease=true
  fi

  if publication_api_get \
      "repos/$GITHUB_REPOSITORY/releases/tags/$GITHUB_REF_NAME" "$release_file"; then
    status=0
  else
    status=$?
  fi

  case "$status" in
    0)
      if ! jq -e --arg tag "$GITHUB_REF_NAME" \
        --arg name "Miningcore $GITHUB_REF_NAME" \
        --argjson prerelease "$expected_prerelease" \
        --rawfile notes "$MININGCORE_RELEASE_NOTES_FILE" \
        '.tag_name == $tag and
         .name == $name and
         .prerelease == $prerelease and
         (.body | type == "string") and
         (.body | startswith($notes)) and
         (.id | type == "number") and
         (.draft | type == "boolean") and
         (.assets | type == "array") and
         all(.assets[];
           (.name | type == "string") and
           (.id | type == "number"))' \
        "$release_file" >/dev/null; then
        publication_die "GitHub returned an invalid release record for '$GITHUB_REF_NAME'"
      fi

      PUBLICATION_RELEASE_ID=$(jq -r '.id' "$release_file")
      if [[ $(jq -r '.draft' "$release_file") == true ]]; then
        PUBLICATION_RELEASE_STATE=draft
      else
        PUBLICATION_RELEASE_STATE=published
      fi
      ;;
    44)
      PUBLICATION_RELEASE_ID=
      PUBLICATION_RELEASE_STATE=absent
      ;;
    *)
      return "$status"
      ;;
  esac

  export PUBLICATION_RELEASE_ID PUBLICATION_RELEASE_STATE
}

publication_asset_id() {
  local asset_name=$1
  local release_file=${PUBLICATION_WORK_DIR:?}/release.json
  local count

  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    return 1
  fi

  count=$(jq --arg name "$asset_name" \
    '[.assets[] | select(.name == $name)] | length' "$release_file")
  if [[ "$count" -gt 1 ]]; then
    publication_die "release '$GITHUB_REF_NAME' contains duplicate asset '$asset_name'"
  fi

  if [[ "$count" -eq 0 ]]; then
    PUBLICATION_ASSET_ID=
    return 1
  fi

  PUBLICATION_ASSET_ID=$(jq -r --arg name "$asset_name" \
    '.assets[] | select(.name == $name) | .id' "$release_file")
  export PUBLICATION_ASSET_ID
}

publication_download_asset() {
  local asset_id=$1
  local destination=$2
  local error_file
  local status

  error_file=$(mktemp)
  if gh api -H 'Accept: application/octet-stream' \
      "repos/$GITHUB_REPOSITORY/releases/assets/$asset_id" \
      > "$destination" 2> "$error_file"; then
    status=0
  else
    status=$?
  fi

  if [[ "$status" -ne 0 ]]; then
    cat "$error_file" >&2
    rm -f -- "$error_file" "$destination"
    publication_die "could not download release asset id $asset_id for byte verification"
  fi

  rm -f -- "$error_file"
}

publication_load_candidate_assets() {
  local -a archives
  local -a discovered_archives
  local expected_archive
  local target

  source "$publication_repository_root/scripts/release/linux-release-targets.sh"
  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    expected_archive="miningcore-${GITHUB_REF_NAME}-linux-x64-ubuntu-${target}.tar.gz"
    if [[ ! -f "$MININGCORE_RELEASE_ASSET_DIR/$expected_archive" ]]; then
      publication_die "tested asset directory is missing '$expected_archive'"
    fi
    archives+=("$expected_archive")
  done

  mapfile -t discovered_archives < <(
    find "$MININGCORE_RELEASE_ASSET_DIR" -maxdepth 1 -type f \
      -name 'miningcore-*-linux-x64-ubuntu-*.tar.gz' -printf '%f\n' | sort
  )

  if [[ ${#discovered_archives[@]} -ne ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} ]]; then
    publication_die \
      "tested asset directory contains ${#discovered_archives[@]} Ubuntu archive(s);" \
      "expected ${#MININGCORE_LINUX_RELEASE_TARGETS[@]}"
  fi

  if [[ ! -f "$MININGCORE_RELEASE_ASSET_DIR/SHA256SUMS" ]]; then
    publication_die "tested asset directory does not contain SHA256SUMS"
  fi

  PUBLICATION_CANDIDATE_ASSETS=("${archives[@]}" SHA256SUMS)
}

publication_validate_asset_inventory() {
  local allow_manifest=$1
  local release_file=${PUBLICATION_WORK_DIR:?}/release.json
  local -a expected
  local asset_name
  local asset_name_json
  local candidate
  local found

  expected=("${PUBLICATION_CANDIDATE_ASSETS[@]}")
  if [[ "$allow_manifest" == true ]]; then
    expected+=("$publication_manifest_name")
  fi

  while IFS= read -r asset_name_json; do
    if ! asset_name=$(jq -er 'select(type == "string")' <<< "$asset_name_json"); then
      publication_die "release '$GITHUB_REF_NAME' contains an invalid asset name"
    fi
    found=false
    for candidate in "${expected[@]}"; do
      if [[ "$asset_name" == "$candidate" ]]; then
        found=true
        break
      fi
    done

    if [[ "$found" != true ]]; then
      publication_die \
        "release '$GITHUB_REF_NAME' contains unexpected asset '$asset_name';" \
        "do not replace or delete it automatically"
    fi
  done < <(jq -c '.assets[].name' "$release_file")
}

publication_sync_candidate_assets() {
  local asset_name
  local asset_id
  local downloaded

  publication_validate_asset_inventory true
  while IFS= read -r asset_name; do
    if publication_asset_id "$asset_name"; then
      asset_id=$PUBLICATION_ASSET_ID
      downloaded="$PUBLICATION_WORK_DIR/existing-$asset_name"
      publication_download_asset "$asset_id" "$downloaded"
      if ! cmp --silent "$MININGCORE_RELEASE_ASSET_DIR/$asset_name" "$downloaded"; then
        publication_die \
          "release asset '$asset_name' differs from this run;" \
          "existing bytes will not be overwritten"
      fi
    elif [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
      if ! gh release upload "$GITHUB_REF_NAME" \
          "$MININGCORE_RELEASE_ASSET_DIR/$asset_name"; then
        publication_die \
          "upload of '$asset_name' failed; inspect the draft before retrying"
      fi
      publication_refresh_release
      if ! publication_asset_id "$asset_name"; then
        publication_die "GitHub did not report uploaded release asset '$asset_name'"
      fi
      asset_id=$PUBLICATION_ASSET_ID
      downloaded="$PUBLICATION_WORK_DIR/uploaded-$asset_name"
      publication_download_asset "$asset_id" "$downloaded"
      if ! cmp --silent "$MININGCORE_RELEASE_ASSET_DIR/$asset_name" "$downloaded"; then
        publication_die "uploaded release asset '$asset_name' failed byte verification"
      fi
    else
      publication_die \
        "published release '$GITHUB_REF_NAME' is missing '$asset_name'" \
        "and cannot be repaired automatically"
    fi
  done < <(printf '%s\n' "${PUBLICATION_CANDIDATE_ASSETS[@]}")
}

publication_create_draft() {
  local -a release_args

  release_args=(
    "$GITHUB_REF_NAME"
    --draft
    --verify-tag
    --title "Miningcore $GITHUB_REF_NAME"
    --notes-file "$MININGCORE_RELEASE_NOTES_FILE"
    --generate-notes
  )

  if [[ "$GITHUB_REF_NAME" == *-* ]]; then
    release_args+=(--prerelease --latest=false)
  else
    release_args+=(--latest=true)
  fi

  if ! gh release create "${release_args[@]}"; then
    publication_die \
      "draft creation failed; inspect GitHub before retrying because the request may have completed"
  fi
  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" != draft ]]; then
    publication_die "GitHub did not create the expected draft release '$GITHUB_REF_NAME'"
  fi
}

publication_inspect_reference() {
  local reference=$1
  local output_file
  local error_file
  local status
  local digest

  output_file=$(mktemp)
  error_file=$(mktemp)
  if docker buildx imagetools inspect \
      --format '{{json .Manifest.Digest}}' "$reference" \
      > "$output_file" 2> "$error_file"; then
    status=0
  else
    status=$?
  fi

  if [[ "$status" -ne 0 ]]; then
    if grep -Eiq 'manifest unknown|name unknown' "$error_file" ||
        { grep -Fqi "$reference" "$error_file" &&
          grep -Eiq '(^|[^[:alpha:]])(404|not found)([^[:alpha:]]|$)' \
            "$error_file"; }; then
      rm -f -- "$output_file" "$error_file"
      return 44
    fi

    cat "$error_file" >&2
    rm -f -- "$output_file" "$error_file"
    publication_die \
      "registry inspection failed for '$reference'; absence was not authoritative"
  fi

  if ! digest=$(jq -er \
      'select(type == "string" and test("^sha256:[0-9a-f]{64}$"))' \
      "$output_file"); then
    rm -f -- "$output_file" "$error_file"
    publication_die "registry returned an invalid manifest digest for '$reference'"
  fi

  rm -f -- "$output_file" "$error_file"
  printf '%s\n' "$digest"
}

publication_optional_digest() {
  local reference=$1
  local digest_file
  local status

  digest_file=$(mktemp)
  if publication_inspect_reference "$reference" > "$digest_file"; then
    status=0
  else
    status=$?
  fi

  case "$status" in
    0)
      PUBLICATION_INSPECTED_DIGEST=$(<"$digest_file")
      rm -f -- "$digest_file"
      export PUBLICATION_INSPECTED_DIGEST
      return 0
      ;;
    44)
      rm -f -- "$digest_file"
      PUBLICATION_INSPECTED_DIGEST=
      return 1
      ;;
    *)
      rm -f -- "$digest_file"
      return "$status"
      ;;
  esac
}

publication_validate_digest() {
  local digest=$1
  if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    publication_die "invalid container digest '$digest'"
  fi
}

publication_write_manifest() {
  local digest=$1
  local destination=$2

  publication_validate_digest "$digest"
  jq -n \
    --arg image "$MININGCORE_IMAGE" \
    --arg stagingTag "$PUBLICATION_STAGING_TAG" \
    --arg digest "$digest" \
    --arg sourceCommit "${MININGCORE_SOURCE_COMMIT,,}" \
    --arg releaseTag "$GITHUB_REF_NAME" \
    '{
      schemaVersion: 1,
      image: $image,
      stagingTag: $stagingTag,
      digest: $digest,
      sourceCommit: $sourceCommit,
      releaseTag: $releaseTag
    }' > "$destination"
}

publication_read_recorded_digest() {
  local asset_id
  local manifest=$PUBLICATION_WORK_DIR/existing-$publication_manifest_name

  if ! publication_asset_id "$publication_manifest_name"; then
    return 1
  fi
  asset_id=$PUBLICATION_ASSET_ID

  publication_download_asset "$asset_id" "$manifest"
  if ! jq -e \
    --arg image "$MININGCORE_IMAGE" \
    --arg stagingTag "$PUBLICATION_STAGING_TAG" \
    --arg sourceCommit "${MININGCORE_SOURCE_COMMIT,,}" \
    --arg releaseTag "$GITHUB_REF_NAME" \
    '.schemaVersion == 1 and
     .image == $image and
     .stagingTag == $stagingTag and
     .sourceCommit == $sourceCommit and
     .releaseTag == $releaseTag and
     (.digest | type == "string")' "$manifest" >/dev/null; then
    publication_die \
      "release '$GITHUB_REF_NAME' contains an invalid or foreign $publication_manifest_name"
  fi

  PUBLICATION_RECORDED_DIGEST=$(jq -r '.digest' "$manifest")
  publication_validate_digest "$PUBLICATION_RECORDED_DIGEST"
  export PUBLICATION_RECORDED_DIGEST
}

publication_assert_stage_matches_record() {
  local stage_digest

  if ! publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
    publication_die \
      "recorded staging image '$PUBLICATION_STAGING_REFERENCE' is unavailable;" \
      "do not rebuild or retag this version"
  fi
  stage_digest=$PUBLICATION_INSPECTED_DIGEST

  if [[ "$stage_digest" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
    publication_die \
      "staging image digest $stage_digest differs from recorded digest $PUBLICATION_RECORDED_DIGEST"
  fi
}

publication_prepare() {
  local stage_digest=
  local recorded_digest=
  local needs_build=true

  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    if publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
      publication_die \
        "staging image '$PUBLICATION_STAGING_REFERENCE' exists without a matching release record"
    fi
    publication_create_draft
  fi

  publication_sync_candidate_assets

  if publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
    stage_digest=$PUBLICATION_INSPECTED_DIGEST
    needs_build=false
  fi

  if publication_read_recorded_digest; then
    recorded_digest=$PUBLICATION_RECORDED_DIGEST
    if [[ -z "$stage_digest" ]]; then
      publication_die \
        "release records container digest $recorded_digest but its staging tag is unavailable"
    fi
    if [[ "$stage_digest" != "$recorded_digest" ]]; then
      publication_die \
        "staging digest $stage_digest differs from release record $recorded_digest"
    fi
  elif [[ "$PUBLICATION_RELEASE_STATE" == published ]]; then
    publication_die \
      "published release '$GITHUB_REF_NAME' lacks $publication_manifest_name" \
      "and cannot be repaired automatically"
  fi

  publication_append_output release_state "$PUBLICATION_RELEASE_STATE"
  publication_append_output needs_container_build "$needs_build"
  publication_append_output staging_digest "$stage_digest"
  publication_append_output staging_tag "$PUBLICATION_STAGING_TAG"
  printf 'Publication state: release=%s, staged-container=%s\n' \
    "$PUBLICATION_RELEASE_STATE" "${stage_digest:-absent}"
}

publication_record() {
  local requested_digest=${MININGCORE_CONTAINER_DIGEST:-}
  local stage_digest
  local manifest=$PUBLICATION_WORK_DIR/$publication_manifest_name
  local existing_manifest=$PUBLICATION_WORK_DIR/existing-$publication_manifest_name
  local asset_id

  publication_validate_digest "$requested_digest"
  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    publication_die "container digest cannot be recorded without a matching draft release"
  fi

  if ! publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
    publication_die "staging image '$PUBLICATION_STAGING_REFERENCE' is unavailable"
  fi
  stage_digest=$PUBLICATION_INSPECTED_DIGEST
  if [[ "$stage_digest" != "$requested_digest" ]]; then
    publication_die \
      "reported build digest $requested_digest differs from staged digest $stage_digest"
  fi

  publication_write_manifest "$stage_digest" "$manifest"
  if publication_asset_id "$publication_manifest_name"; then
    asset_id=$PUBLICATION_ASSET_ID
    publication_download_asset "$asset_id" "$existing_manifest"
    if ! cmp --silent "$manifest" "$existing_manifest"; then
      publication_die \
        "$publication_manifest_name records different immutable container content"
    fi
  elif [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
    if ! gh release upload "$GITHUB_REF_NAME" "$manifest"; then
      publication_die \
        "upload of $publication_manifest_name failed; inspect the draft before retrying"
    fi
    publication_refresh_release
    if ! publication_asset_id "$publication_manifest_name"; then
      publication_die "GitHub did not report uploaded $publication_manifest_name"
    fi
    asset_id=$PUBLICATION_ASSET_ID
    publication_download_asset "$asset_id" "$existing_manifest"
    if ! cmp --silent "$manifest" "$existing_manifest"; then
      publication_die "uploaded $publication_manifest_name failed byte verification"
    fi
  else
    publication_die \
      "published release '$GITHUB_REF_NAME' lacks $publication_manifest_name"
  fi

  publication_append_output digest "$stage_digest"
  printf 'Recorded staged container %s@%s\n' "$MININGCORE_IMAGE" "$stage_digest"
}

publication_verify_complete_release() {
  publication_sync_candidate_assets
  if ! publication_read_recorded_digest; then
    publication_die "release '$GITHUB_REF_NAME' lacks $publication_manifest_name"
  fi
  publication_assert_stage_matches_record
}

publication_publish() {
  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    publication_die "release '$GITHUB_REF_NAME' is absent"
  fi

  publication_verify_complete_release
  if [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
    if ! gh release edit "$GITHUB_REF_NAME" --draft=false; then
      publication_die \
        "release publication request failed; inspect whether the draft became durable" \
        "before retrying"
    fi
    publication_refresh_release
    if [[ "$PUBLICATION_RELEASE_STATE" != published ]]; then
      publication_die "GitHub did not confirm publication of '$GITHUB_REF_NAME'"
    fi
    publication_verify_complete_release
  fi

  printf 'Durable GitHub Release verified: %s\n' "$GITHUB_REF_NAME"
}

publication_promote_immutable_tag() {
  local target=$1
  local existing_digest

  if publication_optional_digest "$target"; then
    existing_digest=$PUBLICATION_INSPECTED_DIGEST
    if [[ "$existing_digest" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
      publication_die \
        "immutable version tag '$target' points to $existing_digest," \
        "expected $PUBLICATION_RECORDED_DIGEST"
    fi
    printf 'Immutable version tag already verified: %s\n' "$target"
    return
  fi

  # Avoid Buildx wrapping a single-platform manifest in a new index: every
  # promoted reference must retain the digest recorded in the release asset.
  if ! docker buildx imagetools create --prefer-index=false \
      --tag "$target" "$MININGCORE_IMAGE@$PUBLICATION_RECORDED_DIGEST"; then
    publication_die "immutable version tag promotion failed for '$target'"
  fi
  if ! publication_optional_digest "$target" ||
      [[ "$PUBLICATION_INSPECTED_DIGEST" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
    publication_die "immutable version tag '$target' failed post-promotion verification"
  fi
}

publication_promote_mutable_tag() {
  local target=$1

  # This function is reachable only after publication_verify_complete_release
  # has confirmed a non-draft release. Mutable aliases are never touched while
  # the release is absent or staged.
  if ! docker buildx imagetools create --prefer-index=false \
      --tag "$target" "$MININGCORE_IMAGE@$PUBLICATION_RECORDED_DIGEST"; then
    publication_die "mutable alias promotion failed for '$target'"
  fi
  if ! publication_optional_digest "$target" ||
      [[ "$PUBLICATION_INSPECTED_DIGEST" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
    publication_die "mutable alias '$target' failed post-promotion verification"
  fi
}

publication_promote() {
  local major_minor

  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" != published ]]; then
    publication_die \
      "container tags cannot be promoted before the matching GitHub Release is durable"
  fi
  publication_verify_complete_release

  publication_promote_immutable_tag "$MININGCORE_IMAGE:$GITHUB_REF_NAME"
  publication_promote_immutable_tag "$MININGCORE_IMAGE:$PUBLICATION_VERSION"

  if [[ "$GITHUB_REF_NAME" != *-* ]]; then
    major_minor=${PUBLICATION_VERSION%.*}
    publication_promote_mutable_tag "$MININGCORE_IMAGE:$major_minor"
    publication_promote_mutable_tag "$MININGCORE_IMAGE:latest"
  fi

  printf 'Promoted and verified container tags for %s@%s\n' \
    "$MININGCORE_IMAGE" "$PUBLICATION_RECORDED_DIGEST"
}

publication_main() {
  local command=${1:-}

  case "$command" in
    prepare|record|publish|promote) ;;
    *) publication_usage ;;
  esac

  publication_require_tools
  publication_validate_environment
  publication_load_candidate_assets
  PUBLICATION_WORK_DIR=$(mktemp -d)
  trap 'rm -rf -- "$PUBLICATION_WORK_DIR"' EXIT
  export PUBLICATION_WORK_DIR

  "publication_$command"
  rm -rf -- "$PUBLICATION_WORK_DIR"
  trap - EXIT
}

if [[ ${BASH_SOURCE[0]} == "$0" ]]; then
  publication_main "$@"
fi
