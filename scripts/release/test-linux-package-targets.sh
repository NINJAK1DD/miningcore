#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
package_script="$repository_root/scripts/release/package-linux-x64.sh"
work_dir=$(mktemp -d)
publish_dir="$work_dir/publish"
output_dir="$work_dir/dist"
complete_dir="$work_dir/complete"
mismatched_dir="$work_dir/mismatched"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$publish_dir" "$output_dir" "$complete_dir" "$mismatched_dir"
printf '#!/usr/bin/env bash\nexit 0\n' > "$publish_dir/Miningcore"
chmod 0755 "$publish_dir/Miningcore"

while IFS= read -r library; do
  [ -n "$library" ] || continue
  : > "$publish_dir/$library"
done < "$repository_root/scripts/release/linux-native-libraries.txt"

if ! compgen -G "$publish_dir/*.so" >/dev/null; then
  echo "Unable to create the native-library package fixture" >&2
  exit 1
fi

SOURCE_COMMIT=$(git -C "$repository_root" rev-parse HEAD)
SOURCE_DATE_EPOCH=$(git -C "$repository_root" show -s --format=%ct "$SOURCE_COMMIT")
export SOURCE_COMMIT SOURCE_DATE_EPOCH

for ubuntu_version in 22.04 26.04; do
  rm -rf -- "$output_dir"
  mkdir -p "$output_dir"

  bash "$package_script" v1.2.3-rc.4 "$ubuntu_version" "$publish_dir" "$output_dir"

  archive="miningcore-v1.2.3-rc.4-linux-x64-ubuntu-${ubuntu_version}.tar.gz"
  package_root="miningcore-v1.2.3-rc.4-linux-x64-ubuntu-${ubuntu_version}"

  test -f "$output_dir/$archive"
  (cd "$output_dir" && sha256sum --check SHA256SUMS)

  target=$(tar -xOf "$output_dir/$archive" "$package_root/BUILD-INFO" |
    sed -n 's/^Target: //p')
  if [ "$target" != "Ubuntu $ubuntu_version x64" ]; then
    echo "$archive records an unexpected target: $target" >&2
    exit 1
  fi

  cp "$output_dir/$archive" "$complete_dir/"
done

bash "$repository_root/scripts/release/collect-linux-release-assets.sh" "$complete_dir"
(cd "$complete_dir" && sha256sum --check --strict SHA256SUMS)

cp "$complete_dir"/*ubuntu-22.04.tar.gz "$mismatched_dir/"
rm -rf -- "$output_dir"
mkdir -p "$output_dir"
SOURCE_COMMIT=1111111111111111111111111111111111111111 \
  bash "$package_script" v1.2.3-rc.4 26.04 "$publish_dir" "$output_dir"
cp "$output_dir"/*ubuntu-26.04.tar.gz "$mismatched_dir/"

set +e
mismatch_output=$(
  bash "$repository_root/scripts/release/collect-linux-release-assets.sh" \
    "$mismatched_dir" 2>&1
)
mismatch_status=$?
set -e

if [ "$mismatch_status" -eq 0 ]; then
  echo "Release collection accepted archives built from different commits" >&2
  exit 1
fi

if ! grep -Fq 'built from different source commits' <<<"$mismatch_output"; then
  echo "Release collection did not diagnose mismatched source commits" >&2
  printf '%s\n' "$mismatch_output" >&2
  exit 1
fi

set +e
invalid_output=$(bash "$package_script" v1.2.3 24.04 "$publish_dir" "$output_dir" 2>&1)
invalid_status=$?
set -e

if [ "$invalid_status" -ne 64 ]; then
  echo "Unsupported archive target returned $invalid_status instead of 64" >&2
  exit 1
fi

if ! grep -Fq 'Supported targets: 22.04, 26.04' <<<"$invalid_output"; then
  echo "Unsupported archive target did not report the target allowlist" >&2
  exit 1
fi

echo "Linux release package target validation passed"
