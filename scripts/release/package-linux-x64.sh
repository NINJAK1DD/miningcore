#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 5 ]]; then
    echo "Usage: $0 <version> <ubuntu-version> <publish-directory>" >&2
    echo "  <output-directory> <build-image>" >&2
    exit 64
fi

version="$1"
ubuntu_version="$2"
publish_dir="$(realpath "$3")"
output_dir="$(realpath -m "$4")"
build_image="$5"
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$repository_root/scripts/release/linux-release-targets.sh"

if [[ ! "$version" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
    echo "Version contains unsupported characters: $version" >&2
    exit 64
fi

if ! miningcore_linux_release_target_supported "$ubuntu_version"; then
    echo "Unsupported Ubuntu release archive target: $ubuntu_version" >&2
    echo "Supported targets: $(miningcore_linux_release_target_list)" >&2
    exit 64
fi

expected_build_image=$(miningcore_linux_release_target_image "$ubuntu_version")
if [[ "$build_image" != "$expected_build_image" ]]; then
    echo "Unexpected Ubuntu $ubuntu_version build image: $build_image" >&2
    echo "Expected pinned image: $expected_build_image" >&2
    exit 64
fi

if [[ ! -x "$publish_dir/Miningcore" ]]; then
    echo "Published Miningcore executable was not found in $publish_dir" >&2
    exit 1
fi

bash "$repository_root/scripts/release/test-linux-native-inventory.sh" "$publish_dir"

source_commit="${SOURCE_COMMIT:-$(git -C "$repository_root" rev-parse HEAD)}"

if [[ -n "${SOURCE_DATE_EPOCH:-}" ]]; then
    source_date_epoch="$SOURCE_DATE_EPOCH"
else
    source_date_epoch="$(git -C "$repository_root" show -s --format=%ct "$source_commit")"
fi
package_name="miningcore-${version}-linux-x64-ubuntu-${ubuntu_version}"
archive_name="${package_name}.tar.gz"
work_dir="$(mktemp -d)"
package_root="$work_dir/$package_name"

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$package_root" "$package_root/migrations" "$package_root/systemd" "$output_dir"
cp -a "$publish_dir/." "$package_root/"
cp "$repository_root/README.md" "$repository_root/LICENSE" \
    "$repository_root/config.example.json" "$package_root/"
cp "$repository_root/docs/releases.md" "$package_root/INSTALL.md"
cp -a "$repository_root/docs" "$package_root/docs"
cp "$repository_root"/src/Miningcore/Persistence/Postgres/Scripts/*.sql \
    "$package_root/migrations/"
cp "$repository_root/packaging/systemd/miningcore.service" \
    "$package_root/systemd/"

cat > "$package_root/BUILD-INFO" <<EOF
Version: $version
Source commit: $source_commit
Target: Ubuntu $ubuntu_version x64
Build image: $build_image
Framework: net10.0 framework-dependent
Build epoch: $source_date_epoch
EOF

find "$package_root" -type d -exec chmod 0755 {} +
find "$package_root" -type f -exec chmod 0644 {} +
chmod 0755 "$package_root/Miningcore"
find "$package_root" -maxdepth 1 -type f -name '*.so' -exec chmod 0755 {} +

archive_path="$output_dir/$archive_name"
tar --sort=name \
    --mtime="@$source_date_epoch" \
    --owner=0 --group=0 --numeric-owner \
    -C "$work_dir" -cf - "$package_name" |
    gzip -n -9 > "$archive_path"

(
    cd "$output_dir"
    sha256sum "$archive_name" > SHA256SUMS
)

echo "Created $archive_path"
echo "Created $output_dir/SHA256SUMS"
