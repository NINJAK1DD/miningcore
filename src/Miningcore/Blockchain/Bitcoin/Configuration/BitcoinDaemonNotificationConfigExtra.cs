namespace Miningcore.Blockchain.Bitcoin.Configuration;

public class BitcoinDaemonNotificationConfigExtra
{
    /// <summary>
    /// Address of ZeroMQ block notify socket
    /// Should match the value of -zmqpubhashblock daemon start parameter
    /// </summary>
    public string ZmqBlockNotifySocket { get; set; }

    /// <summary>
    /// Optional ZeroMQ subscription topic. Defaults to hashblock when omitted.
    /// </summary>
    public string ZmqBlockNotifyTopic { get; set; }
}
