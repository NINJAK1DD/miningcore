# Share relays

The maintained share-relay documentation is now in
[`docs/share-relays.md`](docs/share-relays.md).

The current relay is an in-memory ZeroMQ PUB/SUB transport. It can distribute Stratum roles, but it
does not acknowledge or replay ordinary shares while a receiver is unavailable. Read the durability,
security, merged-mining and validation requirements in the maintained guide before enabling it.
