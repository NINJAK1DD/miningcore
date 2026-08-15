# Miningcore API

This guide describes the API implemented by the current repository. The root URL is normally
`http://HOST:4000`; use HTTPS through a trusted reverse proxy for public access.

## Configuration

```json
"api": {
  "enabled": true,
  "listenAddress": "127.0.0.1",
  "port": 4000,
  "adminPort": 4001,
  "metricsPort": 4002,
  "adminIpWhitelist": [ "127.0.0.1" ],
  "metricsIpWhitelist": [ "127.0.0.1" ],
  "rateLimiting": {
    "disabled": false,
    "ipWhitelist": [ "127.0.0.1" ],
    "rules": []
  }
}
```

Keep the admin and metrics listeners private. If `adminIpWhitelist` or `metricsIpWhitelist` is empty,
the default is localhost. If a reverse proxy is used, test which client address Miningcore observes
before changing a whitelist.

Miningcore does not attach permissive CORS headers to `/api/admin` or `/metrics`, whether those
routes use dedicated ports or the legacy shared listener. Prometheus, `curl` and other non-browser
clients are unaffected. A custom browser dashboard must not scrape `/metrics` cross-origin; collect
or proxy the required telemetry through a deliberately secured same-origin service instead.

Every response the API pipeline produces for those two protected route families carries
`Cross-Origin-Resource-Policy: same-origin`, including wrong-listener, rate-limit, whitelist,
authentication, credential-unavailable and method rejections. Protected pipeline responses also
send `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. Protocol errors that Kestrel
rejects before a request enters the pipeline cannot carry these application headers.

The resource policy blocks eligible cross-origin no-CORS subresource use of the response. It is not
a general navigation or framing control: ordinary nested navigations can remain eligible when the
embedding document does not require cross-origin isolation. Use an appropriate framing policy such
as CSP `frame-ancestors` if a deployment must prohibit framing. CORS and these response headers
restrict browser use of a response; they do not prevent a request from being sent and do not
replace listener isolation, IP whitelists, bearer authentication, TLS or firewall controls. See the
[Fetch Standard resource-policy algorithm](https://fetch.spec.whatwg.org/#cross-origin-resource-policy-header).

Every administrative request also requires `Authorization: Bearer TOKEN`, where `TOKEN` comes only
from the `MININGCORE_ADMIN_API_TOKEN` process environment. Missing or invalid token configuration
fails closed for `/api/admin` without stopping pools or the public API. Administrative responses do
not include CORS headers, and the token must never be supplied to browser code. The token must be
exactly 64 hexadecimal characters. Generate, provision, test and rotate it using the
[administrative API security guide](admin-api-security.md).

The public `/api/help` route lists only public API operations. It deliberately omits
`/api/admin` routes so unauthenticated callers cannot use it as an administrative route catalogue.
Prometheus exports `miningcore_admin_api_authentication_total` with `accepted`, `rejected` and
`unavailable` outcomes so operators can alert on authentication failures without relying on
per-request log messages. Miningcore writes the first rejected bearer attempt and at most one
suppression summary per minute at `Info`; intervening details remain available at `Debug`. This
process-wide limit uses a monotonic elapsed-time clock and prevents unauthenticated clients from
flooding normal operational logs even when the host wall clock is corrected.

When `adminPort` or `metricsPort` is configured, Miningcore creates a dedicated listener and exposes
only that route family on it. Public REST and WebSocket routes remain on `port`; requests for
`/api/admin` or `/metrics` on the public listener, and requests for public routes on a dedicated
listener, return `404 Not Found`. The IP whitelists remain an independent second control. Apply a
firewall rule as well because all three ports bind to `listenAddress`.

`api.port` defaults to `4000` when it is omitted. Omitting either optional port preserves the
previous shared-listener behavior for that route. In particular, omitting `adminPort` leaves
`/api/admin` on the public listener: a reverse proxy must explicitly deny that path unless the
admin whitelist and firewall are the intended protection. Omitting `metricsPort` likewise leaves
`/metrics` on the public listener; a public reverse proxy must deny that path unless exposing pool
metrics is intentional. An explicit dedicated port must be
different from the public port and from the other dedicated port. All API ports must be between 1
and 65535. An API listener and an enabled local Stratum endpoint may share a number only when they
bind different specific addresses; the same address and wildcard/specific overlaps stop startup
with a configuration error.

The port limits and duplicate checks run for an enabled API during normal startup rather than schema
loading. This allows the one-shot `-rs` share importer to remain available when listener-only
settings are stale or temporarily invalid; recovery mode does not open API or Stratum sockets.

For nginx, deny both protected paths whenever they share the public listener:

```nginx
location ~* ^/(?:api/admin|metrics)(?:/|$) {
    return 404;
}
```

The case-insensitive, segment-bounded expression protects every admin and metrics subpath without
blocking public lookalikes such as `/api/administrator` or `/metrics-export`. Keep this denial before
any other regular-expression locations. Do not use a public `^~` prefix that would prevent nginx
from evaluating the protected-route expression.

## Discovery and health

```console
curl http://127.0.0.1:4000/api/help
curl http://127.0.0.1:4000/api/health-check
curl http://127.0.0.1:4000/api/pools
```

`/api/help` is the runtime route summary. Use it as the first check when a client written for another
Miningcore fork expects a route that may have changed.

The `ports` objects returned by `/api/pools` and `/api/pools/{id}` are dedicated public projections,
not serialized `PoolEndpoint` runtime configuration. They expose connection, difficulty, VarDiff, TLS
mode and PROXY-protocol mode information needed by clients. TLS certificate paths and passwords, plus
the trusted PROXY-protocol peer allow-list, have no public DTO members and are never serialized. Null
listener entries retained by disabled or relay-only configurations are omitted from the public map.
Specifically, clients must not expect `ports[*].tlsPfxFile`, `ports[*].tlsPfxPassword` or
`ports[*].tcpProxyProtocol.proxyAddresses`: those keys are absent rather than `null`, including when
`legacyNullValueHandling` is enabled. All other endpoint property names and values remain unchanged.

## Public routes

The main GET routes are:

```text
/api/blocks
/api/pools
/api/pools/{poolId}
/api/pools/{poolId}/performance
/api/pools/{poolId}/miners
/api/pools/{poolId}/blocks
/api/pools/{poolId}/payments
/api/pools/{poolId}/miners/{address}
/api/pools/{poolId}/miners/{address}/blocks
/api/pools/{poolId}/miners/{address}/payments
/api/pools/{poolId}/miners/{address}/balancechanges
/api/pools/{poolId}/miners/{address}/earnings/daily
/api/pools/{poolId}/miners/{address}/performance
/api/pools/{poolId}/miners/{address}/settings
```

Version 2 list routes are available for blocks, payments, miner blocks, miner payments and balance
changes under `/api/v2/...`. They should be preferred by new front ends where pagination or the
response shape differs from the legacy endpoint. Inspect `/api/help` and the controller response on a
test instance before binding a public UI to it.

The miner response from `/api/pools/{poolId}/miners/{address}` includes `bestShare` and
`bestSessionShare` for both the miner aggregate and each current worker. Best Share is the highest
achieved difficulty retained in that miner's share history. Best Session Share is limited to the
logical session IDs represented by the latest miner-statistics sample. See
[Best-share dashboard data](database.md#best-share-dashboard-data) for the PostgreSQL fields and
retention implications.

## WebSocket notifications

Miningcore exposes live notifications at:

```text
ws://HOST:4000/notifications
```

Use `wss://` when the API is behind HTTPS. Reverse proxies must explicitly pass WebSocket upgrade
headers and use timeouts suitable for long-lived connections.

### Payment event contract

Payment notifications have `type: "payment"` and an `outcome` of `success`, `failure` or
`uncertain`. `amount` and `recipientsCount` describe the recipients represented by that event. A
manager-owned uncertain event covers every initially selected balance; handler-owned success and
conclusive-failure events can cover only the payable subset after below-precision balances are
omitted.

The payout manager is the sole notification owner for uncertain outcomes across supported coin
families. Paged and per-recipient handlers preserve recipients already persisted as `accepted`,
explicit wallet rejections as `failed`, the wallet call in progress as `uncertain`, and untouched
recipients as `notAttempted`. Cancellation while a wallet submission is in flight is treated as
uncertain and retains durable payout ownership; cancellation before submission remains a normal
shutdown without a payout-failure event.

All outcome aggregate amount fields (`acceptedAmount`, `failedAmount`, `uncertainAmount` and
`notAttemptedAmount`) represent original amounts owed. `submittedAmount` is the sum requested from
the wallet for attempted recipients whose submitted amount is known. `precisionAdjustment` is the
sum of each attempted recipient's `SubmittedAmount - Amount`; it excludes every `NotAttempted`
balance, including cancellation-skipped and below-precision recipients.

For a complete uncertain Bitcoin-family reconciliation where `submittedAmount` is available, clients
can validate the totals with:

```text
submittedAmount = amount - notAttemptedAmount + precisionAdjustment
```

Do not infer `precisionAdjustment` from `submittedAmount - amount` when `notAttemptedAmount` is
nonzero.

Administrative reconciliation amounts and Bitcoin-family exact payout diagnostics use invariant
decimal notation, preserve the complete decimal value, and remove insignificant trailing zeroes
without rounding. For example, stored values `1.23450` and `0.000000123456` are displayed as
`1.2345` and `0.000000123456`. An uncertain notification omits the wallet-request summary when
`precisionAdjustment` is zero because the submitted total would only repeat the attempted amount
owed.

Handlers that submit one wallet transaction per recipient require each call to return a distinct
transaction ID. Acceptance verification is keyed by that ID. A duplicate returned for separate
submissions is therefore treated as financially uncertain, includes the known ID in administrative
reconciliation, and retains payout ownership rather than risking an incorrect recipient mapping.

Kaspa can return an ordered transaction chain for one recipient. In that case `txIds` on the success
event retains every returned identity in wallet order as a backwards-compatible batch-level list.
The optional `recipientTransactionChains` array additionally maps each `address` to its ordered
`transactionIds` and explicit `canonicalTransactionId`, allowing clients to automate per-recipient
reconciliation without inferring chain boundaries. The canonical value is the final recipient-facing
ID stored in payment history; prerequisite IDs remain in the success event and administrative
reconciliation. Miningcore fails closed unless every returned list is complete, nonblank and unique,
and transaction identities do not overlap between separate recipient submissions.

Public clients can use `recipientsCount` and the outcome-aware `acceptedCount`, `acceptedAmount`,
`failedCount`, `failedAmount`, `uncertainCount`, `uncertainAmount`, `notAttemptedCount` and
`notAttemptedAmount` aggregates. Nullable fields are omitted when they do not apply.

The public WebSocket payload intentionally excludes `error` and uncertain-outcome recipient-level
`reconciliation`. Those values can contain wallet errors and ambiguous transaction mappings and
remain available only to administrative notification channels and logs. A conclusive Kaspa success
can include the optional recipient address and transaction-chain mapping described above. Front ends
written against an older event shape must use `outcome` and the safe aggregate fields instead of
displaying `error`.

## Metrics and administration

When `metricsPort` is configured, Prometheus-compatible metrics are served from `/metrics` on that
listener. Administrative routes are under `/api/admin` on `adminPort`; they can change logging and
payment-processing state. All read and write routes require the bearer token in addition to the IP
whitelist. Never publish the admin port through the public reverse proxy.

The metrics endpoint intentionally emits no permissive CORS headers. This does not affect normal
Prometheus scraping because CORS is enforced by browsers, not server-side monitoring clients.
After listener, rate-limit and IP-whitelist checks succeed, Miningcore accepts only `GET` and `HEAD`
for the metrics route family. `HEAD` returns the same response headers without an exposition body.
It still performs a full registry collection and serialization server-side, so it is not a cheaper
high-frequency liveness probe than `GET`. Exact `GET` and `HEAD` scrapes bypass the public API rate
limiter; rejected lowercase, mixed-case and unsupported method tokens remain subject to it.
`OPTIONS`, `POST` and every other method return an empty `405 Method Not Allowed` response with
`Allow: GET, HEAD`; they never invoke the metrics exporter. Rejected listener and client identities
retain their earlier `404`, `429` or `403` result instead of disclosing the method contract. This
follows the HTTP method contract in
[RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#name-method-definitions), while preserving the
simple `GET` required by the
[OpenMetrics specification](https://prometheus.io/docs/specs/om/open_metrics_spec/).

For the example configuration, local checks and a Prometheus scrape target use:

```console
sudo sh -c '
  . /etc/miningcore/miningcore.env
  printf "Authorization: Bearer %s\n" "$MININGCORE_ADMIN_API_TOKEN" |
    curl --fail --header @- \
      http://127.0.0.1:4001/api/admin/stats/gc
'
curl http://127.0.0.1:4002/metrics
```

After migrating an existing installation that sets dedicated ports, change any scrape target from
`http://127.0.0.1:4000/metrics` to `http://127.0.0.1:4002/metrics`. Confirm that the public listener
returns 404 for protected route families before treating the separation as deployed:

```console
curl --output /dev/null --write-out '%{http_code}\n' http://127.0.0.1:4000/metrics
curl --output /dev/null --write-out '%{http_code}\n' http://127.0.0.1:4000/api/admin/stats/gc
```

Share-accounting backlog monitoring uses three gauges and one counter with a fixed `queue` label:

| Metric | Meaning |
| --- | --- |
| `miningcore_share_persistence_queue_depth` | Shares currently waiting in the queue |
| `miningcore_share_persistence_queue_high_watermark` | Largest queue depth observed since process start |
| `miningcore_share_persistence_queue_capacity` | Configured bounded capacity |
| `miningcore_share_persistence_queue_overflow_total` | Writes rejected because the queue was full or concurrently completed |

The `queue` label is `primary` for the normal PostgreSQL persistence queue and
`emergency_journal` for the overflow writer that force-flushes shares to `shareRecoveryFile`.
These series are exported only on a node that runs the local `ShareRecorder`; relay-only nodes omit
them because they do not own either queue. Admission and removal share an exact accounting boundary,
so concurrent producers cannot make the high-water mark miss a reached capacity. Alert before either
depth approaches capacity and on any increase in the overflow counter. A sustained non-zero
emergency-journal depth requires investigation of PostgreSQL latency or primary-queue saturation.

Litecoin-Dogecoin merged mining exports bounded RPC duration/outcome, fallback-episode, availability
and degraded-state metrics. The authoritative metric contract, PromQL examples and timeout guidance
are in the [merged-mining operations guide](merged-mining-litecoin-dogecoin.md#template-refresh).
Review that guidance before changing `auxiliaryTemplatePollTimeoutMs`; increasing it can delay a
new parent-chain job.

## Front ends and reverse proxies

A static front end should call only the public API. Put both behind HTTPS, restrict cross-origin access
to expected sites, rate-limit expensive miner/history routes, and cache only responses whose freshness
requirements permit it.

The public miner-settings route is read-only. Updating settings requires authenticated
`PUT /api/admin/pools/{poolId}/miners/{address}/settings`. A public WebUI must not call this route
directly or receive the operator token. If self-service settings are required, place a trusted
server-side service with its own user authentication and authorization between the browser and
Miningcore.

The companion [NINJAK1DD/Miningcore.WebUI](https://github.com/NINJAK1DD/Miningcore.WebUI) consumes
this fork's `bestShare` and `bestSessionShare` miner fields. Keep the frontend and backend API
contracts aligned when updating either repository.

The community [btclinux/Miningcore.WebUI](https://github.com/btclinux/Miningcore.WebUI) is an optional
starting point, not part of this repository. It targets another Miningcore fork. Review its current
maintenance, licence and API compatibility, then audit and adapt it before use.
