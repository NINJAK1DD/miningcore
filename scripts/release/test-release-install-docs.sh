#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="$repository_root/docs/releases.md"
readme="$repository_root/README.md"
migration_document="$repository_root/docs/dotnet-6-to-10-migration.md"
source_dockerfile="$repository_root/Dockerfile"
release_dockerfile="$repository_root/packaging/docker/Dockerfile.release"
release_workflow="$repository_root/.github/workflows/release.yml"
zeromq_probe="$repository_root/scripts/release/fixtures/zeromq-runtime-probe/Program.cs"
capability_dir=
normalized_document=$(tr '\r\n\t' '   ' < "$document" | sed -E 's/[[:space:]]+/ /g')

cleanup() {
  if [[ -n "$capability_dir" ]]; then
    rm -f -- "$capability_dir/link"
    rmdir -- "$capability_dir/target" "$capability_dir" 2>/dev/null || true
  fi
}
trap cleanup EXIT

selection_block=$(awk '
  { sub(/\r$/, "") }
  /^export MININGCORE_VERSION=/ { capture = 1 }
  capture && /^```$/ { exit }
  capture { print }
' "$document")
install_block=$(awk '
  { sub(/\r$/, "") }
  /^## Install the archive$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$document")
verification_block=$(awk '
  { sub(/\r$/, "") }
  /^Compare the extracted metadata with the binary/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$document")

assert_contains() {
  local label=$1
  local expected=$2

  if ! grep -Fq "$expected" "$document"; then
    echo "Release installation guide is missing $label" >&2
    exit 1
  fi
}

assert_file_contains() {
  local label=$1
  local expected=$2
  local source=$3

  if ! grep -Fq "$expected" "$source"; then
    echo "$source is missing $label" >&2
    exit 1
  fi
}

assert_prose_contains() {
  local label=$1
  local expected=$2

  if ! grep -Fq "$expected" <<<"$normalized_document"; then
    echo "Release installation guide is missing $label" >&2
    exit 1
  fi
}

assert_contains 'the Ubuntu 26.04 choose-one label' \
  '(choose this on Ubuntu 26.04)'
assert_contains 'the Ubuntu 22.04 choose-one label' \
  '(choose this on Ubuntu 22.04)'
assert_file_contains 'the RC.11 release example' \
  'export MININGCORE_VERSION=v0.1.0-rc.11' "$document"
assert_file_contains 'the RC.11 container example' \
  'MININGCORE_VERSION=v0.1.0-rc.11' "$readme"
assert_contains 'the RC.11 recovery example' \
  'export TAG=v0.1.0-rc.11'
assert_contains 'the RC.11 tagging example' \
  'NEXT_VERSION=v0.1.0-rc.11'
assert_contains 'the interactive-shell safety explanation' \
  'instead of closing an SSH session'
assert_contains 'the successful verification marker' \
  'READY: $archive is verified and ready to install'
assert_contains 'the all-jobs release retry rule' \
  'select **Re-run all jobs**'
assert_contains 'the failed-jobs retry prohibition' \
  'Do not use **Re-run failed jobs**'
assert_contains 'the staged-publication state model' \
  '**No publication:** no release or version-scoped staging tag'
assert_prose_contains 'the durable-release promotion boundary' \
  'published GitHub Release permits the public version tags'
assert_contains 'the publication conflict stop' \
  'HUMAN ACTION REQUIRED'
assert_contains 'the non-transactional publication boundary' \
  'do not provide a shared transaction'
assert_contains 'the authenticated draft-discovery command' \
  'gh api --paginate --slurp'
assert_contains 'the exact-one release selection guard' \
  'expected exactly one matching draft or published release'
assert_contains 'the bounded release-list visibility policy' \
  'retries those visibility checks for a bounded period'
assert_contains 'the repository-wide publication queue' \
  'queues up to 100 release tags'
assert_contains 'the publication queue timeout' \
  '60-minute job'
assert_contains 'the draft ownership boundary' \
  'deterministic collision marker containing the repository'
assert_contains 'the draft marker authorization limitation' \
  'authorization control because a maintainer'
assert_contains 'the draft edit recovery procedure' \
  'Do not manually edit the generated title'
assert_contains 'the deleted-draft staging cleanup boundary' \
  'complete the orphan-tag evidence and cleanup procedure'
assert_contains 'the foreign draft adoption prohibition' \
  'Never make an unrelated draft pass'
assert_contains 'the streamed bounded upload policy' \
  'uploads are streamed'
assert_contains 'the pruned-stage recovery rule' \
  'at least one immutable GHCR version tag still matches the recorded'
assert_contains 'the GitHub CLI publication floor' \
  'Publication requires GitHub CLI'
assert_contains 'the orphan-stage recovery warning' \
  'Never delete a staging tag merely to bypass a digest or'
assert_contains 'the declared build-image contract' \
  'workflow-declared'
assert_contains 'the Ubuntu 22.04 curl compatibility statement' \
  'curl version supplied by Ubuntu 22.04'
assert_contains 'the CryptoNote Boost.Regex runtime provider' \
  'libboost-regex1.90.0'
assert_contains 'the ZanoNote Boost.Locale runtime provider' \
  'libboost-locale1.90.0'
assert_contains 'the ZanoNote Boost.Serialization runtime provider' \
  'libboost-serialization1.90.0'
assert_contains 'the Ubuntu 22.04 Boost.Regex runtime provider' \
  'libboost-regex1.74.0'
assert_contains 'the Ubuntu 22.04 Boost.Locale runtime provider' \
  'libboost-locale1.74.0'
assert_contains 'the Ubuntu 22.04 Boost.Serialization runtime provider' \
  'libboost-serialization1.74.0'
assert_file_contains 'the Ubuntu 24.04 Boost.Locale runtime provider' \
  'libboost-locale1.83.0' "$migration_document"
assert_file_contains 'the Ubuntu 24.04 Boost.Regex runtime provider' \
  'libboost-regex1.83.0' "$migration_document"
assert_file_contains 'the Ubuntu 24.04 Boost.Serialization runtime provider' \
  'libboost-serialization1.83.0' "$migration_document"

if grep -Eq 'libboost-(locale|regex|serialization)-dev' \
  "$document" "$migration_document"; then
  echo 'Runtime installation documentation still names Boost development packages' >&2
  exit 1
fi

if awk '
  /^```/ { code = !code; next }
  code && /libsodium-dev/ { found = 1 }
  END { exit found ? 0 : 1 }
' "$document" "$migration_document"; then
  echo 'Runtime installation commands still name the redundant libsodium-dev package' >&2
  exit 1
fi

for runtime_package in libsodium23 libzmq3-dev; do
  assert_file_contains "the documented $runtime_package provider" \
    "$runtime_package" "$document"
  assert_file_contains "the migration $runtime_package provider" \
    "$runtime_package" "$migration_document"
done

for dockerfile in "$source_dockerfile" "$release_dockerfile"; do
  assert_file_contains 'the Ubuntu 26.04 Boost.Locale runtime package' \
    'libboost-locale1.90.0' "$dockerfile"
  assert_file_contains 'the Ubuntu 26.04 Boost.Regex runtime package' \
    'libboost-regex1.90.0' "$dockerfile"
  assert_file_contains 'the Ubuntu 26.04 Boost.Serialization runtime package' \
    'libboost-serialization1.90.0' "$dockerfile"

  if grep -Eq 'libboost-(locale|regex|serialization)-dev' "$dockerfile"; then
    echo "$dockerfile installs Boost development packages in its runtime stage" >&2
    exit 1
  fi

  runtime_stage=$(awk '
    /^FROM / { stage = "" }
    { stage = stage $0 ORS }
    END { printf "%s", stage }
  ' "$dockerfile")
  for runtime_package in libsodium23 libzmq3-dev; do
    if ! grep -Fq "$runtime_package" <<< "$runtime_stage"; then
      echo "$dockerfile runtime stage omits $runtime_package" >&2
      exit 1
    fi
  done
  if grep -Fq 'libsodium-dev' <<< "$runtime_stage"; then
    echo "$dockerfile directly lists redundant libsodium-dev in its runtime stage" >&2
    exit 1
  fi
done
assert_file_contains 'the Release pull-request trigger' \
  'pull_request:' "$release_workflow"
assert_file_contains 'the source Dockerfile pull-request build' \
  'file: Dockerfile' "$release_workflow"
assert_file_contains 'the packaged Dockerfile pull-request build' \
  'file: packaging/docker/Dockerfile.release' "$release_workflow"
assert_file_contains 'the real managed ZeroMQ probe' \
  'new ZSocket' "$zeromq_probe"
if [[ $(grep -Fc 'Smoke-test managed ZeroMQ binding' "$release_workflow") -ne 2 ]]; then
  echo 'Both final container images must execute the managed ZeroMQ runtime probe' >&2
  exit 1
fi
assert_prose_contains 'the apt-package validation boundary' \
  'validates the current apt package names'
assert_prose_contains 'the apt-package monitor exclusion' \
  'outside the digest-based release image-pin monitor'
assert_prose_contains 'the unversioned ZeroMQ loader requirement' \
  'Linux needs the unversioned'
assert_prose_contains 'the accepted ZeroMQ development-dependency exception' \
  'also pulls development dependencies'
assert_file_contains 'the migration ZeroMQ development-dependency exception' \
  "also installs \`libzmq3-dev\`'s development-package dependencies" \
  "$migration_document"
assert_prose_contains 'the final-image ZeroMQ load probe' \
  'managed ZeroMQ load inside each final image'
assert_contains 'the all-library managed export contract' \
  'every managed entry point must be a callable function in that library'
assert_contains 'the all-library relocation contract' \
  'weak-import inspection then reject missing dependencies'
assert_contains 'the native plugin provider-closure reversal' \
  'previous sibling-plugin assumption is deliberately reversed'
assert_prose_contains 'the generated interop exclusion' \
  "Generated \`bin\` and \`obj\` trees are excluded"
assert_prose_contains 'the unrelated P/Invoke scope boundary' \
  'without applying the wrapper grammar to unrelated'
assert_prose_contains 'the named native-import constructor contract' \
  'correctly named constructor arguments'
assert_prose_contains 'the Unix native-library variation contract' \
  'map to one canonical inventory entry'
assert_prose_contains 'the path-qualified native-import boundary' \
  'relative or absolute path is rejected'
assert_prose_contains 'the canonical ELF symbol-version contract' \
  'must use canonical unversioned symbol names'
assert_contains 'the CryptoNote exception-containment boundary' \
  'daemon-supplied block template from unwinding C++ through P/Invoke'
assert_contains 'the path-filtered branch-protection warning' \
  'Do not configure it as a required'

for stale_version in v0.1.0-rc.9 v0.1.0-rc.10; do
  if grep -Fq "$stale_version" "$readme" "$document"; then
    echo "Release documentation still references stale version $stale_version" >&2
    exit 1
  fi
done

recovery_section=$(awk '
  /^### Recover an interrupted publication$/ { capture = 1 }
  capture { print }
' "$document")
if grep -Fq 'releases/tags/$TAG' <<<"$recovery_section"; then
  echo 'Release recovery documentation still uses the published-only tag endpoint' >&2
  exit 1
fi

if grep -Eq '(^|[[:space:]])exit([[:space:]]|$)' <<<"$selection_block"; then
  echo "The copy-paste release selection block must not exit an interactive shell" >&2
  exit 1
fi

bash -n <<<"$selection_block"
bash -n <<<"$install_block"
bash -n <<<"$verification_block"

for required in \
  'MININGCORE_HOST_RELEASE=' \
  'MININGCORE_RELEASE_READY=' \
  'MININGCORE_INSTALL_READY=' \
  'MININGCORE_DOWNLOAD_DIR=' \
  'if [ -n "$MININGCORE_UBUNTU" ]; then' \
  'if MININGCORE_DOWNLOAD_DIR="$(' \
  'mktemp -d "${TMPDIR:-/tmp}/miningcore-release.XXXXXXXX"' \
  'curl --fail --location --output "$archive_part"' \
  'curl --fail --location --output "$checksum_part"' \
  'sha256sum --ignore-missing --check --strict SHA256SUMS' \
  'export MININGCORE_RELEASE_READY=1' \
  'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' \
  'archive='; do
  if ! grep -Fq "$required" <<<"$selection_block"; then
    echo "Release selection block is missing: $required" >&2
    exit 1
  fi
done

if grep -Fq -- '--remove-on-error' <<<"$selection_block" ||
    grep -Fq -- '--remote-name' <<<"$selection_block"; then
  echo "Release selection block uses a curl option outside the Ubuntu 22.04 contract" >&2
  exit 1
fi

# This test also runs inside each target's pinned release-build userspace. Keep
# every advertised curl option available on the oldest supported Ubuntu target.
curl_help=$(curl --help all)
for curl_option in --fail --location --output; do
  if ! grep -Eq "(^|[[:space:]])${curl_option}([[:space:]=,]|$)" <<<"$curl_help"; then
    echo "The current curl does not support documented option $curl_option" >&2
    exit 1
  fi
done

sha256sum_help=$(sha256sum --help)
for checksum_option in --ignore-missing --strict; do
  if ! grep -Fq -- "$checksum_option" <<<"$sha256sum_help"; then
    echo "The current sha256sum does not support documented option $checksum_option" >&2
    exit 1
  fi
done

if ! grep -Fq -- '--no-target-directory' <<<"$(ln --help)"; then
  echo 'The current ln does not support documented option -T' >&2
  exit 1
fi

capability_dir=$(mktemp -d "${TMPDIR:-/tmp}/miningcore-doc-test.XXXXXXXX")
mkdir "$capability_dir/target"
ln -sfnT "$capability_dir/target" "$capability_dir/link"
test -L "$capability_dir/link"
rm -f -- "$capability_dir/link"
rmdir -- "$capability_dir/target" "$capability_dir"
capability_dir=

for required in \
  'MININGCORE_INSTALL_READY=' \
  'if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then' \
  'test -d "$release_dir"' \
  'if [ ! -e /etc/miningcore/config.json ]; then' \
  'sudo cp "$release_dir/config.example.json" /etc/miningcore/config.json' \
  'sudo ln -sfnT "$release_dir" /opt/miningcore' \
  'MININGCORE_RELEASE_READY=' \
  'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' \
  'WARN: remove the verified release files from $MININGCORE_DOWNLOAD_DIR' \
  'export MININGCORE_INSTALL_READY=1' \
  'STOP: installation failed; /opt/miningcore was not changed'; do
  if ! grep -Fq "$required" <<<"$install_block"; then
    echo "Release installation block is missing: $required" >&2
    exit 1
  fi
done

if grep -Fq 'sudo cp /opt/miningcore/config.example.json' <<<"$install_block"; then
  echo "Release installation block reads configuration through the old live symlink" >&2
  exit 1
fi

find_unique_line() {
  local label=$1
  local pattern=$2
  local source=$3
  local -a matches

  mapfile -t matches < <(grep -nF "$pattern" <<<"$source" | cut -d: -f1)
  if [[ "${#matches[@]}" -ne 1 ]]; then
    echo "Installation-order anchor '$label' occurred ${#matches[@]} times; expected 1" >&2
    return 1
  fi

  printf '%s\n' "${matches[0]}"
}

# Keep these as bare assignments: set -e makes an ambiguous or missing anchor fail the test.
directory_guard_line=$(find_unique_line directory-guard 'test -d "$release_dir"' "$install_block")
symlink_line=$(
  find_unique_line stable-symlink \
    'sudo ln -sfnT "$release_dir" /opt/miningcore' "$install_block"
)
release_consumed_line=$(
  find_unique_line release-consumed 'MININGCORE_RELEASE_READY=' "$install_block"
)
cleanup_line=$(
  find_unique_line download-cleanup 'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' "$install_block"
)

duplicate_anchor_block="${install_block}"$'\nMININGCORE_RELEASE_READY='
if find_unique_line duplicate-guard 'MININGCORE_RELEASE_READY=' \
    "$duplicate_anchor_block" >/dev/null 2>&1; then
  echo 'The installation-order anchor guard accepted an ambiguous duplicate' >&2
  exit 1
fi

if [[ "$symlink_line" -le "$directory_guard_line" ||
    "$release_consumed_line" -le "$symlink_line" ||
    "$cleanup_line" -le "$release_consumed_line" ]]; then
  echo 'The stable symlink, readiness reset and cleanup operations are not safely ordered' >&2
  exit 1
fi

blocked_install_output=$(
  env -u MININGCORE_RELEASE_READY -u MININGCORE_INSTALL_READY \
    bash -c "$install_block" 2>&1
)
if ! grep -Fq 'no release archive passed' <<<"$blocked_install_output"; then
  echo "The installation block did not stop cleanly without a verified archive" >&2
  exit 1
fi

for required in \
  'if [ "${MININGCORE_INSTALL_READY:-}" = 1 ]' \
  '[ -n "${release_dir:-}" ] && [ -d "$release_dir" ]' \
  'cat "$release_dir/BUILD-INFO"' \
  'STOP: no release from this installation run is available to verify'; do
  if ! grep -Fq "$required" <<<"$verification_block"; then
    echo "Release verification block is missing: $required" >&2
    exit 1
  fi
done

blocked_verification_output=$(
  env -u MININGCORE_INSTALL_READY -u release_dir bash -c "$verification_block" 2>&1
)
if ! grep -Fq 'no release from this installation run' <<<"$blocked_verification_output"; then
  echo 'The verification block did not reject stale or absent installation state' >&2
  exit 1
fi

echo "Release installation documentation invariants passed"
