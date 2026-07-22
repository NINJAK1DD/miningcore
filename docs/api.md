# Miningcore API

This guide describes the API implemented by this repository revision. The root URL is normally
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

## Discovery and health

```console
curl http://127.0.0.1:4000/api/help
curl http://127.0.0.1:4000/api/health-check
curl http://127.0.0.1:4000/api/pools
```

`/api/help` is the runtime route summary. Use it as the first check when a client written for another
Miningcore fork expects a route that may have changed.

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

## WebSocket notifications

Miningcore exposes live notifications at:

```text
ws://HOST:4000/notifications
```

Use `wss://` when the API is behind HTTPS. Reverse proxies must explicitly pass WebSocket upgrade
headers and use timeouts suitable for long-lived connections.

### Payment event contract

Payment notifications have `type: "payment"` and an `outcome` of `success`, `failure` or
`uncertain`. `amount` is the original amount owed across the selected batch. When wallet precision
changes the request, `submittedAmount` contains the attempted wallet total and
`precisionAdjustment` contains `submittedAmount - amount`.

Public clients can use `recipientsCount` and the outcome-aware `acceptedCount`, `acceptedAmount`,
`failedCount`, `failedAmount`, `uncertainCount`, `uncertainAmount`, `notAttemptedCount` and
`notAttemptedAmount` aggregates. Nullable fields are omitted when they do not apply.

The public WebSocket payload intentionally excludes `error` and recipient-level `reconciliation`.
Those values can contain wallet errors, addresses and transaction mappings and remain available only
to administrative notification channels and logs. Front ends written against an older event shape
must use `outcome` and the safe aggregate fields instead of displaying `error`.

## Metrics and administration

When `metricsPort` is configured, Prometheus-compatible metrics are served from `/metrics` on that
listener. Administrative routes are under `/api/admin` on `adminPort`; they can change logging and
payment-processing state. Never publish the admin port through the public reverse proxy.

## Front ends and reverse proxies

A static front end should call only the public API. Put both behind HTTPS, restrict cross-origin access
to expected sites, rate-limit expensive miner/history routes, and cache only responses whose freshness
requirements permit it.

The community [btclinux/Miningcore.WebUI](https://github.com/btclinux/Miningcore.WebUI) is an optional
starting point, not part of this repository. It targets another Miningcore fork, has not received a code
push since December 2023, and GitHub does not currently detect a licence. Audit and adapt it before use.
