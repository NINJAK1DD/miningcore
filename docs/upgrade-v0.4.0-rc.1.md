# Upgrade from v0.3.0 to v0.4.0-rc.1

This guide covers the first v0.4.0 release candidate. It is a staging candidate,
not the v0.4.0 stable release. Confirm that its release assets have been published
before using download commands; preparing a release branch does not publish them.
Use a clean checkout of the selected tag or the verified archive for the exact
host. Record the source commit and retain the previous installation.

The changes include stricter notification, TLS, database and miner-input validation,
connection quotas, corrected candidate credit and new opt-in mining families.
An old configuration that started successfully can now fail validation or refuse
connections. Do not replace a working configuration with an example.

Operators skipping v0.3.0 must first satisfy the [v0.3.0 upgrade prerequisites](releases.md#v030-highlights),
including applicable direct-SOLO and payout-ownership migrations. This guide does
not replace the earlier cumulative upgrade boundaries.

## Database migration matrix

Stop every writer and payout manager using the database and take a proven backup
before applying SQL. Run scripts from the immutable candidate, not the active
release symlink. Never run `createdb.sql` over an existing database.

| Existing deployment or intended change | v0.4.0-rc.1 action |
| --- | --- |
| Canonical BTC SOLO, retaining its existing direct-coinbase or custodial mode | No new SQL solely for this upgrade when the v0.3.0 prerequisites are installed. Preserve `soloCoinbasePayout` and existing settlement evidence. |
| LTC/DOGE merged SOLO/SOLO, retaining both payout modes | No new SQL solely for this upgrade when the v0.3.0 prerequisites are installed. Keep both pool IDs, auxiliary address policy and existing claim/payment evidence. |
| Existing PPS, or merged-mining parent/aux accounting on PROP/PPLNS/PPLNSBF even without PPS | Upgrade the accounting schema. An otherwise current v0.3.0 accounting database needs `add_pps_arithmetic_version.sql`, even when retaining version 0. |
| Adding PPS or pooled merged accounting to a database without those prerequisites | Follow the ordered [database upgrade](database.md#upgrade-an-existing-database); the candidate's cumulative `add_share_accounting.sql` includes the arithmetic migration. |
| Opting into PPS arithmetic version 1 | Upgrade every producer/relay/recorder first, then follow the separate [one-way cutoff procedure](pps-arithmetic-migration.md). Schema installation alone does not activate it. |
| Enabling Pearl | Install the required accounting prerequisites and `add_pearl_candidates.sql`, plus the reviewed worker bundle and node/wallet contracts in the [Pearl guide](pearl-pool-guide.md). |
| Other unchanged supported families and payout modes, including plain non-merged PROP/PPLNS/PPLNSBF | No general new SQL migration. Review the family-specific configuration and settlement changes below. |

The existing AuxPoW idempotency, payout ownership and Bitcoin direct-SOLO migration
files are unchanged from v0.3.0. Their original requirements still apply; this does
not waive an incomplete earlier installation. Startup checks the applicable
contracts before mining. Do not drop/recreate receipt tables or clear ownership,
quarantine or uncertain-payment state to make startup succeed.

The arithmetic migration adds version-0 metadata without repricing historical
credits, balances, receipt hashes or remainders. Run it as a separate administrator.
Keep the transition table/functions administrator-owned and the application
SELECT-only on that boundary; grant additional writer roles the documented SELECT
access, not activation or mutation privileges. Use the
[ownership and privilege procedure](pps-arithmetic-migration.md) if the database,
table and runtime roles differ. Unchanged SOLO/SOLO pools do not require creating
these tables just to replace their application.

The startup schema error uses generic `add_share_accounting.sql` advice. For an
otherwise complete v0.3.0 accounting schema, use the standalone arithmetic
migration selected above, not an unnecessary cumulative reinstallation (which
also rebuilds the pruning index). A failed schema preflight can also indicate
incorrect privileges or defaults; diagnose the failure rather than rerunning SQL
blindly or granting the application administrative privileges.

## Configuration checks

### Notifications and SMTP

`notifications.admin.enabled` is the authoritative activation switch. The old
`notifications.enabled` is ignored. With admin alerts enabled, select at least
one usable transport: a plain email recipient with valid SMTP settings, enabled
Pushover with nonblank user/token, or both. Blank recipients select no email.
Invalid active settings can stop normal startup.

For email, set `notifications.email.tlsMode` explicitly:

| SMTP endpoint | Setting |
| --- | --- |
| Port 25/587 with required STARTTLS | `"startTls"` |
| Port 465 with TLS from connection start | `"implicitTls"` |
| Unauthenticated same-host relay using a numeric loopback address | `"localPlaintext"`, with no SMTP credentials |

TLS modes require a trusted, valid certificate matching the configured SMTP host;
there is no opportunistic plaintext fallback. Both credentials must be present,
or both absent. Sender/recipient fields must be plain addresses and the selected
email provider needs a nonblank sender name. Review
[SMTP migration](configuration.md#smtp-transport-migration) and
[notification delivery limits](notifications.md). A provider accepting an alert
does not prove human receipt; queue overflow and uncertainty remain visible in logs.

For an existing authenticated port-587 configuration, add `"tlsMode": "startTls"`
inside `notifications.email` and keep the actual SMTP username and password in
the protected local configuration. A nonempty `user` with `password: ""` is
invalid; do not confuse a password redacted for support with a usable deployed
configuration. Existing `admin.enabled: true`, `emailAddress`, `notifyBlockFound`
and `notifyPaymentSuccess` settings can remain. Disabled Pushover does not need
real credentials. Validate actual delivery in staging; configuration validation
alone cannot establish SMTP reachability, authentication or receipt.

### Database and daemon transport

- PostgreSQL `commandTimeout` must be an integer from 1 through 86400.
  Omitted/null retains 300 seconds; zero/unlimited, strings and fractions are rejected.
- Existing legacy PostgreSQL TLS behavior is retained. When selecting `sslMode`,
  remove both `tls` and `tlsNoValidate`, even when false. Client certificates,
  CA paths and trust sources must satisfy the [PostgreSQL policy](postgres-tls.md).
  An application upgrade does not itself require a PostgreSQL major-version upgrade.
- Daemon HTTPS/HTTP2/WSS certificates are now verified. Provision a private CA into
  the service/container trust store or renew the certificate as needed. Use final
  endpoint URLs: daemon HTTP redirects are refused. Protected direct HTTP endpoints
  are not automatically converted to HTTPS.
- Beam explorer, Conceal and Warthog REST now honor `ssl`/`http2`; old settings
  previously ignored by REST may require an actual TLS/HTTP2 proxy before restart.
  Check their [REST migration](rpc-tls.md#beam-conceal-and-warthog-rest-endpoints).

### Stratum, nginx and miner compatibility

Admission defaults now apply to existing configurations, across all ports of each
pool: 4096 concurrent dispatches, a pool burst of 200 and 100 starts/second;
per address, 256 concurrent dispatches, a burst of 32 and two starts/second.
TLS/PROXY/first-request setup has a ten-second deadline. IPv6 quotas group by
`/64`; `connectionAdmission.ipv6PrefixLength: 128` restores exact-address quota
grouping. These are resource quotas, not changes to miner identity or bans.

Measure NAT, hosting-provider and reconnect-burst demand before raising limits.
The tracked-address capacity must also cover
`maxConcurrentConnections + burst + connectionsPerSecond * idleExpirySeconds`.
Zero does not disable admission. See [admission sizing](stratum-connection-admission.md).

If nginx proxies only the website/API, it does not provide Stratum client identity.
If it also proxies mining TCP, verify the actual trusted-peer allowlist and PROXY
v1 path so the fleet is not unintentionally grouped under nginx's address.
Miningcore accepts strict CRLF-terminated PROXY v1 headers, not v2. If Miningcore
handles TLS, its PROXY line is inside that TLS stream; a cleartext PROXY preface
before its handshake is unsupported. Preserve the reviewed termination order.
Forwarded HTTP headers cannot supply Stratum identity. Keep private backend
listeners reachable only from the intended proxies.

Reconnect miners during cutover; do not carry old in-memory jobs across binaries.
Every configured `varDiff` block must explicitly set a finite positive `minDiff`;
omission becomes zero and now fails startup. A specified `maxDiff` must be finite,
positive and at least `minDiff`. `targetTime` and `retargetTime` must be finite
and positive; a specified `maxDelta` must be finite and nonnegative. Omitting
`maxDiff` remains supported, and zero/omitted `maxDelta` disables delta limiting.
Choose family-appropriate bounds before restarting; do not copy another coin's
difficulty values.

Submissions now require advertised extranonce widths and valid hex fields.
Duplicate subscribe preserves work once with an error; repeating it closes the
session. Minimum-difficulty negotiation now retains a floor, and rapid repeated
difficulty requests are bounded. Test actual firmware/proxies and monitor low
difficulty, wrong-width, duplicate and timeout counters. Use the
[issued-work guide](difficulty-assignment-grace.md) and
[Bitcoin minimum-difficulty policy](bitcoin-minimum-difficulty.md).

### Monitoring and log consumers

Update dashboards, alerts and log parsers before relying on cutover monitoring:

- RPC batch-method labels now use `batch`; unknown/custom RPC method labels use
  `other`, rather than raw or joined method names. Unknown Stratum request methods
  also use `other`; recognised methods retain their bounded labels. See the
  [RPC transport changes](releases.md#v040-rc1-credential-safe-rpc-transport-diagnostics)
  and [Stratum diagnostic contract](stratum-diagnostics.md).
- Ban log parsers must match `Disconnecting banned address @` instead of
  `Disconnecting banned client @`; review the [ban-attribution policy](stratum-ban-attribution.md).
- PostgreSQL startup filters must match `Using PostgreSQL persistence ` instead
  of `Using postgres connection string:`. Do not expect endpoint or credential
  values in the new diagnostic. See [PostgreSQL diagnostics](releases.md#v040-rc1-postgresql-credential-safe-diagnostics).
- `--dumpconfig` emits diagnostic format version 2. Consumers must check
  `diagnosticFormatVersion`; the allowlisted, lossy output is not a runnable
  configuration or backup. Review [the dump contract](configuration.md#safe-configuration-dumps).

Exercise representative logs/metrics against updated consumers in staging. An
empty dashboard or silent alert rule is not evidence of a healthy pool.

## Family and payout boundaries

- BTC, LTC and DOGE retain their existing chain identities and SOLO modes.
  Keep pool IDs, wallet addresses, coin keys and direct/custodial choices unchanged.
  The new BTCB2B maturity rules do not apply to ordinary SHA256d BTC, LTC or DOGE.
- PROP, PPLNS and PPLNSBF now stop settlement on invalid stored/adjusted/network
  difficulty. Run the [read-only historical audit](bitcoin-minimum-difficulty.md#pre-upgrade-settlement-audit--all-coin-families)
  before affected pooled payouts. Missing/pruned receipt evidence means incomplete
  audit coverage, not a clean ledger. Do not reprice or delete old shares automatically.
  SOLO does not use these shared weighted-settlement calculations.
- Candidate credit is now bounded by immutable qualifying chain targets.
  Automatic assignments can fall with network ceilings; explicit device/configured
  floors remain enforced and may warn. Existing receipts/liabilities are retained.
  See [family normalization and historical review](non-bitcoin-candidate-credit.md).
- Conceal/CryptoNote/Zano require difficulty at least one; Conceal/CryptoNote short
  targets also require whole-number ranges below their reviewed ceiling and
  compatible MaxDelta. Xelis/Zano require `shareMultiplier: 1`. Review
  [VarDiff migration](vardiff-monotonic.md) for exact family bounds.
- Xelis supports canonical v1/v2, not current-mainnet v3 certification.
  Unsupported work suspends mining and releases miners without IP bans.
  Extensions must adopt the [changed Xelis APIs](xelis-target-validation.md#xelis-extension-api-changes).
- BTCB2B uses the separate `bitcoin-blake2b` family and reviewed Knots 29.4.3.
  Historical/rolling/post-cutoff wallet thresholds differ; expiry is not a blanket
  unlock. Follow the [node upgrade/revalidation runbook](bitcoin-blake2b-knots-29.4.3-review.md).
  DATUM pool/server implementation remains outside this candidate.
- Pearl is opt-in and requires the separately built worker bundle. Its certified
  lane is Ubuntu 26.04 glibc x64; ordinary archives without that bundle cannot start
  a Pearl pool. It is not an Ubuntu 22.04 certification claim.
- Previous Warthog wallet-key exposure and historical credential-bearing debug
  output need the incident review linked from [release notes](releases.md).
  Upgrading does not remove copies of old logs or rotate credentials.

## Staging and cutover

1. Select the immutable candidate and record its commit. On Ubuntu 22.04, use the
   22.04 archive or a clean 22.04 source build; never copy 26.04 native libraries
   onto it. .NET 10 remains the framework. Linux native builds require x86-64-v2,
   AES-NI and PCLMULQDQ. Build helpers reject inherited compiler/Make overrides.
   Source operators must build the complete native/managed output; archive
   operators replace it with a complete verified host-matching archive. A
   managed-DLL-only replacement is insufficient.
   Build scripts install packages, so rehearse on a separate matching host before
   running them during production maintenance.
2. Preserve the original config, service environment, custom coin files, certificates,
   recovery journals/state and wallet backups. Keep a verified PostgreSQL backup
   and the previous complete application directory. A config dump is not a backup.
3. Rehearse with a restored database and isolated/regtest daemons and wallets.
   Never connect a staging payout manager to production wallets. Give it separate
   ports and recovery paths. Keep only one payout manager for each database.
4. Confirm startup validation, daemon sync, schema preflights, notifications, miner
   authorization, accepted shares, candidate attribution and payout/recovery checks.
   A successful `--dumpconfig` checks JSON/schema only: it starts no services,
   opens no database/daemon connections and cannot prove runtime readiness.
5. Stop all pools, relay/recorder/recovery processes and payout writers sharing the
   live database. Let accounting drain; stop Miningcore before any local PostgreSQL
   shutdown. Back up the quiescent database and retain uncertainty/recovery evidence.
   Apply only the migrations selected above. Do not clear a stale payout owner
   without the [ownership recovery checks](database.md#recover-payout-manager-ownership-safely).
6. Activate the complete candidate, retaining config paths, pool identities, mode
   choices and protected state. For local systemd PostgreSQL, verify the actual
   [service ordering](systemd-postgresql-ordering.md), including any changed cluster unit.
   The supplied service expects the documented installation layout; a development
   build directory is not automatically that layout.
7. Start one owner, verify every pool and move representative miners first.
   Monitor accepted-difficulty totals, bans, disconnects, admission limits,
   recovery/fatal state, database errors, pending rewards and wallet/payment evidence.
   Before stable promotion, complete a controlled soak and the applicable
   [production commissioning checks](mainnet-validation.md).

## Container upgrades

The published GHCR image is assembled from the release archive and includes
`/app/migrations/` and `/app/BUILD-INFO`. Source-built images using the root
Dockerfile copy the publish output instead; do not assume they contain the same
SQL directory. Neither image automatically upgrades the database. Do not try to
retrieve all migrations from embedded application resources.

1. Select the published candidate, pull its versioned image and record the
   immutable image digest and `org.opencontainers.image.revision` label. Match that revision to the
   selected release's source SHA and archive `BUILD-INFO` using the
   [release identity checks](releases.md#install-the-archive). Do not
   mix an image from one commit with SQL from a moving `dev` branch.
2. Obtain SQL from the published candidate image's `/app/migrations/` and inspect
   its `/app/BUILD-INFO`, or use the same verified release archive's `migrations/`
   directory. Files can be copied from a newly created, stopped container without
   starting Miningcore or attaching live configuration/volumes; remove that
   temporary container and its anonymous volumes afterwards. For source-built
   images, use an immutable checkout of that exact release source SHA. In a source
   checkout the directory is `src/Miningcore/Persistence/Postgres/Scripts/`.
   Retain the complete directory and verify its source identity before running
   any SQL; do not execute archive binaries on an incompatible host just to get
   the text files. An existing PPS/pooled-merged v0.3.0 schema needs
   `add_pps_arithmetic_version.sql`; choose other scripts only from the matrix.

   To extract from the published image, replace the digest below with the verified
   digest from step 1. This creates a stopped container with no network, copies to
   a new private directory and attempts to remove the temporary container/anonymous
   volumes on success or failure. Cleanup failures report the container ID and
   return nonzero; any earlier failure status is preserved. It never starts
   Miningcore or applies SQL. Inspect the
   copied `BUILD-INFO` and migrations before using them; copied files alone do not
   prove that the image's source identity matches the selected release.
   The function calls `docker` directly: define and run it in a shell already
   authorized for Docker. If your setup requires `sudo docker`, use an administrative
   shell such as `sudo -i`; `sudo extract_candidate_sql` cannot invoke a shell function.
   The private output directory is retained even after a failed copy, and its path
   is printed before copying. Treat it as incomplete unless both copies succeed and
   the release identity is verified; inspect or remove that specific directory afterwards.

   ```console
   extract_candidate_sql() (
     umask 077
     image='ghcr.io/ninjak1dd/miningcore@sha256:REPLACE_WITH_VERIFIED_DIGEST'
     container=$(docker create --network none "$image") || return
     cleanup_candidate() {
       status=$?
       if ! docker rm -v "$container" >/dev/null; then
         printf 'ERROR: Failed to remove temporary container %s\n' "$container" >&2
         if [ "$status" -eq 0 ]; then status=1; fi
       fi
       exit "$status"
     }
     trap cleanup_candidate EXIT
     output=$(mktemp -d "${TMPDIR:-/tmp}/miningcore-sql.XXXXXXXX") || return
     printf 'Candidate extraction directory (may be incomplete): %s\n' "$output"
     docker cp "$container:/app/migrations" "$output/" &&
       docker cp "$container:/app/BUILD-INFO" "$output/" &&
       printf 'Inspect candidate SQL and BUILD-INFO in %s\n' "$output"
   )
   extract_candidate_sql
   ```

3. Preserve the existing container/Compose definition, previous image digest,
   config/environment files, custom coin mounts, certificates and persistent
   volumes. Do not run the new-install download/token-generation blocks in the
   README or release guide over this state. Review UID/GID 10001 access and
   private network/RPC reachability without widening permissions.
4. Rehearse on isolated database/wallet copies. At cutover stop every writer and
   payout manager sharing the live database, including other containers and
   external relays. Back up the quiescent database, then have the database
   administrator apply only the selected verified scripts with `ON_ERROR_STOP`.
   Do not grant schema ownership or transition-activation rights to the container.
5. Recreate the service with the recorded candidate image/digest and the same
   protected mounts, environment, network and port definitions. Keep only one
   payout owner; do not launch an old and new container side by side against the
   live database. A Compose image change does not justify removing volumes or
   regenerating credentials.
6. Apply the same [cutover checks](#staging-and-cutover) and financial rollback
   boundary below. On rollback retain new database/wallet evidence and select
   the recorded previous image, not a mutable `latest` tag.

## Rollback and release evidence

Retain the new database, journals and wallet history before stopping the candidate.
Do not restore a pre-upgrade database over payments made after that backup, remove
PPS transitions or replay payouts merely because an acknowledgement was lost.
Reconcile liabilities, submissions and wallet transactions before selecting a
compatible application/database pair. PPS arithmetic activation is one-way;
the [migration rollback boundary](pps-arithmetic-migration.md) takes precedence.
Installing only the version-0 arithmetic metadata does not itself prevent the
v0.3.0 schema contract from working; this is not permission to discard payments,
recovery records or other new evidence, nor to roll back after a version-1 cutover.

The RC does not certify every daemon, miner, operating system or mainnet payout
combination. Dated lab/mainnet results retain their original source identities.
Record the final candidate commit, exact dependencies, test results, exclusions
and deployment-specific observations. A release tag and green CI do not replace
the commissioning checks for an operator's private configuration.
After publication, retain the actual source SHA, archive identity/checksums,
container tag/digest and prerelease status, and confirm they refer to that same
candidate. Do not mark tag/publication gates complete based only on PR CI.
