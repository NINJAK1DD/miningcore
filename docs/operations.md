# Miningcore operator handbook

This is the day-to-day checklist for a production Miningcore service. It deliberately links to the
authoritative procedures instead of duplicating recovery SQL or release commands.

## Before accepting miners

- Use a maintained Linux host and keep the .NET 10 runtime, PostgreSQL and coin daemons serviced.
- Keep wallet RPC, daemon RPC, PostgreSQL, the admin API, metrics and relay ports on trusted networks.
- Run exactly one payout manager for each database/pool set. Automatic payout-manager failover is not
  supported.
- Store the production configuration and licence environment outside the application directory with
  service-account-only permissions.
- Put `shareRecoveryFile` on separately monitored or reserved storage when possible, and persist
  `shareRecoveryStateDirectory` across service or container replacement.
- Use a service manager whose forced-stop timeout exceeds Miningcore's 45-second accounting budget.
  The supplied systemd unit uses 90 seconds.
- Verify a PostgreSQL backup and retain the previous immutable application directory or container
  before every upgrade.

Use the [release guide](releases.md) for installation, the [configuration guide](configuration.md)
for settings, and the [database guide](database.md) for backup and schema preparation.

## Normal service checks

For a systemd installation:

```console
sudo systemctl status miningcore --no-pager
sudo journalctl -u miningcore --since '30 minutes ago' --no-pager
curl --fail http://127.0.0.1:4000/api/health-check
curl --fail http://127.0.0.1:4000/api/pools
sudo sh -c '
  . /etc/miningcore/miningcore.env
  printf "Authorization: Bearer %s\n" "$MININGCORE_ADMIN_API_TOKEN" |
    curl --fail --header @- \
      http://127.0.0.1:4001/api/admin/stats/gc
'
curl --fail http://127.0.0.1:4002/metrics --output /dev/null
```

The last two checks use the dedicated ports from `config.example.json`; substitute the production
values or the shared public port when those optional listeners are omitted. A public-port request to
a configured protected route should return 404.
Provision the admin credential according to the
[administrative API security guide](admin-api-security.md); never put it in `config.json` or a
public dashboard.

Confirm that:

- every enabled pool is online and its daemon is synchronized;
- expected Stratum ports are listening and accepting representative test miners;
- shares continue to reach PostgreSQL;
- the intended process owns payout processing and no uncertain payout is awaiting reconciliation;
- disk space and inodes are healthy on database, log, journal and service-state filesystems; and
- backups complete and can be inspected or restored on the planned schedule.

Do not treat an active process alone as proof of a healthy pool. Check the API, recent shares, daemon
height and the administrative logs together.

## Monitoring and alerts

Monitor at least:

- process availability, restart count and non-zero exit status;
- daemon sync, peer state and wallet/RPC errors;
- pool connections, hashrate, accepted/rejected shares and last block time;
- PostgreSQL health, transaction latency, backup age and free storage;
- wallet balances, payout ownership and uncertain payment/block events;
- Miningcore log growth and filesystem free bytes/inodes; and
- primary and emergency persistence-queue depth, high-water mark, capacity and overflow count.

Prometheus metric names and queue labels are documented in [API and monitoring](api.md#metrics-and-administration).
Administrative email or Pushover delivery is an extra signal; the service journal, durable recovery
state and database remain authoritative.

## Bitcoin-family payout wallets

### Fee reserve and balance readiness

Determine who pays transaction fees before enabling a Bitcoin-family payout wallet. With
`minersPayTxFees` disabled, the pool pays the fee and the exact wallet used by Miningcore needs a
confirmed, spendable reserve. A newly matured coinbase can equal the complete block reward while
the wallet still needs an additional input to pay the fee on top of Miningcore's recipient outputs.
Without that reserve, `sendmany` can return `Insufficient funds` code `-6` even though the reward is
visible in the wallet.

With `minersPayTxFees` enabled, Miningcore asks a normal `sendmany` wallet to deduct the fee from
the recipient outputs, so a separate reserve is not strictly required for that request. A daemon or
coin template that uses Miningcore's broken-`sendmany` fallback instead submits individual
`sendtoaddress` requests without fee subtraction. Keep confirmed spendable headroom for that path
even when miners normally pay fees.

There is no safe universal reserve amount. Size it for several payout cycles, the expected recipient
count, input consolidation, the daemon's current fee policy, whether the pool or miners pay fees,
and whether the coin uses per-recipient fallback submissions.
Keep the hot-wallet balance no larger than the operating reserve and expected near-term payouts.
Monitor confirmed, unconfirmed and immature balances separately, and verify that the configured
named wallet is loaded after every daemon restart. Use daemon-specific read-only checks with the
same network, authentication and wallet-selection arguments as Miningcore:

```console
bitcoin-cli -rpcwallet=POOL_WALLET getwalletinfo
bitcoin-cli listwallets
bitcoin-cli -rpcwallet=POOL_WALLET listunspent
bitcoin-cli estimatesmartfee 6

litecoin-cli -rpcwallet=POOL_WALLET getwalletinfo
litecoin-cli listwallets
litecoin-cli -rpcwallet=POOL_WALLET listunspent
litecoin-cli estimatesmartfee 6

dogecoin-cli getwalletinfo
dogecoin-cli listunspent
```

Dogecoin Core does not provide the same named-wallet discovery workflow as current Bitcoin and
Litecoin Core releases; use the wallet selection supported by the deployed Dogecoin version rather
than assuming `listwallets` is available. Dogecoin deployments commonly use an explicitly configured
`paytxfee` and release-specific dust policy; inspect those settings and the deployed release notes
instead of relying on `estimatesmartfee` as the sole fee-policy signal.

Confirmed inputs matter. Miningcore's ordinary fee-subtracting `sendmany` request explicitly uses
`minconf=1`; the shorter ordinary form and the per-recipient `sendtoaddress` fallback rely on the
daemon's defaults. Verify the exact daemon and wallet behavior used by the configured coin before
enabling payments.

If `sendmany` returns code `-6`, do not edit balances or create a replacement payment manually.
Confirm that no payment batch was persisted and identify whether the failure came from a missing
pool-paid reserve, immature or unconfirmed inputs, coin selection, or the per-recipient fallback.
Correct that cause, then let the normal scheduler retry. Verify one wallet transaction, one
`payment_batches` identity, the expected `payments` rows and only the configured precision residual. Use the
[Bitcoin-family payout reconciliation](database.md#reconcile-a-bitcoin-family-payout) procedure when
the wallet outcome or transaction identity is uncertain.

### Wallet backups

Use each daemon's `backupwallet` RPC instead of copying a live `wallet.dat`. Keep each daemon in a
separate service-account-owned directory beneath a root-controlled parent. Do not grant the daemon
accounts write access to one another's backups or to the root-owned integrity manifests.

Run the complete setup, RPC and checksum procedure on the host where the daemon actually runs:
`backupwallet` writes to the daemon host's filesystem, not the `*-cli` host's filesystem. Supply the
same configuration or data directory, network, RPC endpoint, authentication, proxy and wallet
selection used by Miningcore. Prefer a protected daemon configuration or authentication cookie to
putting RPC secrets in shell history. If daemons run on separate hosts, maintain a separate
root-controlled hierarchy and manifests on each host and copy each hierarchy to its own host-specific
off-site directory.

On each daemon host, replace `DAEMON` and `DAEMON_SERVICE_USER` with that daemon's directory name and
actual systemd `User=` value. Repeat the child-directory command for each daemon that shares a host:

```console
BACKUP_ROOT=/srv/wallet-backups
DAEMON=bitcoin
DAEMON_SERVICE_USER=bitcoin

sudo install -d -o root -g root -m 0711 "$BACKUP_ROOT"
sudo install -d -o "$DAEMON_SERVICE_USER" -g root -m 0700 "$BACKUP_ROOT/$DAEMON"
```

If the daemon service uses systemd filesystem hardening such as `ProtectSystem=strict`, allow only
its own child directory, not the complete backup root. For example, add
`ReadWritePaths=/srv/wallet-backups/REPLACE_WITH_DAEMON` to a service drop-in, then run
`systemctl daemon-reload`, restart the daemon and inspect the effective unit before requesting a
backup.

Bitcoin and Litecoin accept an absolute destination. Use a unique filename for each generation and
replace `REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS` with the arguments that select the exact production
daemon.

#### Bitcoin

```console
BACKUP_ROOT=/srv/wallet-backups
stamp="$(date -u +%Y%m%dT%H%M%S%NZ)"
bitcoin_rel="bitcoin/bitcoin-pool-${stamp}.dat"
bitcoin_backup="$BACKUP_ROOT/$bitcoin_rel"

bitcoin-cli REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS \
  -rpcwallet=POOL_WALLET \
  backupwallet "$bitcoin_backup"
sudo bash -eu -o pipefail -c '
  umask 077
  set -C
  cd "$1"
  sha256sum -- "$2" > "$3"
' _ "$BACKUP_ROOT" "$bitcoin_rel" "SHA256SUMS.bitcoin-${stamp}"
```

#### Litecoin

```console
BACKUP_ROOT=/srv/wallet-backups
stamp="$(date -u +%Y%m%dT%H%M%S%NZ)"
litecoin_rel="litecoin/litecoin-pool-${stamp}.dat"
litecoin_backup="$BACKUP_ROOT/$litecoin_rel"

litecoin-cli REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS \
  -rpcwallet=POOL_WALLET \
  backupwallet "$litecoin_backup"
sudo bash -eu -o pipefail -c '
  umask 077
  set -C
  cd "$1"
  sha256sum -- "$2" > "$3"
' _ "$BACKUP_ROOT" "$litecoin_rel" "SHA256SUMS.litecoin-${stamp}"
```

#### Dogecoin

[Dogecoin Core 1.14.6 and later](https://github.com/dogecoin/dogecoin/releases/tag/v1.14.6)
writes `backupwallet` output only beneath its configured `backupdir` and does not overwrite an
existing filename. Create a protected directory owned by the Dogecoin service account, add the
setting to `dogecoin.conf`, and restart Dogecoin Core before requesting the backup. The example
below requires `backupdir` to be exactly `$BACKUP_ROOT/dogecoin`; if you change either path, update
both the daemon configuration and the backup commands:

```ini
backupdir=/srv/wallet-backups/dogecoin
```

```console
BACKUP_ROOT=/srv/wallet-backups
stamp="$(date -u +%Y%m%dT%H%M%S%NZ)"
dogecoin_backup="dogecoin-pool-${stamp}.dat"
dogecoin_rel="dogecoin/$dogecoin_backup"

dogecoin-cli REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS backupwallet "$dogecoin_backup"
sudo bash -eu -o pipefail -c '
  umask 077
  set -C
  cd "$1"
  sha256sum -- "$2" > "$3"
' _ "$BACKUP_ROOT" "$dogecoin_rel" "SHA256SUMS.dogecoin-${stamp}"
```

Do not reuse a Dogecoin backup filename. If the RPC reports that the destination already exists,
retain that backup and issue a new timestamped name.

The checksum commands run without an unguarded pipeline and create one root-owned manifest per
backup generation. Each manifest contains a daemon-relative path such as
`bitcoin/bitcoin-pool-...dat`, so a missing or unreadable backup makes validation fail while the
manifest remains portable with the directory hierarchy. Do not move manifests into a
daemon-writable directory.

Copy the complete hierarchy and all paired manifests to encrypted external storage with restricted access. If
the daemons use separate hosts, preserve each copy beneath a distinct host-specific directory. Verify
the copied files **at that destination** before accepting the backup; do not accidentally verify the
original `/srv` files or replace the copied manifests before this check. These commands assume the
encrypted storage is already mounted and decrypted. For an encrypted archive or backup object,
decrypt or mount it into an isolated verification destination first:

```console
TRUSTED_BACKUP_ROOT=/trusted/external/REPLACE_WITH_DAEMON_HOST/wallet-backups
sudo bash -eu -o pipefail -c '
  cd "$1"
  declare -A covered=()
  count=0
  while IFS= read -r -d "" manifest; do
    sha256sum --check -- "$manifest"
    mapfile -t entries < <(cut -d " " -f 3- -- "$manifest")
    test "${#entries[@]}" -eq 1
    backup="./${entries[0]}"
    test -z "${covered[$backup]+present}"
    covered["$backup"]=1
    count=$((count + 1))
  done < <(find . -maxdepth 1 -type f -name "SHA256SUMS.*" -print0 | sort -z)
  test "$count" -gt 0

  while IFS= read -r -d "" backup; do
    if test -z "${covered[$backup]+present}"; then
      echo "Backup has no paired checksum manifest: $backup" >&2
      exit 1
    fi
  done < <(find . -mindepth 2 -maxdepth 2 -type f -name "*.dat" -print0 | sort -z)
' _ "$TRUSTED_BACKUP_ROOT"
```

The nanosecond timestamp makes accidental name reuse extremely unlikely, while `set -C` prevents a
manifest from being overwritten if a name is nevertheless reused. Destination verification also
rejects duplicate manifest coverage and any retained `.dat` backup that has no paired manifest.

Define and monitor a retention policy for both local and off-host storage. Never delete the last
verified generation. Prune an old backup only after a newer off-host copy has passed checksum and
restore testing, and remove the old backup together with its paired `SHA256SUMS.*` manifest using a
root-controlled process. This keeps every retained manifest verifiable instead of leaving entries
for files that were intentionally removed.

Keep wallet encryption credentials or recovery material in a separate protected location. Repeat
backups after wallet/keypool changes and periodically restore a copy in an isolated lab with payments
disabled. Verify the digest, wallet load, expected controlled addresses and a non-production signing
workflow; a file that has never been restored is not a proven backup.

## Safe stop and start order

For planned maintenance, stop Miningcore before PostgreSQL or coin wallets so active persistence can
finish and payout ownership can be released cleanly:

```console
sudo systemctl stop miningcore
sudo systemctl is-active miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true
```

Start PostgreSQL and the required daemons first, wait for them to become ready, then start Miningcore.
Read the complete startup log before returning traffic. Do not force-kill the process merely because
shutdown takes several seconds; candidate delivery and share recovery intentionally outlive ordinary
request cancellation within the bounded shutdown window.

## Incident routing

Start with [Troubleshooting](troubleshooting.md) for bounded evidence collection, HTTP status
interpretation and symptom-based triage. The table below identifies the authoritative recovery
procedure for incidents that can affect accounting or availability.

- **Disk full or database startup failure:** preserve logs and recovery files, then follow
  [disk-exhaustion recovery](database.md#recover-after-disk-exhaustion).
- **Exit status `74` or fatal recovery latch:** keep miners offline and follow
  [share-recovery fatal-state reconciliation](database.md#reconcile-fatal-share-recovery-state).
- **Recovery journal contains records:** preserve the source and use the
  [manifested one-shot importer](database.md#inspect-and-import-a-recovery-journal).
- **Another payout manager owns the database:** prove the old process and backend are dead, then
  follow [payout ownership recovery](database.md#recover-payout-manager-ownership-safely).
- **`sendmany` returns code `-6`:** inspect confirmed inputs, fee ownership and fallback mode; see
  [fee reserve readiness](#fee-reserve-and-balance-readiness).
- **Auxiliary template update fails:** confirm recovery and inspect DOGE sync, load, RPC pressure
  and [template metrics](merged-mining-litecoin-dogecoin.md#template-refresh).
- **Stratum listener cannot be reserved:** keep the cluster offline and inspect the named endpoint;
  see [listener reservation](configuration.md#stratum-listener-reservation).
- **Logs consume unexpected space:** inspect native archives and follow
  [log-rotation guidance](configuration.md#log-files-and-rotation).
- **Relay receiver is unavailable:** restore the route and review the
  [share-relay durability boundary](share-relays.md#durability-boundary).

Never clear a payout owner, delete a fatal latch, edit a recovery journal, or manually change balances
as a first response. The recovery gates exist to prevent duplicate payments and silent share loss.

## Routine maintenance

- Apply security and runtime servicing updates in a controlled maintenance window.
- Review new Miningcore release notes and database migrations before changing binaries.
- Test PostgreSQL restore and application rollback periodically, not only during an incident.
- Refresh daemon-generated wallet backups and test their restoration in an isolated lab.
- Test administrative notifications without exposing credentials in logs or shell history.
- Review firewall rules, public ports, TLS policy, recovery-storage capacity and monitoring thresholds.
- Re-run daemon-backed or physical-relay validation after changing daemons, wallets, topology,
  firewalls, proxies or operating systems.

Windows builds remain suitable for development and test labs; Linux is the supported production
target. A laboratory pass does not replace validating the actual production route and configuration.
