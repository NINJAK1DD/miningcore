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
  MININGCORE_SOURCE_COMMIT       commit named by the release tag
  MININGCORE_IMAGE               lowercase GHCR image name
  MININGCORE_RELEASE_ASSET_DIR   tested archive/checksum directory
  MININGCORE_RELEASE_NOTES_FILE  release-note preamble (prepare only)
  GH_TOKEN                       release API token with contents write access

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

  for tool in gh jq docker curl cmp find sort realpath sha256sum stat awk; do
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
  : "${GH_TOKEN:?GH_TOKEN is required}"

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

  if [[ ! "$GH_TOKEN" =~ ^[A-Za-z0-9_]+$ ]]; then
    publication_die "GH_TOKEN contains characters unsafe for the private curl configuration"
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

publication_api_list_releases() {
  local destination=$1
  local error_file

  error_file=$(mktemp "$PUBLICATION_WORK_DIR/release-list-error.XXXXXX")
  if ! gh api --paginate --slurp \
      "repos/$GITHUB_REPOSITORY/releases?per_page=100" \
      > "$destination" 2> "$error_file"; then
    cat "$error_file" >&2
    rm -f -- "$error_file" "$destination"
    publication_die \
      "GitHub release-list inspection failed; publication state is not authoritative"
  fi
  rm -f -- "$error_file"

  if ! jq -e \
      'type == "array" and
       all(.[]; type == "array") and
       all(.[][];
         (.tag_name | type == "string") and
         (.draft | type == "boolean") and
         (.prerelease | type == "boolean"))' \
      "$destination" >/dev/null; then
    publication_die \
      "GitHub returned an invalid release list; publication state is not authoritative"
  fi
}

publication_refresh_release() {
  local release_file=${PUBLICATION_WORK_DIR:?}/release.json
  local release_list=$PUBLICATION_WORK_DIR/releases.json
  local matches
  local expected_prerelease=false

  if [[ "$GITHUB_REF_NAME" == *-* ]]; then
    expected_prerelease=true
  fi

  # GitHub's tag endpoint returns published releases only. An authenticated,
  # paginated list is the authoritative source for both drafts and published
  # releases; accepting exactly one match also fails closed on ambiguity.
  publication_api_list_releases "$release_list"
  matches=$(jq --arg tag "$GITHUB_REF_NAME" \
    '[.[][] | select(.tag_name == $tag)] | length' "$release_list")
  if [[ "$matches" -gt 1 ]]; then
    publication_die \
      "GitHub returned $matches releases for tag '$GITHUB_REF_NAME'; state is ambiguous"
  fi
  if [[ "$matches" -eq 0 ]]; then
    rm -f -- "$release_file"
    PUBLICATION_RELEASE_ID=
    PUBLICATION_RELEASE_STATE=absent
    export PUBLICATION_RELEASE_ID PUBLICATION_RELEASE_STATE
    return
  fi

  jq --arg tag "$GITHUB_REF_NAME" \
    '[.[][] | select(.tag_name == $tag)][0]' "$release_list" > "$release_file"
  if ! jq -e --arg tag "$GITHUB_REF_NAME" \
      --argjson prerelease "$expected_prerelease" \
      '.tag_name == $tag and
       .prerelease == $prerelease and
       (.id | type == "number" and . > 0 and floor == .) and
       (.draft | type == "boolean") and
       (.assets | type == "array") and
       all(.assets[];
         (.name | type == "string") and
         (.id | type == "number" and . > 0 and floor == .) and
         ((.state == null) or (.state == "uploaded")) and
         ((.size == null) or
           (.size | type == "number" and . >= 0 and floor == .)) and
         ((.digest == null) or
           (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))))' \
      "$release_file" >/dev/null; then
    publication_die "GitHub returned an invalid release record for '$GITHUB_REF_NAME'"
  fi

  PUBLICATION_RELEASE_ID=$(jq -r '.id' "$release_file")
  if [[ $(jq -r '.draft' "$release_file") == true ]]; then
    PUBLICATION_RELEASE_STATE=draft
  else
    PUBLICATION_RELEASE_STATE=published
  fi

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
  PUBLICATION_ASSET_DIGEST=$(jq -r --arg name "$asset_name" \
    '.assets[] | select(.name == $name) | .digest // empty' "$release_file")
  PUBLICATION_ASSET_SIZE=$(jq -r --arg name "$asset_name" \
    '.assets[] | select(.name == $name) | .size // empty' "$release_file")
  PUBLICATION_ASSET_STATE=$(jq -r --arg name "$asset_name" \
    '.assets[] | select(.name == $name) | .state // empty' "$release_file")
  export PUBLICATION_ASSET_ID PUBLICATION_ASSET_DIGEST
  export PUBLICATION_ASSET_SIZE PUBLICATION_ASSET_STATE
}

publication_download_asset() {
  local asset_id=$1
  local destination=$2
  local error_file
  local status

  error_file=$(mktemp "$PUBLICATION_WORK_DIR/asset-download-error.XXXXXX")
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

publication_upload_asset() {
  local source=$1
  local asset_name=${source##*/}
  local response_file
  local error_file
  local curl_config
  local upload_url

  if [[ ! "$asset_name" =~ ^[A-Za-z0-9._-]+$ ]]; then
    publication_die "release asset name '$asset_name' is unsafe for API upload"
  fi

  response_file=$(mktemp "$PUBLICATION_WORK_DIR/asset-upload-response.XXXXXX")
  error_file=$(mktemp "$PUBLICATION_WORK_DIR/asset-upload-error.XXXXXX")
  curl_config=$(mktemp "$PUBLICATION_WORK_DIR/asset-upload-curl.XXXXXX")
  upload_url="https://uploads.github.com/repos/$GITHUB_REPOSITORY"
  upload_url+="/releases/$PUBLICATION_RELEASE_ID/assets?name=$asset_name"
  chmod 600 "$curl_config"
  printf 'header = "Authorization: Bearer %s"\n' "$GH_TOKEN" > "$curl_config"
  if ! curl --fail-with-body --silent --show-error --location \
      --config "$curl_config" --request POST \
      --header 'Accept: application/vnd.github+json' \
      --header 'Content-Type: application/octet-stream' \
      --data-binary "@$source" --output "$response_file" \
      "$upload_url" 2> "$error_file"; then
    cat "$error_file" >&2
    rm -f -- "$response_file" "$error_file" "$curl_config"
    publication_die \
      "upload of '$asset_name' failed; inspect release id $PUBLICATION_RELEASE_ID before retrying"
  fi
  rm -f -- "$error_file" "$curl_config"

  if ! jq -e --arg name "$asset_name" \
      '.name == $name and
       (.id | type == "number" and . > 0 and floor == .) and
       .state == "uploaded"' \
      "$response_file" >/dev/null; then
    rm -f -- "$response_file"
    publication_die \
      "GitHub did not confirm a complete upload of '$asset_name' for release id" \
      "$PUBLICATION_RELEASE_ID"
  fi
  rm -f -- "$response_file"
}

publication_verify_candidate_asset() {
  local asset_name=$1
  local asset_id=$PUBLICATION_ASSET_ID
  local local_file=$MININGCORE_RELEASE_ASSET_DIR/$asset_name
  local downloaded
  local local_digest
  local local_size

  if [[ -n "$PUBLICATION_ASSET_STATE" && "$PUBLICATION_ASSET_STATE" != uploaded ]]; then
    publication_die "release asset '$asset_name' is not in the uploaded state"
  fi

  if [[ -n "$PUBLICATION_ASSET_DIGEST" ]]; then
    local_digest=sha256:$(sha256sum "$local_file" | awk '{print $1}')
    local_size=$(stat -c '%s' "$local_file")
    if [[ "$PUBLICATION_ASSET_DIGEST" != "$local_digest" ]]; then
      publication_die \
        "release asset '$asset_name' digest differs from this run;" \
        "existing bytes will not be overwritten"
    fi
    if [[ -n "$PUBLICATION_ASSET_SIZE" && "$PUBLICATION_ASSET_SIZE" != "$local_size" ]]; then
      publication_die \
        "release asset '$asset_name' size differs from this run;" \
        "existing bytes will not be overwritten"
    fi
    return
  fi

  # Older release records may not expose GitHub's server-computed digest. Keep
  # the byte-for-byte fallback rather than weakening verification.
  downloaded="$PUBLICATION_WORK_DIR/existing-$asset_name"
  publication_download_asset "$asset_id" "$downloaded"
  if ! cmp --silent "$local_file" "$downloaded"; then
    publication_die \
      "release asset '$asset_name' differs from this run;" \
      "existing bytes will not be overwritten"
  fi
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
  local release_file=${PUBLICATION_WORK_DIR:?}/release.json
  local -a expected
  local asset_name
  local asset_name_json
  local candidate
  local found

  expected=("${PUBLICATION_CANDIDATE_ASSETS[@]}")
  expected+=("$publication_manifest_name")

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

  publication_validate_asset_inventory
  while IFS= read -r asset_name; do
    if publication_asset_id "$asset_name"; then
      publication_verify_candidate_asset "$asset_name"
    elif [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
      publication_upload_asset "$MININGCORE_RELEASE_ASSET_DIR/$asset_name"
      publication_wait_for_release_asset "$asset_name"
      publication_verify_candidate_asset "$asset_name"
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
    --latest=false
    --verify-tag
    --title "Miningcore $GITHUB_REF_NAME"
    --notes-file "$MININGCORE_RELEASE_NOTES_FILE"
    --generate-notes
  )

  if [[ "$GITHUB_REF_NAME" == *-* ]]; then
    release_args+=(--prerelease)
  fi

  if ! gh release create "${release_args[@]}"; then
    publication_die \
      "draft creation failed; inspect GitHub before retrying because the request may have completed"
  fi
  publication_wait_for_draft_release
}

publication_wait_for_draft_release() {
  local delay

  for delay in 0 1 2 4 8; do
    if [[ "$delay" -gt 0 ]]; then
      sleep "$delay"
    fi
    publication_refresh_release
    if [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
      return
    fi
    if [[ "$PUBLICATION_RELEASE_STATE" == published ]]; then
      publication_die \
        "draft creation unexpectedly exposed a published release for '$GITHUB_REF_NAME'"
    fi
  done

  publication_die \
    "GitHub accepted draft creation but the authenticated release list did not expose" \
    "'$GITHUB_REF_NAME' after bounded retries"
}

publication_wait_for_release_asset() {
  local asset_name=$1
  local expected_release_id=$PUBLICATION_RELEASE_ID
  local delay

  for delay in 0 1 2 4 8; do
    if [[ "$delay" -gt 0 ]]; then
      sleep "$delay"
    fi
    publication_refresh_release
    if [[ "$PUBLICATION_RELEASE_STATE" == draft &&
        "$PUBLICATION_RELEASE_ID" == "$expected_release_id" ]] &&
        publication_asset_id "$asset_name"; then
      return
    fi
    if [[ "$PUBLICATION_RELEASE_STATE" != absent &&
        "$PUBLICATION_RELEASE_ID" != "$expected_release_id" ]]; then
      publication_die \
        "release identity changed while waiting for '$asset_name': expected id" \
        "$expected_release_id, found id $PUBLICATION_RELEASE_ID"
    fi
    if [[ "$PUBLICATION_RELEASE_STATE" == published ]]; then
      publication_die \
        "release id $expected_release_id became published before '$asset_name' was verified"
    fi
  done

  publication_die \
    "GitHub accepted upload of '$asset_name' to release id $expected_release_id but the" \
    "authenticated release list did not expose it after bounded retries"
}

publication_inspect_reference() {
  local reference=$1
  local output_file
  local error_file
  local status
  local digest

  output_file=$(mktemp "$PUBLICATION_WORK_DIR/registry-output.XXXXXX")
  error_file=$(mktemp "$PUBLICATION_WORK_DIR/registry-error.XXXXXX")
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

  digest_file=$(mktemp "$PUBLICATION_WORK_DIR/digest.XXXXXX")
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

publication_assert_immutable_tags_match_record() {
  local reference

  for reference in \
      "$MININGCORE_IMAGE:$GITHUB_REF_NAME" \
      "$MININGCORE_IMAGE:$PUBLICATION_VERSION"; do
    if ! publication_optional_digest "$reference"; then
      publication_die \
        "staging image is unavailable and immutable tag '$reference' is absent"
    fi
    if [[ "$PUBLICATION_INSPECTED_DIGEST" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
      publication_die \
        "staging image is unavailable and immutable tag '$reference' differs from" \
        "recorded digest $PUBLICATION_RECORDED_DIGEST"
    fi
  done
}

publication_assert_container_evidence() {
  if publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
    if [[ "$PUBLICATION_INSPECTED_DIGEST" != "$PUBLICATION_RECORDED_DIGEST" ]]; then
      publication_die \
        "staging image digest $PUBLICATION_INSPECTED_DIGEST differs from recorded digest" \
        "$PUBLICATION_RECORDED_DIGEST"
    fi
    return
  fi

  if [[ "$PUBLICATION_RELEASE_STATE" != published ]]; then
    publication_die \
      "recorded staging image '$PUBLICATION_STAGING_REFERENCE' is unavailable;" \
      "do not rebuild or retag this version"
  fi

  # A retention policy may prune the audit-only staging tag after publication.
  # A durable release plus both matching immutable tags is sufficient to prove
  # that a completed publication can be re-run without rebuilding content.
  publication_assert_immutable_tags_match_record
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
      if [[ "$PUBLICATION_RELEASE_STATE" != published ]]; then
        publication_die \
          "release records container digest $recorded_digest but its staging tag is unavailable"
      fi
      publication_assert_immutable_tags_match_record
      stage_digest=$recorded_digest
      needs_build=false
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
  local recorded_present=false

  publication_validate_digest "$requested_digest"
  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    publication_die "container digest cannot be recorded without a matching draft release"
  fi

  if publication_read_recorded_digest; then
    recorded_present=true
    if [[ "$PUBLICATION_RECORDED_DIGEST" != "$requested_digest" ]]; then
      publication_die \
        "$publication_manifest_name records $PUBLICATION_RECORDED_DIGEST," \
        "not requested digest $requested_digest"
    fi
  fi

  if publication_optional_digest "$PUBLICATION_STAGING_REFERENCE"; then
    stage_digest=$PUBLICATION_INSPECTED_DIGEST
    if [[ "$stage_digest" != "$requested_digest" ]]; then
      publication_die \
        "reported build digest $requested_digest differs from staged digest $stage_digest"
    fi
  elif [[ "$PUBLICATION_RELEASE_STATE" == published && "$recorded_present" == true ]]; then
    stage_digest=$requested_digest
    publication_assert_immutable_tags_match_record
  else
    publication_die "staging image '$PUBLICATION_STAGING_REFERENCE' is unavailable"
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
    publication_upload_asset "$manifest"
    publication_wait_for_release_asset "$publication_manifest_name"
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
  publication_assert_container_evidence
}

publication_publish_release_by_id() {
  local make_latest=$1
  local response_file
  local error_file
  local expected_prerelease=false

  if [[ "$GITHUB_REF_NAME" == *-* ]]; then
    expected_prerelease=true
  fi

  response_file=$(mktemp "$PUBLICATION_WORK_DIR/release-update-response.XXXXXX")
  error_file=$(mktemp "$PUBLICATION_WORK_DIR/release-update-error.XXXXXX")
  if ! gh api --method PATCH \
      -F draft=false -f make_latest="$make_latest" \
      "repos/$GITHUB_REPOSITORY/releases/$PUBLICATION_RELEASE_ID" \
      > "$response_file" 2> "$error_file"; then
    cat "$error_file" >&2
    rm -f -- "$response_file" "$error_file"
    publication_die \
      "release publication request failed for id $PUBLICATION_RELEASE_ID;" \
      "inspect whether the draft became durable before retrying"
  fi
  rm -f -- "$error_file"

  if ! jq -e --arg tag "$GITHUB_REF_NAME" \
      --argjson id "$PUBLICATION_RELEASE_ID" \
      --argjson prerelease "$expected_prerelease" \
      '.id == $id and
       .tag_name == $tag and .draft == false and .prerelease == $prerelease' \
      "$response_file" >/dev/null; then
    rm -f -- "$response_file"
    publication_die \
      "GitHub returned an invalid publication response for release id $PUBLICATION_RELEASE_ID"
  fi
  rm -f -- "$response_file"
}

publication_wait_for_published_release() {
  local delay
  local expected_release_id=$PUBLICATION_RELEASE_ID

  for delay in 0 1 2 4 8; do
    if [[ "$delay" -gt 0 ]]; then
      sleep "$delay"
    fi
    publication_refresh_release
    if [[ "$PUBLICATION_RELEASE_STATE" == published &&
        "$PUBLICATION_RELEASE_ID" == "$expected_release_id" ]]; then
      return
    fi
  done

  publication_die \
    "GitHub accepted publication of release id $expected_release_id but the authenticated" \
    "release list did not confirm its durable state after bounded retries"
}

publication_publish() {
  local make_latest=false

  publication_refresh_release
  if [[ "$PUBLICATION_RELEASE_STATE" == absent ]]; then
    publication_die "release '$GITHUB_REF_NAME' is absent"
  fi

  publication_verify_complete_release
  if [[ "$PUBLICATION_RELEASE_STATE" == draft ]]; then
    if [[ "$GITHUB_REF_NAME" != *-* ]]; then
      publication_resolve_mutable_alias_freshness
      make_latest=$PUBLICATION_MAY_PROMOTE_LATEST
    fi
    publication_publish_release_by_id "$make_latest"
    publication_wait_for_published_release
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

  # Buildx 0.14+ provides --prefer-index. The workflow's setup-buildx action
  # supplies a current version. Avoid wrapping a single-platform manifest in a
  # new index: every promoted reference must retain the recorded digest.
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

publication_stable_version_is_greater() {
  local left=$1
  local right=$2
  local -a left_parts
  local -a right_parts
  local index

  IFS=. read -r -a left_parts <<< "$left"
  IFS=. read -r -a right_parts <<< "$right"
  for index in 0 1 2; do
    if [[ ${#left_parts[$index]} -gt ${#right_parts[$index]} ]]; then
      return 0
    fi
    if [[ ${#left_parts[$index]} -lt ${#right_parts[$index]} ]]; then
      return 1
    fi
    if [[ ${left_parts[$index]} > ${right_parts[$index]} ]]; then
      return 0
    fi
    if [[ ${left_parts[$index]} < ${right_parts[$index]} ]]; then
      return 1
    fi
  done

  return 1
}

publication_resolve_mutable_alias_freshness() {
  local release_list=$PUBLICATION_WORK_DIR/releases.json
  local stable_tags=$PUBLICATION_WORK_DIR/stable-tags
  local current_line=${PUBLICATION_VERSION%.*}
  local current_occurrences
  local tag
  local version

  publication_api_list_releases "$release_list"
  if ! jq -r \
      '.[][] |
       select(.draft == false and .prerelease == false) |
       .tag_name |
       select(test("^v(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"))' \
      "$release_list" > "$stable_tags"; then
    publication_die "published stable-release tags could not be read authoritatively"
  fi

  current_occurrences=$(jq --arg tag "$GITHUB_REF_NAME" \
    '[.[][] | select(.tag_name == $tag)] | length' "$release_list")
  if [[ "$current_occurrences" -ne 1 ]]; then
    publication_die \
      "release '$GITHUB_REF_NAME' occurred $current_occurrences times" \
      "in the authoritative release list"
  fi

  PUBLICATION_MAY_PROMOTE_LINE_ALIAS=true
  PUBLICATION_MAY_PROMOTE_LATEST=true

  while IFS= read -r tag; do
    [[ -n "$tag" ]] || continue
    version=${tag#v}
    if publication_stable_version_is_greater "$version" "$PUBLICATION_VERSION"; then
      PUBLICATION_MAY_PROMOTE_LATEST=false
      if [[ ${version%.*} == "$current_line" ]]; then
        PUBLICATION_MAY_PROMOTE_LINE_ALIAS=false
      fi
    fi
  done < "$stable_tags"

  export PUBLICATION_MAY_PROMOTE_LINE_ALIAS PUBLICATION_MAY_PROMOTE_LATEST
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
    publication_resolve_mutable_alias_freshness
    if [[ "$PUBLICATION_MAY_PROMOTE_LINE_ALIAS" == true ]]; then
      publication_promote_mutable_tag "$MININGCORE_IMAGE:$major_minor"
    else
      printf 'Preserving newer %s alias; %s is not the newest stable release in that line\n' \
        "$major_minor" "$GITHUB_REF_NAME"
    fi
    if [[ "$PUBLICATION_MAY_PROMOTE_LATEST" == true ]]; then
      publication_promote_mutable_tag "$MININGCORE_IMAGE:latest"
    else
      printf 'Preserving newer latest alias; %s is not the newest stable release\n' \
        "$GITHUB_REF_NAME"
    fi
  fi

  printf 'Verified immutable tags and processed eligible aliases for %s@%s\n' \
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
