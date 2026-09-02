#!/usr/bin/env bash
# Literal documentation contracts intentionally must not expand shell expressions.
# shellcheck disable=SC2016

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="$repository_root/docs/releases.md"
readme="$repository_root/README.md"
config_example="$repository_root/config.example.json"
pps_document="$repository_root/docs/pps.md"
database_document="$repository_root/docs/database.md"
merged_mining_document="$repository_root/docs/merged-mining-litecoin-dogecoin.md"
bitcoin_direct_document="$repository_root/docs/bitcoin-direct-solo.md"
licence_document="$repository_root/docs/lucky-penny-licence.md"
migration_document="$repository_root/docs/dotnet-6-to-10-migration.md"
source_dockerfile="$repository_root/Dockerfile"
release_dockerfile="$repository_root/packaging/docker/Dockerfile.release"
release_workflow="$repository_root/.github/workflows/release.yml"
zeromq_probe="$repository_root/scripts/release/fixtures/zeromq-runtime-probe/Program.cs"
capability_dir=
fixture_dir=
normalized_document=$(tr '\r\n\t' '   ' < "$document" | sed -E 's/[[:space:]]+/ /g')
release_document=$(tr -d '\r' < "$document")

cleanup() {
  if [[ -n "$capability_dir" ]]; then
    rm -f -- "$capability_dir/link"
    rmdir -- "$capability_dir/target" "$capability_dir" 2>/dev/null || true
  fi

  if [[ -n "$fixture_dir" ]]; then
    rm -rf -- "$fixture_dir"
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
readme_install_block=$(awk '
  { sub(/\r$/, "") }
  /^### 3\. Install the versioned application$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
readme_database_block=$(awk '
  { sub(/\r$/, "") }
  /^### 4\. Create PostgreSQL and load the schema$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
release_database_block=$(awk '
  { sub(/\r$/, "") }
  /^For a new database, use the packaged schema:$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$document")
partition_block=$(awk '
  { sub(/\r$/, "") }
  /^### 7\. Optional: partition the `shares` table$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
quickstart_configuration_section=$(awk '
  { sub(/\r$/, "") }
  /^### 5\. Choose and edit a configuration$/ { capture = 1 }
  capture { print }
  capture && /^### 6\. Install, secure and synchronize the coin daemons$/ { exit }
' "$readme")
direct_solo_install_block=$(awk '
  { sub(/\r$/, "") }
  /^If selecting direct settlement, install the reviewed$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
quickstart_placeholder_block=$(awk '
  { sub(/\r$/, "") }
  /^fail-closed check\. Continue only when it prints `READY`/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
database_new_install_section=$(awk '
  { sub(/\r$/, "") }
  /^## New installation$/ { capture = 1 }
  capture { print }
  capture && /^## Back up and restore$/ { exit }
' "$database_document")
database_upgrade_section=$(awk '
  { sub(/\r$/, "") }
  /^## Upgrade an existing database$/ { capture = 1 }
  capture { print }
  capture && /^## / && !/^## Upgrade an existing database$/ { exit }
' "$database_document")
source_build_section=$(awk '
  { sub(/\r$/, "") }
  /^For a fresh source-only installation,/ { capture = 1 }
  capture && /^## API and web front ends$/ { exit }
  capture { print }
' "$readme")
source_placeholder_block=$(awk '
  { sub(/\r$/, "") }
  /^source_placeholder_status=0$/ { capture = 1 }
  capture && /^```$/ { exit }
  capture { print }
' "$readme")
upgrade_block=$(awk '
  { sub(/\r$/, "") }
  /^## Upgrade or roll back$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$document")

assert_contains() {
  local label=$1
  local expected=$2

  if ! grep -Fq -- "$expected" "$document"; then
    echo "Release installation guide is missing $label" >&2
    exit 1
  fi
}

assert_file_contains() {
  local label=$1
  local expected=$2
  local source=$3

  if ! grep -Fq -- "$expected" "$source"; then
    echo "$source is missing $label" >&2
    exit 1
  fi
}

assert_prose_contains() {
  local label=$1
  local expected=$2

  if ! grep -Fq -- "$expected" <<<"$normalized_document"; then
    echo "Release installation guide is missing $label" >&2
    exit 1
  fi
}

assert_contains 'the Ubuntu 26.04 choose-one label' \
  '(choose this on Ubuntu 26.04)'
assert_contains 'the Ubuntu 22.04 choose-one label' \
  '(choose this on Ubuntu 22.04)'
assert_file_contains 'the v0.3.0-rc.1 release example' \
  'export MININGCORE_VERSION=v0.3.0-rc.1' "$document"
assert_file_contains 'the v0.3.0-rc.1 container example' \
  'MININGCORE_VERSION=v0.3.0-rc.1' "$readme"
assert_file_contains 'the v0.3.0-rc.1 database migration example' \
  'export MININGCORE_VERSION=v0.3.0-rc.1' "$database_document"
assert_file_contains 'the v0.2.1 hotfix task route' \
  '| v0.2.0 `SOLO`/`SOLO` merged-mining failure | [v0.2.1 hotfix](#v021-hotfix) |' "$document"
assert_file_contains 'the v0.2.1 troubleshooting route' \
  '[v0.2.1 hotfix](releases.md#v021-hotfix)' "$repository_root/docs/troubleshooting.md"
assert_file_contains 'the quick-start Ubuntu 26.04 runtime package' \
  'aspnetcore-runtime-10.0' "$readme"
assert_file_contains 'the quick-start checksum readiness latch' \
  'export MININGCORE_QUICKSTART_READY=1' "$readme"
assert_file_contains 'the quick-start guarded installation' \
  'if [ "${MININGCORE_QUICKSTART_READY:-}" = 1 ]; then' "$readme"
assert_file_contains 'the quick-start existing-config guard' \
  'if [ ! -e /etc/miningcore/config.json ]; then' "$readme"
assert_file_contains 'the quick-start PostgreSQL role creation' \
  'sudo -u postgres createuser --pwprompt miningcore' "$readme"
assert_contains 'the packaged PostgreSQL role creation' \
  'sudo -u postgres createuser --pwprompt miningcore'
assert_contains 'the packaged PostgreSQL database creation' \
  'sudo -u postgres createdb --owner=miningcore miningcore'
assert_file_contains 'the quick-start packaged schema path' \
  '/opt/miningcore/migrations/createdb.sql' "$readme"
assert_file_contains 'the quick-start advanced partitioning appendix' \
  '/opt/miningcore/migrations/createdb_postgresql_11_appendix.sql' "$readme"
assert_file_contains 'the quick-start partition example' \
  'CREATE TABLE public.shares_bitcoin_solo' "$readme"
assert_file_contains 'the quick-start daemon ownership boundary' \
  'Miningcore does not install or manage the full nodes' "$readme"
assert_file_contains 'the installed coin-definition path' \
  '/opt/miningcore/coins.json' "$readme"
assert_file_contains 'the quick-start persistent logging path' \
  'logging.logBaseDirectory` to `/var/log/miningcore' "$readme"
assert_file_contains 'the quick-start recovery state path' \
  'shareRecoveryStateDirectory` to `/var/lib/miningcore' "$readme"
assert_file_contains 'the quick-start systemd unit path' \
  '/etc/systemd/system/miningcore.service' "$readme"
assert_file_contains 'the quick-start service enablement' \
  'sudo systemctl enable --now miningcore' "$readme"
assert_file_contains 'the quick-start AutoMapper licensing guidance' \
  'Review AutoMapper licensing and configure an applicable Lucky Penny key' "$readme"
assert_file_contains 'the quick-start Lucky Penny guide link' \
  '[Lucky Penny licence-key guide](docs/lucky-penny-licence.md)' "$readme"
assert_file_contains 'the Lucky Penny documentation-table entry' \
  '| Review AutoMapper licensing or configure a Lucky Penny key |' "$readme"
assert_file_contains 'the official AutoMapper licence acquisition link' \
  'https://automapper.io/' "$readme"
assert_file_contains 'the official Lucky Penny registration link' \
  'https://luckypennysoftware.com/Identity/Account/Register' "$readme"
assert_file_contains 'the licence guide acquisition route' \
  'https://automapper.io/' "$licence_document"
assert_file_contains 'the licence guide account-registration route' \
  'https://luckypennysoftware.com/Identity/Account/Register' "$licence_document"
assert_file_contains 'the neutral AutoMapper dual-licensing boundary' \
  'dual-licensed under RPL-1.5 or Lucky Penny commercial terms' "$licence_document"
assert_file_contains 'the quick-start private-service boundary' \
  'Keep PostgreSQL, daemon/wallet RPC, the administrative API and metrics private' "$readme"
if [[ $(grep -Ec '/CHANGE_ME\|REPLACE_WITH_/' "$readme") -ne 2 ]]; then
  echo 'The quick-start and source-build gates do not share the complete placeholder vocabulary' >&2
  exit 1
fi
assert_file_contains 'the quick-start placeholder gate' \
  'quickstart_placeholder_status=0' "$readme"
assert_file_contains 'the quick-start privileged-read preflight' \
  'if sudo -v &&' "$readme"
assert_file_contains 'the guarded-block sudo-validation requirement' \
  'require an account for which `sudo -v` succeeds' "$readme"
assert_file_contains 'the quick-start placeholder inspection failure boundary' \
  'STOP: could not inspect /etc/miningcore/config.json' "$readme"
assert_file_contains 'the manual source-build editor boundary' \
  '${EDITOR:-vi} build/config.json' "$readme"
assert_file_contains 'the manual source-build placeholder gate' \
  'source_placeholder_status=0' "$readme"
assert_file_contains 'the manual source-build inspection failure boundary' \
  'STOP: could not inspect build/config.json' "$readme"
assert_file_contains 'the manual source-build launch boundary' \
  'Only after the check prints `READY`, start the published binary' "$readme"
assert_file_contains 'the manual source-build systemd layout boundary' \
  'it does not run the development `build/` layout unchanged' "$readme"
assert_file_contains 'the quick-start direct-SOLO opt-in boundary' \
  '#### Optional: enable Bitcoin direct-coinbase SOLO' "$readme"
assert_file_contains 'the quick-start direct-SOLO binary-version boundary' \
  'branch, that means `v0.3.0-rc.1`' "$readme"
assert_file_contains 'the stable-release direct-SOLO skip boundary' \
  'If you substituted the stable `v0.2.1` release in this quick start, skip this entire subsection' \
  "$readme"
assert_file_contains 'the quick-start direct-SOLO conditional fresh-schema boundary' \
  'Only a fresh database created from `v0.3.0-rc.1` has the required schema' \
  "$readme"
assert_file_contains 'the quick-start direct-SOLO existing-database migration route' \
  '[direct-SOLO database migration](docs/bitcoin-direct-solo.md#database-migration)' \
  "$readme"
assert_file_contains 'the quick-start direct-SOLO protected example installation' \
  'direct_solo_source=/opt/miningcore/examples/bitcoin_direct_solo_pool.json' \
  "$readme"
assert_file_contains 'the quick-start direct-SOLO backup boundary' \
  '/etc/miningcore/config.json.before-direct-solo.XXXXXXXX' "$readme"
assert_file_contains 'the quick-start direct-SOLO nonzero failure boundary' \
  'STOP: direct-SOLO example installation failed' "$readme"
assert_file_contains 'the mandatory quick-start edit heading' \
  '#### Edit and validate the configuration' "$readme"
assert_file_contains 'the restored configuration-schema route' \
  '[configuration schema](src/Miningcore/config.schema.json)' "$readme"
assert_file_contains 'the restored source-run health probe' \
  'curl --fail --max-time 5 http://127.0.0.1:4000/api/health-check' "$readme"
assert_file_contains 'the README source-checkout database route' \
  'src/Miningcore/Persistence/Postgres/Scripts/createdb.sql' "$readme"
assert_file_contains 'the DigiByte Odocrypt feature highlight' \
  'Activation- and schedule-aware DigiByte Odocrypt mining' "$readme"
assert_file_contains 'the direct-SOLO migration task-table route' \
  '| Migrate a v0.2.1-or-earlier/pre-PR #135 database before enabling direct Bitcoin SOLO |' "$readme"
assert_file_contains 'the v0.3.0-rc.1 highlight route' \
  '| Evaluate v0.3.0-rc.1 from v0.2.1 | [v0.3.0-rc.1 highlights](#v030-rc1-highlights) |' \
  "$document"
assert_prose_contains 'the v0.3.0-rc.1 candidate warning' \
  'Treat this candidate as staging software'
assert_prose_contains 'the v0.3.0-rc.1 unchanged-pool migration boundary' \
  'No new v0.3.0 database migration is required solely to keep their existing payout behavior.'
assert_file_contains 'the v0.3.0 feature-detail heading' \
  '### Feature detail' "$document"
assert_prose_contains 'the release-locked stable-baseline checklist' \
  "replace the README's named stable-version substitution boundary"
assert_file_contains 'the v0.3.0-rc.1 optional direct-SOLO migration' \
  '`add_bitcoin_direct_solo.sql` from that immutable candidate directory before setting' \
  "$document"
assert_file_contains 'the concise source-build progress contract' \
  "Interactive builds retain .NET's concise progress and elapsed-time display" "$readme"
assert_file_contains 'the terminal-logger opt-out contract' \
  'The standard `MSBUILDTERMINALLOGGER=off` environment setting remains available' "$readme"
assert_prose_contains 'the private source-build audit-log contract' \
  'Warning enforcement uses a separate private normal-verbosity MSBuild log'
assert_contains 'the v0.3.0-rc.1 recovery example' \
  'export TAG=v0.3.0-rc.1'
assert_contains 'the v0.3.0-rc.1 tagging example' \
  'NEXT_VERSION=v0.3.0-rc.1'
assert_file_contains 'the direct PPS Bitcoin-family boundary' \
  'Direct audited `Bitcoin`-family pool' "$pps_document"
assert_file_contains 'the PPS reserve warning' \
  'separately controlled reserve' "$pps_document"
assert_file_contains 'the direct-SOLO opt-in default' \
  'strict JSON Boolean and defaults to `false`' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO canonical option casing' \
  '`soloCoinbasePayout` is case-sensitive' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO decoded block RPC contract' \
  '`getblock <hash> 2` response contains decoded' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO exact rounding rule' \
  'recipient satoshis = floor(coinbasevalue × percentage / 100)' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO candidate migration' \
  '$MININGCORE_CANDIDATE_DIR/migrations/add_bitcoin_direct_solo.sql' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO PostgreSQL-upgrade fail-closed boundary' \
  'If preflight still fails,' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO active-symlink prohibition' \
  'not the active `/opt/miningcore` symlink' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO forward-only binary boundary' \
  'Application rollback across this feature boundary is **not supported after direct work has been' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO rollback evidence query' \
  "WHERE settlementmode = 'coinbase-direct';" "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO journal rollback boundary' \
  'A zero row count does not prove rollback safety after a database-write failure' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO dedicated block identity' \
  '`bitcoin-coinbase-direct` block type' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO final block-weight gate' \
  "Bitcoin's 4,000,000-weight-unit consensus" "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO pre-submission durability boundary' \
  'durable submission-outbox boundary before its' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO exact replay payload' \
  'stores the exact serialized block' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO prepared-state silence' \
  'A prepared row is not announced as found' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO bounded submission rejection' \
  'three definitive misses over at least 30 minutes' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO propagation-safe journal fallback' \
  'ordinary 2/4/8-second database retry ladder' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO external journal line-size contract' \
  'one line of up to 16 MiB' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO deferred fail-stop decision matrix' \
  '| Commit outcome uncertain |' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO retryable database continuation policy' \
  '| Retryable database failure or two-second propagation timeout | Exact recovery-journal record | No deferred fail-stop;' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO non-retryable database fail-stop policy' \
  '| Unexpected non-retryable database failure | Exact recovery-journal record | Generic database-health fail-stop |' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO exceptional-commit replay identity' \
  "stable idempotent identity" "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO active duplicate evidence rule' \
  'accepted response or duplicate transitions to' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO public block page cap' \
  'at most 100 rows per page' "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO pending payload projection' \
  'an immature `observed-active` row remains metadata-only' \
  "$bitcoin_direct_document"
assert_file_contains 'the direct-SOLO bounded terminal reconciliation depth' \
  'within 4,032 blocks of the reported chain tip' "$bitcoin_direct_document"
assert_file_contains 'the pre-release direct-journal quarantine boundary' \
  'Earlier draft builds wrote direct evidence under the historical' \
  "$bitcoin_direct_document"
assert_file_contains 'the release-level direct-SOLO downgrade prohibition' \
  'not roll the binary back below the release containing this feature when' \
  "$document"
for migration in add_auxpow_block_idempotency.sql \
    add_payout_manager_ownership.sql add_share_accounting.sql; do
  assert_file_contains "the PPS $migration migration requirement" \
    "$migration" "$pps_document"
  assert_file_contains "the merged-mining $migration migration requirement" \
    "$migration" "$merged_mining_document"
done
for migration in createdb.sql createdb_postgresql_11_appendix.sql; do
  assert_file_contains "the database-runbook packaged $migration path" \
    "/opt/miningcore/migrations/$migration" "$database_document"
done
for migration in add_auxpow_block_idempotency.sql \
    add_payout_manager_ownership.sql add_share_accounting.sql; do
  assert_file_contains "the database-runbook candidate $migration path" \
    "\$MININGCORE_CANDIDATE_DIR/migrations/$migration" "$database_document"
done
assert_file_contains 'the database-runbook source-checkout alternative' \
  '`src/Miningcore/Persistence/Postgres/Scripts/` directory' "$database_document"
if ! grep -Fq -- '-f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql' \
    <<<"$database_new_install_section"; then
  echo 'The new-installation runbook is missing the executable source-checkout schema import' >&2
  exit 1
fi
if grep -Eq '[[:space:]]-f[[:space:]]+src/Miningcore/Persistence/Postgres/Scripts/' \
    <<<"$database_upgrade_section" ||
    grep -Eq '[[:space:]]-f[[:space:]]+src/Miningcore/Persistence/Postgres/Scripts/' \
      "$pps_document" "$merged_mining_document"; then
  echo 'An existing-database guide has a repository-only executable migration path' >&2
  exit 1
fi
if grep -REq --include='*.md' \
    '/opt/miningcore/migrations/add_(auxpow_block_idempotency|payout_manager_ownership|share_accounting|bitcoin_direct_solo)\.sql' \
    "$readme" "$repository_root/docs"; then
  echo 'User documentation reads release migrations through the active symlink' >&2
  exit 1
fi
assert_file_contains 'the PPS canonical database-upgrade delegation' \
  '[database upgrade procedure](database.md#upgrade-an-existing-database)' "$pps_document"
assert_file_contains 'the PPS active-symlink prohibition' \
  'Do not run these upgrade migrations through the active `/opt/miningcore` symlink' "$pps_document"
assert_file_contains 'the merged-mining canonical database-upgrade delegation' \
  '[database upgrade procedure](database.md#upgrade-an-existing-database)' \
  "$merged_mining_document"
assert_file_contains 'the merged-mining active-symlink prohibition' \
  'Do not run release migrations through the active `/opt/miningcore` symlink' \
  "$merged_mining_document"
assert_file_contains 'the database-upgrade relay-recorder stop boundary' \
  'share-relay senders, receivers and recorders, recovery importers and payout managers on every node' \
  "$database_document"
assert_file_contains 'the release-upgrade numbered distributed-writer stop boundary' \
  '4. Stop every Miningcore writer using the database, including share-relay senders,' "$document"
assert_file_contains 'the release-upgrade relay-role inventory' \
  'share-relay senders, receivers and recorders, recovery importers and payout managers' "$document"
assert_file_contains 'the release-upgrade local-service scope warning' \
  'stops only the supplied local `miningcore.service`' "$document"
assert_file_contains 'the non-PPS schema-convergence clarification' \
  'canonical v0.2.0 upgrade still applies all three additive, idempotent migrations' "$document"
assert_file_contains 'the database-runbook transactional initial import' \
  '--single-transaction' "$database_document"
assert_file_contains 'the quick-start existing-partition explanation' \
  'already partitioned `shares` table is refused so its existing partition layout remains intact.' \
  "$readme"
assert_file_contains 'the PPS receiver-before-sender rule' \
  'Upgrade and migrate relay receivers/recorders before senders' "$pps_document"
assert_file_contains 'the authoritative PPS ledger boundary' \
  'are the financial record' "$pps_document"
assert_file_contains 'the PPS share-retention range' \
  'ppsShareRetentionDays` from 1 through 365' "$pps_document"
assert_file_contains 'the PPS accounting-retention range' \
  'shareAccountingRetentionDays` from 1 through 3650' "$pps_document"
assert_file_contains 'the PPS prune-batch range' \
  'shareAccountingPruneBatchSize` from 1000' "$pps_document"
assert_file_contains 'the PPS two-level payment-processing requirement' \
  'Both payment-processing switches shown above are mandatory' "$pps_document"
assert_file_contains 'the PPS runtime-toggle safety boundary' \
  'reject disabling an active PPS pool' "$pps_document"
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
  FNR == 1 { code = 0 }
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
assert_prose_contains 'the complete native-import library expression contract' \
  'Nonliteral library or entry-point expressions'
assert_prose_contains 'the verbatim native-import parameter contract' \
  'including C# verbatim identifiers'
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

assert_secure_token_writes() {
  local source=$1

  if ! awk '
    /^```/ {
      in_code = !in_code
      pending_install = 0
      secure_live = 0
      secure_new = 0
      next
    }
    !in_code { next }
    /sudo install -m 0600 -o root -g root \/dev\/null/ {
      pending_install = 1
      next
    }
    pending_install && /\/etc\/miningcore\/miningcore\.env\.new/ {
      secure_new = 1
      pending_install = 0
      next
    }
    pending_install && /\/etc\/miningcore\/miningcore\.env/ {
      secure_live = 1
      pending_install = 0
      next
    }
    /sudo tee \/etc\/miningcore\/miningcore\.env\.new/ && !secure_new {
      unsafe = 1
    }
    /sudo tee \/etc\/miningcore\/miningcore\.env([^.]|$)/ && !secure_live {
      unsafe = 1
    }
    END { exit unsafe ? 1 : 0 }
  ' "$source"; then
    echo "$source writes an administrative token before creating its root-only file" >&2
    exit 1
  fi
}

assert_secure_token_writes "$readme"
assert_secure_token_writes "$document"

for stale_version in v0.1.0-rc.9 v0.1.0-rc.10 v0.1.0-rc.11 v0.1.0-rc.12; do
  if grep -Fq "$stale_version" "$readme" "$document"; then
    echo "Release documentation still references stale version $stale_version" >&2
    exit 1
  fi
done

if grep -Fq 'v0.1.0-rc.13' "$readme"; then
  echo 'README still recommends the RC.13 candidate instead of the stable release' >&2
  exit 1
fi

if grep -Eq \
    '^(export )?(MININGCORE_VERSION|TAG|NEXT_VERSION)=v0\.1\.0-rc\.13([[:space:]]|$)' \
    "$document"; then
  echo 'Release guide still contains a copy-paste RC.13 assignment' >&2
  exit 1
fi

if grep -Eh \
    '^(export )?(MININGCORE_VERSION|TAG|NEXT_VERSION)=v[0-9]+\.[0-9]+\.[0-9]+' \
    "$readme" "$document" "$database_document" |
    grep -Ev \
      '^(export )?(MININGCORE_VERSION|TAG|NEXT_VERSION)=v0\.3\.0-rc\.1([[:space:]]|$)'; then
  echo 'README, release guide or database guide contains a stale copy-paste release assignment' >&2
  exit 1
fi

if [[ $(grep -Ec '^(export )?MININGCORE_VERSION=v0\.3\.0-rc\.1([[:space:]]|$)' "$readme") -ne 3 ]] ||
    [[ $(grep -Ec '^export MININGCORE_VERSION=v0\.3\.0-rc\.1([[:space:]]|$)' "$document") -ne 2 ]] ||
    [[ $(grep -Ec '^export MININGCORE_VERSION=v0\.3\.0-rc\.1([[:space:]]|$)' "$database_document") -ne 1 ]] ||
    [[ $(grep -Ec '^NEXT_VERSION=v0\.3\.0-rc\.1([[:space:]]|$)' "$document") -ne 1 ]] ||
    [[ $(grep -Ec '^export TAG=v0\.3\.0-rc\.1([[:space:]]|$)' "$document") -ne 1 ]]; then
  echo 'The v0.3.0-rc.1 copy-paste assignment inventory is incomplete or duplicated' >&2
  exit 1
fi

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
bash -n <<<"$readme_install_block"
bash -n <<<"$readme_database_block"
bash -n <<<"$release_database_block"
bash -n <<<"$partition_block"
bash -n <<<"$direct_solo_install_block"
bash -n <<<"$quickstart_placeholder_block"
bash -n <<<"$source_placeholder_block"
bash -n <<<"$upgrade_block"

if [[ "$readme_database_block" != "$release_database_block" ]]; then
  echo 'README and release-guide fresh-database procedures have drifted' >&2
  exit 1
fi

for required in \
  'MININGCORE_DATABASE_READY=' \
  "SELECT 1 FROM pg_roles WHERE rolname = 'miningcore'" \
  "SELECT 1 FROM pg_database WHERE datname = 'miningcore'" \
  'sudo -u postgres createuser --pwprompt miningcore &&' \
  'sudo -u postgres createdb --owner=miningcore miningcore &&' \
  'psql --single-transaction -v ON_ERROR_STOP=1' \
  'export MININGCORE_DATABASE_READY=1'; do
  if ! grep -Fq "$required" <<<"$readme_database_block"; then
    echo "Fresh-database block is missing: $required" >&2
    exit 1
  fi
done

for required in \
  'MININGCORE_UPGRADE_READY=' \
  'if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then' \
  'release_dir="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}"' \
  'stage_miningcore_candidate()' \
  'sudo tar -xzf "$archive" -C /opt' \
  'pg_restore --list "$upgrade_backup"' \
  '"$release_dir/migrations/add_auxpow_block_idempotency.sql"' \
  '"$release_dir/migrations/add_payout_manager_ownership.sql"' \
  '"$release_dir/migrations/add_share_accounting.sql"' \
  'sudo ln -sfnT "$release_dir" /opt/miningcore' \
  'export MININGCORE_UPGRADE_READY=1' \
  'STOP: upgrade failed; /opt/miningcore was not changed'; do
  if ! grep -Fq "$required" <<<"$upgrade_block"; then
    echo "Release upgrade block is missing: $required" >&2
    exit 1
  fi
done

if grep -Fq '/opt/miningcore/migrations/' <<<"$upgrade_block"; then
  echo 'Release upgrade block reads migrations through the old active symlink' >&2
  exit 1
fi

for required in \
  'MININGCORE_PARTITION_READY=' \
  'pg_dump -Fc -d miningcore' \
  'pg_restore --list "$partition_backup"' \
  'SELECT count(*) FROM public.shares;' \
  'SELECT count(*) FROM pg_partitioned_table WHERE partrelid =' \
  'if [ "$share_count" != 0 ]; then' \
  'elif [ "$partitioned_share_table_count" != 0 ]; then' \
  'shares is already partitioned; keep its current layout or use the full' \
  'createdb_postgresql_11_appendix.sql' \
  'export MININGCORE_PARTITION_READY=1'; do
  if ! grep -Fq "$required" <<<"$partition_block"; then
    echo "Quick-start partition block is missing: $required" >&2
    exit 1
  fi
done

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
  'test -d "$release_dir" || return' \
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

if ! grep -Fq 'test -d "$release_dir" || return' <<<"$readme_install_block"; then
  echo 'README installation directory guard does not propagate failure explicitly' >&2
  exit 1
fi

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

release_rc_heading_line=$(
  find_unique_line release-rc-heading '## v0.3.0-rc.1 highlights' "$release_document"
)
release_upgrade_heading_line=$(
  find_unique_line release-upgrade-heading '### Upgrade boundary from v0.2.1' "$release_document"
)
release_feature_heading_line=$(
  find_unique_line release-feature-heading '### Feature detail' "$release_document"
)
release_previous_heading_line=$(
  find_unique_line release-previous-heading '## v0.2.1 hotfix' "$release_document"
)
release_tag_checklist_line=$(
  find_unique_line release-tag-checklist 'Before tagging a new release,' "$release_document"
)
release_tag_preference_line=$(
  find_unique_line release-tag-preference 'failures before publication. Prefer a signed annotated tag:' \
    "$release_document"
)
release_tag_command_line=$(
  find_unique_line release-tag-command 'git switch dev' "$release_document"
)

if [[ "$release_upgrade_heading_line" -le "$release_rc_heading_line" ||
    "$release_feature_heading_line" -le "$release_upgrade_heading_line" ||
    "$release_previous_heading_line" -le "$release_feature_heading_line" ]]; then
  echo 'The v0.3.0 upgrade boundary and feature detail are not correctly nested before v0.2.1' >&2
  exit 1
fi

if [[ "$release_tag_preference_line" -le "$release_tag_checklist_line" ||
    "$release_tag_command_line" -le "$release_tag_preference_line" ]]; then
  echo 'The maintainer checklist interrupts the signed-tag command introduction' >&2
  exit 1
fi

source_editor_line=$(
  find_unique_line source-editor '${EDITOR:-vi} build/config.json' "$source_build_section"
)
source_check_line=$(
  find_unique_line source-placeholder-check \
    'source_placeholder_status=0' "$source_build_section"
)
source_launch_line=$(
  find_unique_line source-launch './Miningcore -c config.json' "$source_build_section"
)

if [[ "$source_check_line" -le "$source_editor_line" ||
    "$source_launch_line" -le "$source_check_line" ]]; then
  echo 'The source-build edit, fail-closed placeholder check and launch are not safely ordered' >&2
  exit 1
fi

quickstart_example_line=$(
  find_unique_line quickstart-example-selection \
    'direct_solo_source=/opt/miningcore/examples/bitcoin_direct_solo_pool.json' \
    "$quickstart_configuration_section"
)
quickstart_editor_line=$(
  find_unique_line quickstart-editor \
    'sudoedit /etc/miningcore/config.json' "$quickstart_configuration_section"
)
quickstart_edit_heading_line=$(
  find_unique_line quickstart-edit-heading \
    '#### Edit and validate the configuration' "$quickstart_configuration_section"
)
quickstart_check_line=$(
  find_unique_line quickstart-placeholder-check \
    'quickstart_placeholder_status=0' \
    "$quickstart_configuration_section"
)
quickstart_continue_line=$(
  find_unique_line quickstart-continue \
    '### 6. Install, secure and synchronize the coin daemons' \
    "$quickstart_configuration_section"
)

if [[ "$quickstart_edit_heading_line" -le "$quickstart_example_line" ||
    "$quickstart_editor_line" -le "$quickstart_edit_heading_line" ||
    "$quickstart_check_line" -le "$quickstart_editor_line" ||
    "$quickstart_continue_line" -le "$quickstart_check_line" ]]; then
  echo 'Quick-start example selection, editing, final validation and continuation are not safely ordered' >&2
  exit 1
fi

direct_source_guard_line=$(
  find_unique_line direct-source-guard 'sudo test -f "$direct_solo_source"' \
    "$direct_solo_install_block"
)
direct_backup_line=$(
  find_unique_line direct-backup 'sudo cp --preserve=mode,ownership,timestamps' \
    "$direct_solo_install_block"
)
direct_install_line=$(
  find_unique_line direct-install 'sudo install -m 0640 -o root -g miningcore' \
    "$direct_solo_install_block"
)
direct_ready_line=$(
  find_unique_line direct-ready 'READY: installed the direct-SOLO example' \
    "$direct_solo_install_block"
)
direct_stop_line=$(
  find_unique_line direct-stop 'STOP: direct-SOLO example installation failed' \
    "$direct_solo_install_block"
)
direct_failure_line=$(
  find_unique_line direct-failure '  false' "$direct_solo_install_block"
)

if [[ "$direct_backup_line" -le "$direct_source_guard_line" ||
    "$direct_install_line" -le "$direct_backup_line" ||
    "$direct_ready_line" -le "$direct_install_line" ||
    "$direct_failure_line" -le "$direct_stop_line" ]]; then
  echo 'Direct-SOLO validation, backup, install, readiness and failure are not safely ordered' >&2
  exit 1
fi

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

upgrade_extract_line=$(
  find_unique_line upgrade-extraction 'sudo tar -xzf "$archive" -C /opt' "$upgrade_block"
)
upgrade_stop_line=$(
  find_unique_line upgrade-stop 'if sudo systemctl stop miningcore &&' "$upgrade_block"
)
upgrade_backup_line=$(
  find_unique_line upgrade-backup 'sudo -u postgres pg_dump -Fc -d miningcore' "$upgrade_block"
)
upgrade_restore_check_line=$(
  find_unique_line upgrade-backup-check 'pg_restore --list "$upgrade_backup"' "$upgrade_block"
)
upgrade_auxpow_line=$(
  find_unique_line upgrade-auxpow-migration \
    '"$release_dir/migrations/add_auxpow_block_idempotency.sql"' "$upgrade_block"
)
upgrade_ownership_line=$(
  find_unique_line upgrade-ownership-migration \
    '"$release_dir/migrations/add_payout_manager_ownership.sql"' "$upgrade_block"
)
upgrade_accounting_line=$(
  find_unique_line upgrade-accounting-migration \
    '"$release_dir/migrations/add_share_accounting.sql"' "$upgrade_block"
)
upgrade_symlink_line=$(
  find_unique_line upgrade-stable-symlink \
    'sudo ln -sfnT "$release_dir" /opt/miningcore' "$upgrade_block"
)

if [[ "$upgrade_stop_line" -le "$upgrade_extract_line" ||
    "$upgrade_backup_line" -le "$upgrade_stop_line" ||
    "$upgrade_restore_check_line" -le "$upgrade_backup_line" ||
    "$upgrade_auxpow_line" -le "$upgrade_restore_check_line" ||
    "$upgrade_ownership_line" -le "$upgrade_auxpow_line" ||
    "$upgrade_accounting_line" -le "$upgrade_ownership_line" ||
    "$upgrade_symlink_line" -le "$upgrade_accounting_line" ]]; then
  echo 'Candidate staging, backup, migrations and stable-symlink activation are not safely ordered' >&2
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

# Execute the documented blocks against hostile command fixtures. These checks
# model an ordinary interactive shell, where a failed command does not imply a
# global errexit policy.
fixture_dir=$(mktemp -d "${TMPDIR:-/tmp}/miningcore-install-docs.XXXXXXXX")
mkdir -p "$fixture_dir/bin" "$fixture_dir/download" "$fixture_dir/home"
trace="$fixture_dir/trace"

source_placeholder_root="$fixture_dir/source-placeholder"
mkdir -p "$source_placeholder_root/build"

awk '
  /^[[:space:]]*\/\// { print; next }
  { gsub(/(CHANGE_ME|REPLACE_WITH_)[A-Z0-9_]*/, "configured"); print }
' "$config_example" > "$source_placeholder_root/build/config.json"
printf '%s\n' '// REPLACE_WITH_COMMENT_ONLY_DOES_NOT_BLOCK' \
  >> "$source_placeholder_root/build/config.json"
if ! real_config_output=$(
  cd "$source_placeholder_root" && bash -c "$source_placeholder_block" 2>&1
); then
  echo 'The source placeholder check rejected the edited real config.example.json' >&2
  exit 1
fi
if ! grep -Fq 'READY: no active placeholders remain' <<<"$real_config_output" ||
    grep -Fq 'STOP:' <<<"$real_config_output"; then
  echo 'The edited real config.example.json did not reach the documented READY state' >&2
  exit 1
fi

printf '%s\n' '{ "configured": true }' > "$source_placeholder_root/build/config.json"
if ! clean_placeholder_output=$(
  cd "$source_placeholder_root" && bash -c "$source_placeholder_block" 2>&1
); then
  echo 'The source placeholder check rejected a readable configuration without placeholders' >&2
  exit 1
fi
if ! grep -Fq 'READY: no active placeholders remain' <<<"$clean_placeholder_output" ||
    grep -Fq 'STOP:' <<<"$clean_placeholder_output"; then
  echo 'The source placeholder check did not report its clean success state exactly' >&2
  exit 1
fi

printf '%s\n' '{ "password": "CHANGE_ME" }' > "$source_placeholder_root/build/config.json"
if matched_placeholder_output=$(
  cd "$source_placeholder_root" && bash -c "$source_placeholder_block" 2>&1
); then
  echo 'The source placeholder check accepted an unresolved placeholder' >&2
  exit 1
fi
if ! grep -Fq 'STOP: replace every active placeholder' <<<"$matched_placeholder_output" ||
    grep -Fq 'READY:' <<<"$matched_placeholder_output"; then
  echo 'The source placeholder check did not fail closed on a placeholder match' >&2
  exit 1
fi

rm -f -- "$source_placeholder_root/build/config.json"
if failed_placeholder_output=$(
  cd "$source_placeholder_root" && bash -c "$source_placeholder_block" 2>&1
); then
  echo 'The source placeholder check accepted a failed configuration inspection' >&2
  exit 1
fi
if ! grep -Fq 'STOP: could not inspect build/config.json' <<<"$failed_placeholder_output" ||
    grep -Fq 'READY:' <<<"$failed_placeholder_output"; then
  echo 'The source placeholder check did not distinguish inspection failure from no matches' >&2
  exit 1
fi

mapfile -t optional_placeholder_lines < <(
  awk '
    /^[[:space:]]*\/\// && /(CHANGE_ME|REPLACE_WITH_)/ {
      sub(/^[[:space:]]*\/\/[[:space:]]?/, "")
      print
    }
  ' "$config_example"
)
if [[ ${#optional_placeholder_lines[@]} -ne 4 ]]; then
  echo 'The maintained config does not contain the four reviewed optional secret placeholders' >&2
  exit 1
fi

for index in "${!optional_placeholder_lines[@]}"; do
  printf '%s\n' "${optional_placeholder_lines[$index]}" \
    > "$source_placeholder_root/build/config.json"
  if optional_source_output=$(
    cd "$source_placeholder_root" && bash -c "$source_placeholder_block" 2>&1
  ); then
    echo "The source gate accepted uncommented optional placeholder $index" >&2
    exit 1
  fi
  if ! grep -Fq 'STOP: replace every active placeholder' <<<"$optional_source_output" ||
      grep -Fq 'READY:' <<<"$optional_source_output"; then
    echo "The source gate did not reject optional placeholder $index exactly" >&2
    exit 1
  fi
done

quick_config="$fixture_dir/quick-config.json"
awk '
  /^[[:space:]]*\/\// { print; next }
  { gsub(/(CHANGE_ME|REPLACE_WITH_)[A-Z0-9_]*/, "configured"); print }
' "$config_example" > "$quick_config"
printf '%s\n' '// REPLACE_WITH_COMMENT_ONLY_DOES_NOT_BLOCK' >> "$quick_config"

mkdir -p "$fixture_dir/direct-bin"
cat > "$fixture_dir/direct-bin/sudo" <<'EOF'
#!/usr/bin/env bash
case "${1:-}" in
  -v)
    exit 0
    ;;
  test)
    if [[ "${2:-}" == -f && "${3:-}" == /opt/miningcore/examples/bitcoin_direct_solo_pool.json ]]; then
      exit "${DOC_TEST_DIRECT_SOURCE_STATUS:-0}"
    fi
    exit 0
    ;;
  mktemp)
    printf '%s\n' '/etc/miningcore/config.json.before-direct-solo.TESTBACKUP'
    exit 0
    ;;
  cp|install)
    exit 0
    ;;
esac
echo "Unexpected direct-SOLO sudo invocation: $*" >&2
exit 64
EOF
chmod +x "$fixture_dir/direct-bin/sudo"

if ! direct_success_output=$(
  env PATH="$fixture_dir/direct-bin:$PATH" bash -c "$direct_solo_install_block" 2>&1
); then
  echo 'The direct-SOLO install block rejected its simulated protected success path' >&2
  exit 1
fi
if ! grep -Fq 'READY: installed the direct-SOLO example' <<<"$direct_success_output" ||
    grep -Fq 'STOP:' <<<"$direct_success_output"; then
  echo 'The direct-SOLO install block did not report its success state exactly' >&2
  exit 1
fi

if direct_failure_output=$(
  env PATH="$fixture_dir/direct-bin:$PATH" DOC_TEST_DIRECT_SOURCE_STATUS=1 \
    bash -c "$direct_solo_install_block" 2>&1
); then
  echo 'The direct-SOLO install block returned success after a source validation failure' >&2
  exit 1
fi
if ! grep -Fq 'STOP: direct-SOLO example installation failed' <<<"$direct_failure_output" ||
    grep -Fq 'READY:' <<<"$direct_failure_output"; then
  echo 'The direct-SOLO install block did not fail closed exactly' >&2
  exit 1
fi

cat > "$fixture_dir/bin/sudo" <<'EOF'
#!/usr/bin/env bash
case "${1:-}" in
  -v)
    exit "${DOC_TEST_SUDO_VALIDATE_STATUS:-0}"
    ;;
  test)
    exit "${DOC_TEST_SUDO_TEST_STATUS:-0}"
    ;;
  awk)
    shift
    program=${1:?}
    exec /usr/bin/awk "$program" "${DOC_TEST_QUICK_CONFIG:?}"
    ;;
esac
echo "Unexpected quick-start sudo invocation: $*" >&2
exit 64
EOF
chmod +x "$fixture_dir/bin/sudo"

if ! quick_clean_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_QUICK_CONFIG="$quick_config" \
    bash -c "$quickstart_placeholder_block" 2>&1
); then
  echo 'The quick-start gate rejected the edited real config.example.json' >&2
  exit 1
fi
if ! grep -Fq 'READY: no active placeholders remain' <<<"$quick_clean_output" ||
    grep -Fq 'STOP:' <<<"$quick_clean_output"; then
  echo 'The quick-start gate did not report its real-config success state exactly' >&2
  exit 1
fi

printf '%s\n' '{ "password": "CHANGE_ME_ACTIVE" }' > "$quick_config"
if quick_match_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_QUICK_CONFIG="$quick_config" \
    bash -c "$quickstart_placeholder_block" 2>&1
); then
  echo 'The quick-start gate accepted an unresolved active placeholder' >&2
  exit 1
fi
if ! grep -Fq 'STOP: replace every active placeholder' <<<"$quick_match_output" ||
    grep -Fq 'READY:' <<<"$quick_match_output"; then
  echo 'The quick-start gate did not fail closed on an active placeholder' >&2
  exit 1
fi

for index in "${!optional_placeholder_lines[@]}"; do
  printf '%s\n' "${optional_placeholder_lines[$index]}" > "$quick_config"
  if optional_quick_output=$(
    env PATH="$fixture_dir/bin:$PATH" DOC_TEST_QUICK_CONFIG="$quick_config" \
      bash -c "$quickstart_placeholder_block" 2>&1
  ); then
    echo "The quick-start gate accepted uncommented optional placeholder $index" >&2
    exit 1
  fi
  if ! grep -Fq 'STOP: replace every active placeholder' <<<"$optional_quick_output" ||
      grep -Fq 'READY:' <<<"$optional_quick_output"; then
    echo "The quick-start gate did not reject optional placeholder $index exactly" >&2
    exit 1
  fi
done

if quick_sudo_failure_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_QUICK_CONFIG="$quick_config" \
    DOC_TEST_SUDO_VALIDATE_STATUS=1 bash -c "$quickstart_placeholder_block" 2>&1
); then
  echo 'The quick-start gate accepted failed sudo authorization' >&2
  exit 1
fi
if ! grep -Fq 'STOP: could not inspect /etc/miningcore/config.json' \
    <<<"$quick_sudo_failure_output" || grep -Fq 'READY:' <<<"$quick_sudo_failure_output"; then
  echo 'The quick-start gate confused sudo failure with a clean configuration' >&2
  exit 1
fi

if quick_read_failure_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_QUICK_CONFIG="$quick_config" \
    DOC_TEST_SUDO_TEST_STATUS=1 bash -c "$quickstart_placeholder_block" 2>&1
); then
  echo 'The quick-start gate accepted an unreadable or unsafe configuration object' >&2
  exit 1
fi
if ! grep -Fq 'STOP: could not inspect /etc/miningcore/config.json' \
    <<<"$quick_read_failure_output" || grep -Fq 'READY:' <<<"$quick_read_failure_output"; then
  echo 'The quick-start gate confused failed file preflight with a clean configuration' >&2
  exit 1
fi

cat > "$fixture_dir/bin/id" <<'EOF'
#!/usr/bin/env bash
exit 0
EOF
cat > "$fixture_dir/bin/sudo" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${DOC_TEST_TRACE:?}"
exit 0
EOF
chmod +x "$fixture_dir/bin/id" "$fixture_dir/bin/sudo"

: > "$trace"
failed_release_install=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_TRACE="$trace" \
    MININGCORE_RELEASE_READY=1 MININGCORE_VERSION=v-doc-test-missing \
    MININGCORE_UBUNTU=26.04 MININGCORE_DOWNLOAD_DIR="$fixture_dir/download" \
    bash -c "$install_block" 2>&1
)
if ! grep -Fq 'installation failed; /opt/miningcore was not changed' \
    <<<"$failed_release_install" || grep -Fq 'ln -sfnT' "$trace"; then
  echo 'Release-guide installation can activate a missing extraction' >&2
  exit 1
fi

: > "$trace"
failed_readme_install=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_TRACE="$trace" \
    MININGCORE_QUICKSTART_READY=1 MININGCORE_VERSION=v-doc-test-missing \
    MININGCORE_UBUNTU=26.04 download_dir="$fixture_dir/download" \
    archive_name=miningcore-missing.tar.gz bash -c "$readme_install_block" 2>&1
)
if ! grep -Fq 'installation failed; /opt/miningcore was not changed' \
    <<<"$failed_readme_install" || grep -Fq 'ln -sfnT' "$trace"; then
  echo 'README installation can activate a missing extraction' >&2
  exit 1
fi

cat > "$fixture_dir/bin/sudo" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${DOC_TEST_TRACE:?}"
case "$*" in
  *"SELECT 1 FROM pg_roles"*)
    printf '%s\n' "${DOC_TEST_ROLE_EXISTS:-}"
    ;;
  *"SELECT 1 FROM pg_database"*)
    printf '%s\n' "${DOC_TEST_DATABASE_EXISTS:-}"
    ;;
  *createuser*)
    exit "${DOC_TEST_CREATEUSER_STATUS:-0}"
    ;;
  *createdb.sql*)
    exit "${DOC_TEST_SCHEMA_STATUS:-0}"
    ;;
  *createdb*)
    exit "${DOC_TEST_CREATEDB_STATUS:-0}"
    ;;
esac
exit 0
EOF
cat > "$fixture_dir/bin/psql" <<'EOF'
#!/usr/bin/env bash
printf 'verification %s\n' "$*" >> "${DOC_TEST_TRACE:?}"
exit "${DOC_TEST_VERIFY_STATUS:-0}"
EOF
chmod +x "$fixture_dir/bin/sudo" "$fixture_dir/bin/psql"

: > "$trace"
existing_database_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_TRACE="$trace" \
    DOC_TEST_ROLE_EXISTS=1 DOC_TEST_DATABASE_EXISTS= \
    bash -c "$readme_database_block" 2>&1
)
if ! grep -Fq 'role or database already exists' <<<"$existing_database_output" ||
    grep -Eq 'createuser|createdb.sql|verification ' "$trace"; then
  echo 'Fresh-database block modifies or verifies a pre-existing deployment' >&2
  exit 1
fi

: > "$trace"
failed_database_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_TRACE="$trace" \
    DOC_TEST_ROLE_EXISTS= DOC_TEST_DATABASE_EXISTS= DOC_TEST_CREATEDB_STATUS=1 \
    bash -c "$readme_database_block" 2>&1
)
if ! grep -Fq 'database provisioning failed' <<<"$failed_database_output" ||
    grep -Eq 'createdb.sql|verification ' "$trace"; then
  echo 'Fresh-database block imports the schema after failed object creation' >&2
  exit 1
fi

: > "$trace"
successful_database_output=$(
  env PATH="$fixture_dir/bin:$PATH" DOC_TEST_TRACE="$trace" \
    DOC_TEST_ROLE_EXISTS= DOC_TEST_DATABASE_EXISTS= \
    bash -c "$readme_database_block" 2>&1
)
if ! grep -Fq 'READY: created and verified the miningcore database' \
    <<<"$successful_database_output" ||
    ! grep -Fq -- '--single-transaction' "$trace" ||
    ! grep -Fq 'verification ' "$trace"; then
  echo 'Fresh-database block did not complete its guarded success path' >&2
  exit 1
fi

cat > "$fixture_dir/bin/sudo" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${DOC_TEST_TRACE:?}"
case "$*" in
  *pg_dump*)
    exit "${DOC_TEST_DUMP_STATUS:-0}"
    ;;
  *createdb_postgresql_11_appendix.sql*)
    exit "${DOC_TEST_APPENDIX_STATUS:-0}"
    ;;
  *"SELECT count(*) FROM pg_partitioned_table"*)
    printf '%s\n' "${DOC_TEST_PARTITIONED_SHARE_TABLE_COUNT:-0}"
    ;;
  *"SELECT count(*) FROM public.shares;"*)
    printf '%s\n' "${DOC_TEST_SHARE_COUNT:-0}"
    ;;
esac
exit 0
EOF
cat > "$fixture_dir/bin/pg_restore" <<'EOF'
#!/usr/bin/env bash
printf 'pg_restore %s\n' "$*" >> "${DOC_TEST_TRACE:?}"
exit "${DOC_TEST_RESTORE_STATUS:-0}"
EOF
chmod +x "$fixture_dir/bin/sudo" "$fixture_dir/bin/pg_restore"

: > "$trace"
failed_backup_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_DUMP_STATUS=1 bash -c "$partition_block" 2>&1
)
if ! grep -Fq 'appendix not run' <<<"$failed_backup_output" ||
    grep -Fq 'createdb_postgresql_11_appendix.sql' "$trace"; then
  echo 'Partition appendix ran after a failed backup' >&2
  exit 1
fi

: > "$trace"
nonempty_shares_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_SHARE_COUNT=1 bash -c "$partition_block" 2>&1
)
if ! grep -Fq 'shares is not empty' <<<"$nonempty_shares_output" ||
    grep -Fq 'createdb_postgresql_11_appendix.sql' "$trace"; then
  echo 'Partition appendix ran against a non-empty shares table' >&2
  exit 1
fi

: > "$trace"
partitioned_shares_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_SHARE_COUNT=0 \
    DOC_TEST_PARTITIONED_SHARE_TABLE_COUNT=1 bash -c "$partition_block" 2>&1
)
partitioned_stop='shares is already partitioned; keep its current layout or use the full'
partitioned_stop+=' partition migration runbook'
if ! grep -Fq "$partitioned_stop" <<<"$partitioned_shares_output" ||
    grep -Fq 'createdb_postgresql_11_appendix.sql' "$trace"; then
  echo 'Partition appendix discarded an existing empty partition layout' >&2
  exit 1
fi

: > "$trace"
successful_partition_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_SHARE_COUNT=0 bash -c "$partition_block" 2>&1
)
if ! grep -Fq 'READY: rebuilt the empty shares table' \
    <<<"$successful_partition_output" ||
    ! grep -Fq 'createdb_postgresql_11_appendix.sql' "$trace"; then
  echo 'Partition block did not complete its guarded success path' >&2
  exit 1
fi

# Exercise the routine-upgrade block with its versioned directory redirected
# into the private fixture. The production path assignment is asserted above;
# only the filesystem root changes here so an unprivileged test can create the
# candidate binary and migration inventory.
candidate_dir="$fixture_dir/candidate-release"
# This is a multiline documented command block, not a simple variable substitution.
# shellcheck disable=SC2001
upgrade_fixture_block=$(
  sed 's#^  release_dir=.*#  release_dir="${DOC_TEST_RELEASE_DIR}"#' \
    <<<"$upgrade_block"
)
cat > "$fixture_dir/bin/sudo" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "${DOC_TEST_TRACE:?}"
case "$*" in
  test\ -e*)
    exit 1
    ;;
  tar\ -xzf*)
    mkdir -p "${DOC_TEST_RELEASE_DIR:?}/migrations"
    printf '%s\n' 'fixture build identity' > "$DOC_TEST_RELEASE_DIR/BUILD-INFO"
    printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$DOC_TEST_RELEASE_DIR/Miningcore"
    chmod +x "$DOC_TEST_RELEASE_DIR/Miningcore"
    ;;
  *pg_dump*)
    printf '%s\n' 'fixture database backup'
    ;;
  *add_share_accounting.sql*)
    exit "${DOC_TEST_SHARE_MIGRATION_STATUS:-0}"
    ;;
esac
exit 0
EOF
chmod +x "$fixture_dir/bin/sudo"

: > "$trace"
failed_upgrade_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_RELEASE_DIR="$candidate_dir" \
    DOC_TEST_SHARE_MIGRATION_STATUS=1 MININGCORE_RELEASE_READY=1 \
    MININGCORE_VERSION=v-doc-upgrade MININGCORE_UBUNTU=26.04 \
    archive="$fixture_dir/download/candidate.tar.gz" \
    bash -c "$upgrade_fixture_block" 2>&1
)
if ! grep -Fq 'upgrade failed; /opt/miningcore was not changed' \
    <<<"$failed_upgrade_output" || grep -Fq 'ln -sfnT' "$trace" ||
    [[ $(grep -Fc "$candidate_dir/migrations/" "$trace") -ne 3 ]]; then
  echo 'Release upgrade can activate a candidate after a migration failure or use stale SQL' >&2
  exit 1
fi

rm -rf -- "$candidate_dir"
: > "$trace"
successful_upgrade_output=$(
  env PATH="$fixture_dir/bin:$PATH" HOME="$fixture_dir/home" \
    DOC_TEST_TRACE="$trace" DOC_TEST_RELEASE_DIR="$candidate_dir" \
    DOC_TEST_SHARE_MIGRATION_STATUS=0 MININGCORE_RELEASE_READY=1 \
    MININGCORE_VERSION=v-doc-upgrade MININGCORE_UBUNTU=26.04 \
    archive="$fixture_dir/download/candidate.tar.gz" \
    bash -c "$upgrade_fixture_block" 2>&1
)
if ! grep -Fq "READY: migrated the database and activated $candidate_dir" \
    <<<"$successful_upgrade_output" ||
    ! grep -Fq "ln -sfnT $candidate_dir /opt/miningcore" "$trace"; then
  echo 'Release upgrade did not activate the verified candidate after all migrations succeeded' >&2
  exit 1
fi

echo "Release installation documentation invariants passed"
