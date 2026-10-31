# Administrative notification activation

`notifications.admin.enabled` is the authoritative switch for administrative
notifications. The older `notifications.enabled` field is deprecated and ignored.
Its value does not enable or suppress delivery. Set `admin.enabled` explicitly
when migrating an existing configuration; do not rely on the old field.
If the old field is `true` while `admin.enabled` is `false`, startup logs a
warning about the mismatch.

| `notifications` | `admin.enabled` | `admin.emailAddress` | `pushover.enabled` | Normal host result |
| --- | --- | --- | --- | --- |
| Omitted or `{}` | Omitted | Omitted | Omitted | Inactive; no subscriptions or critical delivery |
| Present; top-level `enabled` either value | Omitted or `false` | Any | Any | Inactive; no provider validation or delivery |
| Present; top-level `enabled` either value | `true` | Null, empty or whitespace | Omitted or `false` | Startup error: no transport |
| Present; top-level `enabled` either value | `true` | Nonblank | Omitted or `false` | Email only; `notifications.email` required |
| Present; top-level `enabled` either value | `true` | Null, empty or whitespace | `true` | Pushover only; user and token required |
| Present; top-level `enabled` either value | `true` | Nonblank | `true` | Both transports; both providers required |

An empty `admin` or `pushover` object is equivalent to omitted flags. A
nonblank `admin.emailAddress` opts into email even if `notifications.email` is
absent; normal startup then reports the missing provider. To use Pushover only,
remove or blank that address. When email is active, SMTP host, port,
`tlsMode`, sender name and sender address must be usable. The sender name must
not be whitespace, and sender and recipient must be plain email addresses.
SMTP credentials
must be both present or both absent under the
[verified TLS policy](configuration.md#smtp-transport-migration). When Pushover is
active, user and token must be nonblank. Inactive providers are not validated.
The same null, empty and whitespace rule governs ordinary and critical email.

An active normal host subscribes to administrative, block-found and payment
events. `notifyBlockFound` controls block-found alerts and
`notifyPaymentSuccess` controls successful payment alerts. Payment failures and
uncertain outcomes remain eligible. Critical persistence alerts ignore these
event filters, but respect `admin.enabled` and the transport choices. Email
and Pushover attempts are independent: failure of one does not prevent an
attempt through the other. Ordinary delivery starts both selected transports
independently and limits each attempt to 15 seconds; a stalled SMTP server
does not hold up Pushover. Each transport processes events in order in its own
queue, so later Pushover alerts also proceed while email is stalled. A timeout
bounds the wait, but does not prove the remote service rejected the message;
check provider and reconciliation evidence before retrying a payout alert.
Pushover requests share a two-request gate within a Miningcore process across
hosted and critical senders,
with one slot reserved for critical alerts when ordinary traffic is active.
No automatic retry is made after an uncertain API outcome. Pushover titles and
messages are kept within its
250 and 1024 character limits, preserving the incident summary and trailing
operator instruction when detail is truncated. An HTTP response counts as
accepted only when Pushover confirms `status: 1`, which means the provider
queued the request rather than a person reading it. Uncertain-payout Pushover
alerts point to retained reconciliation evidence and wallet history even if
email is not delivered. If email is configured, the alert mentions its
per-recipient summary only conditionally on receipt. Host shutdown cancellation is not logged as a delivery
error; a channel timeout is logged with its transport name. An active critical
sender with no usable transport
reports an error to its caller instead of reporting successful delivery.

Normal startup validates this policy before notification readiness and
subscriptions. The hosted service and the separately registered critical
sender are distinct instances. The critical sender can be resolved lazily
without starting the host; it checks each selected provider at dispatch and
reports failures without preventing another selected transport. Persistence
and recovery failure handlers catch notification errors after retaining the
original failure and fail-stop status.

The `-rs` recovery importer deliberately excludes the live notification
section from its sanitized configuration. It neither validates notification
providers nor sends configured administrative alerts; inspect its exit status,
logs and fatal-state files. Live journal and candidate persistence failures
use the normal cluster configuration and the unhosted critical sender above.

For an existing configuration with `notifications.enabled: true` and
`admin.enabled: false`, enable `admin.enabled` only after configuring at least
one transport. For one with top-level `enabled: false` and `admin.enabled:
true`, alerts remain active; verify the recipient and selected provider before
upgrading. Remove the deprecated top-level field after review.
