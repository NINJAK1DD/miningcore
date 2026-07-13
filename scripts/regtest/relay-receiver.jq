.clusterName = "relay-receiver"
| .logging.logBaseDirectory = "/tmp/miningcore-relay-receiver"
| .api.enabled = false
| .shareRelay = null
| .shareRelays = [{
    url: "tcp://127.0.0.1:5570"
  }]
| .pools[0].enableInternalStratum = false
| .pools[1].enableInternalStratum = false
