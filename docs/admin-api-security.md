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
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
sudo chown root:root /etc/miningcore/miningcore.env
sudo chmod 0600 /etc/miningcore/miningcore.env
```

Miningcore accepts exactly 64 ASCII hexadecimal characters (`0-9`, `a-f` or `A-F`). It rejects
shorter, longer, Unicode or punctuation-bearing values so the credential has an unambiguous format
across HTTP clients, proxies, shells and service managers. Its authentication object retains only a
SHA-256 digest and compares supplied credentials in constant time. Like any environment secret, the
original value remains available to the service process and privileged host/container
administrators. If the variable is missing or invalid, Miningcore starts the pools and public API
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

Administrative routes intentionally emit no cross-origin resource sharing (CORS) headers. Public
front ends must call only public routes. If users need to change miner settings, implement a trusted
server-side service with its own user authentication and authorization; that service may call the
authenticated admin `PUT` endpoint. Never place the Miningcore admin token in JavaScript or send it
to a browser.

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

Read-only administrative routes remain `GET`, but require the same bearer token and IP whitelist.
The former public `POST /api/pools/{poolId}/miners/{address}/settings` route has been removed because
a caller-supplied recent mining IP address is not adequate authorization.

## Rotate or revoke

Generate a replacement value, atomically replace the environment file, restart Miningcore, and test
one read-only route. A restart immediately invalidates the old token. If compromise is suspected,
also review admin-request logs, payment-processing state, miner settings and firewall/reverse-proxy
exposure before restoring administrative access.
