.clusterName = "relay-sender"
| .logging.logBaseDirectory = "/tmp/miningcore-relay-sender"
| .persistence = null
| .paymentProcessing.enabled = false
| .api.enabled = false
| .shareRelay = {
    publishUrl: "tcp://127.0.0.1:5570",
    connect: false
  }
| .shareRelays = null
| .pools[0].mergedMining.acceptNonDurableBlockDelivery = true
| .pools[0].enableInternalStratum = true
| .pools[1].enableInternalStratum = true
