.clusterName = "mweb-live"
| .logging.logBaseDirectory = "/tmp/miningcore-mweb"
| .paymentProcessing.enabled = false
| .api.enabled = false
| .shareRelay = {
    publishUrl: "tcp://127.0.0.1:5560",
    connect: false
  }
| .shareRelays = null
| .pools[0].address = $parentAddress
| .pools[0].ports = {
    "3335": {
      name: "MWEB merged-mining live test",
      listenAddress: "0.0.0.0",
      difficulty: 0.0001,
      tls: false
    }
  }
| .pools[0].daemons = [{
    host: "127.0.0.1",
    port: 20342,
    user: "litecoin",
    password: "local-test-password"
  }]
| .pools[1].ports = {
    "4446": {
      name: "DOGE auxiliary live test",
      listenAddress: "127.0.0.1",
      difficulty: 0.0001,
      tls: false
    }
  }
