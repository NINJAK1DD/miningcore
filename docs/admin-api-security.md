# Administrative API security

Miningcore requires two independent controls for every `/api/admin` request:

1. the client address must pass `api.adminIpWhitelist`; and
2. the request must carry a bearer token from the
   `MININGCORE_ADMIN_API_TOKEN` environment variable.

Keep the admin listener on loopback or a trusted management network and enforce the same boundary
with a firewall. A bearer token sent over plain HTTP can be captured by anyone able to observe that
traffic, so enable API TLS before using the listener across an untrusted network. Do not expose the
token to a browser, public WebUI, URL, configuration file, log or source-control repository.

## Generate and store a token

Generate exactly 32 unpredictable bytes encoded as 64 hexadecimal characters:

```console
sudo mkdir -p /etc/miningcore
sudo install -m 0600 -o root -g root /dev/null \
  /etc/miningcore/miningcore.env
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
```

Miningcore accepts exactly 64 ASCII hexadecimal characters (`0-9`, `a-f` or `A-F`). It rejects
shorter, longer, Unicode or punctuation-bearing values so the credential has an unambiguous format
across HTTP clients, proxies, shells and service managers. Uppercase and lowercase hexadecimal
forms are equivalent. Its authentication object retains only a SHA-256 digest, compares supplied
credentials in constant time, and clears Miningcore's managed-process copy after startup so child
processes do not inherit it. Like any environment secret, the original value can remain available
to the service manager or container runtime and privileged host/container administrators. If the
variable is missing or invalid, Miningcore starts the pools and public API
but returns `503 Service Unavailable` from every administrative route. This fail-closed state is
reported at startup without logging the token.

### systemd

The packaged service reads the optional `/etc/miningcore/miningcore.env` file. For an existing custom
unit, retain directory traversal for the service group, then add a drop-in. Only the environment
file itself should be restricted to `root:root` mode `0600`:

```console
sudo chown root:miningcore /etc/miningcore
sudo chmod 0750 /etc/miningcore
sudo chown root:root /etc/miningcore/miningcore.env
sudo chmod 0600 /etc/miningcore/miningcore.env
```

Do not change `/etc/miningcore` to `root:root` mode `0750`: that prevents the `miningcore` service
account from traversing the directory to read `config.json`.

Add the environment file to a custom unit with:

```console
sudo systemctl edit miningcore
```

```ini
[Service]
EnvironmentFile=/etc/miningcore/miningcore.env
```

Then reload and restart:

```console
sudo systemctl daemon-reload
sudo systemctl restart miningcore
sudo journalctl -u miningcore -n 50 --no-pager
```

### Docker

Pass the same root-readable file without copying it into an image or configuration volume:

```console
sudo docker run --env-file /etc/miningcore/miningcore.env ...
```

Docker administrators can inspect container environment variables. Treat access to the Docker
daemon as root-equivalent and never commit an environment file to source control.

## Call the API

Read the root-owned token only for the command that needs it:

```console
sudo sh -c '
  . /etc/miningcore/miningcore.env
  printf "Authorization: Bearer %s\n" "$MININGCORE_ADMIN_API_TOKEN" |
    curl --fail --header @- \
      http://127.0.0.1:4001/api/admin/stats/gc
'
```

Reading the header from standard input keeps the bearer value out of `curl`'s process arguments.
To replace a miner's settings, send the settings object directly (replace the example pool and
address):

```console
sudo sh -c '
  . /etc/miningcore/miningcore.env
  printf "Authorization: Bearer %s\n" "$MININGCORE_ADMIN_API_TOKEN" |
    curl --fail --request PUT --header @- \
      --header "Content-Type: application/json" \
      --data "{\"paymentThreshold\":0.1}" \
      http://127.0.0.1:4001/api/admin/pools/btc1-solo/miners/REPLACE_WITH_MINER_ADDRESS/settings
'
```

Use the configured admin port. When `adminPort` is omitted, the route remains on the public port for
listener compatibility but still requires both authentication controls. Missing, malformed and
incorrect credentials return `401 Unauthorized`; a client outside the IP whitelist receives `403
Forbidden`; a protected route on the wrong dedicated listener receives `404 Not Found`.

Administrative requests remain subject to the public API rate limiter before Miningcore evaluates
the admin IP whitelist and bearer token. This limits rejection and log amplification from untrusted
sources, especially when the admin route shares the public listener. Loopback is exempt by default.
If trusted remote automation needs a rate-limit exemption, add only its narrowly scoped source
address to `api.rateLimiting.ipWhitelist` as well as `api.adminIpWhitelist`; authentication and the
admin whitelist remain mandatory. The rate-limit IP whitelist bypasses API throttling globally,
not only for administrative requests, so restrict its entries to trusted fixed addresses.

Admin IP-whitelist rejections have a separate fixed-size, monotonic log limiter. The first
rejection is written at `Info`; intervening entries are counted, and the next permitted
informational entry (at most one per minute) includes the suppressed total. Metrics whitelist
logging has an independent limiter, so a metrics flood cannot hide the first administrative
rejection. Per-request details remain available at `Debug`, but enabling that level during hostile
traffic can substantially increase log volume. A summary is emitted only when another rejection
arrives after the interval; it can therefore be delayed, and the address on it belongs to the
current request rather than the possibly different suppressed sources. All rejected requests
continue to return `403 Forbidden`, including when API rate limiting is disabled. This protects
normal logs from amplification but does not throttle requests; keep the dedicated listener, source
whitelist and firewall boundary in place.

Prometheus counter `miningcore_api_ip_whitelist_rejections_total` increments for every source-IP
whitelist rejection even when its informational log entry is suppressed. Alert on its
`route_family` label to distinguish `admin` and `metrics` boundaries. The label is deliberately
limited to the fixed values `admin`, `metrics` or `other`; it never includes a client address or
request path, so hostile traffic cannot create attacker-controlled metric cardinality.

Use the increase over an operationally appropriate window as an alert input, then choose a
threshold that excludes expected maintenance probes:

```promql
sum by (route_family) (
  increase(miningcore_api_ip_whitelist_rejections_total[5m])
)
```

Administrative routes intentionally emit no cross-origin resource sharing (CORS) headers. Public
front ends must call only public routes. If users need to change miner settings, implement a trusted
server-side service with its own user authentication and authorization; that service may call the
authenticated admin `PUT` endpoint. Never place the Miningcore admin token in JavaScript or send it
to a browser.

Every administrative response produced by the API pipeline sends
`Cross-Origin-Resource-Policy: same-origin`, including wrong-listener, rate-limit, whitelist,
authentication and credential-unavailable rejections. Protected pipeline responses also send
`Cache-Control: no-store` and `X-Content-Type-Options: nosniff` to discourage storage and MIME
sniffing. These headers cannot cover protocol errors that Kestrel rejects before a request enters
the API pipeline.

The resource policy blocks eligible cross-origin no-CORS subresource use of the response. It does
not generally prohibit cross-origin navigation or iframe embedding; use an appropriate framing
policy such as CSP `frame-ancestors` if that is a deployment requirement. CORS and these response
headers govern browser response use only: they do not prevent requests from being transmitted and
never replace the dedicated listener, IP whitelist, bearer token, TLS or firewall boundary.

## Mutating routes

State-changing requests use explicit non-GET verbs:

| Method | Route | Purpose |
| --- | --- | --- |
| `PUT` | `/api/admin/logging/level/{level}` | Set the runtime log level |
| `PUT` | `/api/admin/payment/processing/enable` | Enable payments for enabled pools |
| `PUT` | `/api/admin/payment/processing/disable` | Disable payments for enabled pools |
| `PUT` | `/api/admin/payment/processing/{poolId}/enable` | Enable one pool's payments |
| `PUT` | `/api/admin/payment/processing/{poolId}/disable` | Disable one pool's payments |
| `PUT` | `/api/admin/pools/{poolId}/miners/{address}/settings` | Replace miner settings |
| `POST` | `/api/admin/forcegc` | Request an immediate garbage collection |

Payment-processing toggles are validated atomically before any pool is changed. Miningcore rejects
disabling payment processing while an enabled PPS pool is accepting shares, and enabling PPS when
the cluster-level payout and retention scheduler was not active at startup. Stop Miningcore and make
PPS contract changes through a reviewed configuration and controlled restart. If a bulk disable is
rejected, non-PPS pools remain individually controllable through their per-pool routes; see the
[PPS operator guide](pps.md).

Read-only administrative routes remain `GET`, but require the same bearer token and IP whitelist.
The former public `POST /api/pools/{poolId}/miners/{address}/settings` route has been removed because
a caller-supplied recent mining IP address is not adequate authorization. No unauthenticated
`410 Gone` tombstone is registered by design: retaining the public route solely to announce its
removal would preserve an unnecessary attack surface and permanent cleanup obligation.

## Rotate or revoke

Generate a replacement value, replace the environment file, restart or recreate Miningcore, and
test one read-only route. Once the new process starts, the old token is immediately invalid. If
compromise is suspected, also review admin-request logs, payment-processing state, miner settings
and firewall/reverse-proxy exposure before restoring administrative access.

For systemd, replace the file and restart the service:

```console
sudo install -m 0600 -o root -g root /dev/null \
  /etc/miningcore/miningcore.env.new
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env.new >/dev/null
unset token
sudo mv /etc/miningcore/miningcore.env.new /etc/miningcore/miningcore.env
sudo systemctl restart miningcore
```

For Docker, `docker restart` is insufficient because it preserves the environment captured when
the container was created. Replace the file, remove the old container, and repeat the complete
version-pinned `docker run` command from the installation guide, including every network, port and
volume option:

```console
sudo docker rm -f miningcore
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  --env-file /etc/miningcore/miningcore.env \
  --network miningcore \
  -p 4000:4000 \
  -p 127.0.0.1:4001:4001 \
  -p 127.0.0.1:4002:4002 \
  -p 3032:3032 \
  -v /etc/miningcore/config.json:/etc/miningcore/config.json:ro \
  -v /var/lib/miningcore:/var/lib/miningcore \
  "ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}"
```

Generate and install the replacement file using the commands at the start of this guide before
removing the running container. Adjust the example to match the original deployment exactly.
