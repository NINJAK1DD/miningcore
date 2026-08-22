#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
package_script="$repository_root/scripts/release/package-linux-x64.sh"
collector="$repository_root/scripts/release/collect-linux-release-assets.sh"
work_dir=$(mktemp -d)
publish_dir="$work_dir/publish"
output_dir="$work_dir/dist"
complete_dir="$work_dir/complete"
version=v1.2.3-rc.4
source "$repository_root/scripts/release/linux-release-targets.sh"

resolute_digest=sha256:2260313b31c8c011cd2eebe728008efac1b3982be73eb71348ea2648d2c0e09b
jammy_digest=sha256:2edbbc5dc405e9612ba3584ce95480277e3eb374407b5505fe26f17df77c7dbc
resolute_image="ubuntu:26.04@$resolute_digest"
jammy_image="ubuntu:22.04@$jammy_digest"
expected_matrix='{"include":['
expected_matrix+='{"ubuntu":"26.04","role":"primary","runner":"ubuntu-24.04",'
expected_matrix+="\"image\":\"$resolute_image\"},"
expected_matrix+='{"ubuntu":"22.04","role":"compatibility","runner":"ubuntu-24.04",'
expected_matrix+="\"image\":\"$jammy_image\"}]}"
actual_matrix=$(miningcore_linux_release_matrix_json)

if [[ "$actual_matrix" != "$expected_matrix" ]]; then
  echo "Linux release target matrix is unexpected: $actual_matrix" >&2
  exit 1
fi

# Sourcing the shared contract through two helper paths must remain harmless.
source "$repository_root/scripts/release/linux-release-targets.sh"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

new_fixture() {
  local name=$1
  local fixture="$work_dir/$name"

  rm -rf -- "$fixture"
  mkdir -p "$fixture"
  printf '%s\n' "$fixture"
}

expect_collection_failure() {
  local label=$1
  local fixture=$2
  local expected_message=$3
  local status
  local output

  set +e
  output=$(bash "$collector" "$fixture" "$version" "$SOURCE_COMMIT" 2>&1)
  status=$?
  set -e

  if [[ "$status" -eq 0 ]]; then
    echo "Release collection accepted $label" >&2
    exit 1
  fi

  if ! grep -Fq "$expected_message" <<<"$output"; then
    echo "Release collection did not diagnose $label" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

repack_fixture() {
  local archive=$1
  local operation=$2
  local replacement=${3:-}
  local unpack="$work_dir/unpack"
  local package_root
  local build_info

  rm -rf -- "$unpack"
  mkdir -p "$unpack"
  tar -xzf "$archive" -C "$unpack"
  package_root=$(find "$unpack" -mindepth 1 -maxdepth 1 -type d -printf '%f\n')
  build_info="$unpack/$package_root/BUILD-INFO"

  case "$operation" in
    remove-build-info)
      rm -- "$build_info"
      ;;
    replace-target)
      sed -i "s/^Target: .*/Target: $replacement/" "$build_info"
      ;;
    replace-commit)
      sed -i "s/^Source commit: .*/Source commit: $replacement/" "$build_info"
      ;;
    replace-build-image)
      sed -i "s|^Build image: .*|Build image: $replacement|" "$build_info"
      ;;
    *)
      echo "Unsupported fixture operation: $operation" >&2
      exit 1
      ;;
  esac

  tar -czf "$archive" -C "$unpack" "$package_root"
}

mkdir -p "$publish_dir" "$output_dir" "$complete_dir"
printf '#!/usr/bin/env bash\nexit 0\n' > "$publish_dir/Miningcore"
chmod 0755 "$publish_dir/Miningcore"

while IFS= read -r library; do
  [[ -n "$library" ]] || continue
  : > "$publish_dir/$library"
done < "$repository_root/scripts/release/linux-native-libraries.txt"

if ! compgen -G "$publish_dir/*.so" >/dev/null; then
  echo "Unable to create the native-library package fixture" >&2
  exit 1
fi

SOURCE_COMMIT=$(git -C "$repository_root" rev-parse HEAD)
SOURCE_DATE_EPOCH=$(git -C "$repository_root" show -s --format=%ct "$SOURCE_COMMIT")
export SOURCE_COMMIT SOURCE_DATE_EPOCH

for ubuntu_version in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  rm -rf -- "$output_dir"
  mkdir -p "$output_dir"
  build_image=$(miningcore_linux_release_target_image "$ubuntu_version")

  bash "$package_script" "$version" "$ubuntu_version" "$publish_dir" \
    "$output_dir" "$build_image"

  archive="miningcore-${version}-linux-x64-ubuntu-${ubuntu_version}.tar.gz"
  package_root="miningcore-${version}-linux-x64-ubuntu-${ubuntu_version}"

  test -f "$output_dir/$archive"
  (cd "$output_dir" && sha256sum --check SHA256SUMS)

  target=$(tar -xOf "$output_dir/$archive" "$package_root/BUILD-INFO" |
    sed -n 's/^Target: //p')
  if [[ "$target" != "Ubuntu $ubuntu_version x64" ]]; then
    echo "$archive records an unexpected target: $target" >&2
    exit 1
  fi

  recorded_build_image=$(tar -xOf "$output_dir/$archive" "$package_root/BUILD-INFO" |
    sed -n 's/^Build image: //p')
  if [[ "$recorded_build_image" != "$build_image" ]]; then
    echo "$archive records an unexpected build image: $recorded_build_image" >&2
    exit 1
  fi

  cp "$output_dir/$archive" "$complete_dir/"
done

bash "$collector" "$complete_dir" "$version" "$SOURCE_COMMIT"
(cd "$complete_dir" && sha256sum --check --strict SHA256SUMS)

fixture=$(new_fixture identical-existing-release)
cp "$complete_dir"/* "$fixture/"
bash "$repository_root/scripts/release/verify-existing-release-assets.sh" \
  "$complete_dir" "$fixture"
printf 'changed\n' >> "$fixture/$(basename "$complete_dir"/*ubuntu-22.04.tar.gz)"

set +e
comparison_output=$(
  bash "$repository_root/scripts/release/verify-existing-release-assets.sh" \
    "$complete_dir" "$fixture" 2>&1
)
comparison_status=$?
set -e

if [[ "$comparison_status" -eq 0 ]]; then
  echo "Existing-release validation accepted changed archive bytes" >&2
  printf '%s\n' "$comparison_output" >&2
  exit 1
fi

fixture=$(new_fixture historical-single-archive-release)
cp "$complete_dir"/*ubuntu-22.04.tar.gz "$fixture/"
(
  cd "$fixture"
  sha256sum ./*.tar.gz > SHA256SUMS
)

set +e
historical_output=$(
  bash "$repository_root/scripts/release/verify-existing-release-assets.sh" \
    "$complete_dir" "$fixture" 2>&1
)
historical_status=$?
set -e

if [[ "$historical_status" -eq 0 ]] ||
    ! grep -Fq 'may predate the dual-archive format' <<<"$historical_output"; then
  echo "Historical release validation did not require a human decision" >&2
  printf '%s\n' "$historical_output" >&2
  exit 1
fi

fixture=$(new_fixture mismatched-commit)
cp "$complete_dir"/*ubuntu-22.04.tar.gz "$fixture/"
rm -rf -- "$output_dir"
mkdir -p "$output_dir"
SOURCE_COMMIT=1111111111111111111111111111111111111111 \
  bash "$package_script" "$version" 26.04 "$publish_dir" "$output_dir" \
  "$(miningcore_linux_release_target_image 26.04)"
cp "$output_dir"/*ubuntu-26.04.tar.gz "$fixture/"
expect_collection_failure "archives built from different commits" "$fixture" \
  "records source commit 1111111111111111111111111111111111111111; expected"

fixture=$(new_fixture unsupported-target)
cp "$complete_dir"/*ubuntu-26.04.tar.gz "$fixture/"
cp "$complete_dir"/*ubuntu-22.04.tar.gz \
  "$fixture/miningcore-${version}-linux-x64-ubuntu-24.04.tar.gz"
expect_collection_failure "an unsupported Ubuntu target" "$fixture" \
  'Unsupported Ubuntu archive target in filename: 24.04'

fixture=$(new_fixture missing-archive)
cp "$complete_dir"/*ubuntu-26.04.tar.gz "$fixture/"
expect_collection_failure "a missing compatibility archive" "$fixture" \
  'Expected exactly 2 Ubuntu archives, found 1'
expect_collection_failure "an attempt-scoped partial rerun" "$fixture" \
  'use Re-run all jobs, not Re-run failed jobs'

fixture=$(new_fixture extra-archive)
cp "$complete_dir"/*.tar.gz "$fixture/"
cp "$complete_dir"/*ubuntu-22.04.tar.gz \
  "$fixture/miningcore-${version}-linux-x64-ubuntu-24.04.tar.gz"
expect_collection_failure "an unexpected third archive" "$fixture" \
  'Expected exactly 2 Ubuntu archives, found 3'

fixture=$(new_fixture damaged-archive)
cp "$complete_dir"/*.tar.gz "$fixture/"
printf 'not a tar archive\n' > "$fixture/$(basename "$complete_dir"/*ubuntu-22.04.tar.gz)"
expect_collection_failure "a damaged archive" "$fixture" \
  'is not a valid gzip-compressed tar archive'

fixture=$(new_fixture target-mismatch)
cp "$complete_dir"/*.tar.gz "$fixture/"
repack_fixture "$fixture/$(basename "$complete_dir"/*ubuntu-26.04.tar.gz)" \
  replace-target 'Ubuntu 22.04 x64'
expect_collection_failure "filename and BUILD-INFO target disagreement" "$fixture" \
  'records an unexpected target: Ubuntu 22.04 x64'

fixture=$(new_fixture build-image-mismatch)
cp "$complete_dir"/*.tar.gz "$fixture/"
repack_fixture "$fixture/$(basename "$complete_dir"/*ubuntu-26.04.tar.gz)" \
  replace-build-image \
  'ubuntu:26.04@sha256:1111111111111111111111111111111111111111111111111111111111111111'
expect_collection_failure "an unreviewed build-image identity" "$fixture" \
  'records an unexpected build image'

fixture=$(new_fixture missing-build-info)
cp "$complete_dir"/*.tar.gz "$fixture/"
repack_fixture "$fixture/$(basename "$complete_dir"/*ubuntu-22.04.tar.gz)" \
  remove-build-info
expect_collection_failure "a missing BUILD-INFO" "$fixture" 'does not contain'

fixture=$(new_fixture malformed-commit)
cp "$complete_dir"/*.tar.gz "$fixture/"
repack_fixture "$fixture/$(basename "$complete_dir"/*ubuntu-22.04.tar.gz)" \
  replace-commit not-a-commit
expect_collection_failure "a malformed source commit" "$fixture" \
  'records an invalid source commit: not-a-commit'

fixture=$(new_fixture version-mismatch)
cp "$complete_dir"/*ubuntu-22.04.tar.gz "$fixture/"
rm -rf -- "$output_dir"
mkdir -p "$output_dir"
bash "$package_script" v1.2.4 26.04 "$publish_dir" "$output_dir" \
  "$(miningcore_linux_release_target_image 26.04)"
cp "$output_dir"/*ubuntu-26.04.tar.gz "$fixture/"
expect_collection_failure "archives from different releases" "$fixture" \
  'records release v1.2.4; expected v1.2.3-rc.4'

set +e
invalid_output=$(
  bash "$package_script" v1.2.3 24.04 "$publish_dir" "$output_dir" \
    unsupported 2>&1
)
invalid_status=$?
set -e

if [[ "$invalid_status" -ne 64 ]]; then
  echo "Unsupported archive target returned $invalid_status instead of 64" >&2
  exit 1
fi

set +e
unpinned_output=$(
  bash "$package_script" "$version" 26.04 "$publish_dir" "$output_dir" \
    ubuntu:26.04 2>&1
)
unpinned_status=$?
set -e

if [[ "$unpinned_status" -ne 64 ]] ||
    ! grep -Fq 'Expected pinned image:' <<<"$unpinned_output"; then
  echo "Package assembly did not reject an unpinned build image" >&2
  printf '%s\n' "$unpinned_output" >&2
  exit 1
fi

if ! grep -Fq 'Supported targets: 26.04, 22.04' <<<"$invalid_output"; then
  echo "Unsupported archive target did not report the target allowlist" >&2
  printf '%s\n' "$invalid_output" >&2
  exit 1
fi

echo "Linux release package target validation passed"
