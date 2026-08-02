using Miningcore.Configuration;
using Miningcore.Mining;

if(args.Length != 4 || !string.Equals(args[0], "hold", StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "usage: Miningcore.Tests.ProcessHost hold <recovery-file> <state-directory> <ready-file>");
    return 64;
}

var config = new ClusterConfig
{
    ShareRecoveryFile = args[1],
    ShareRecoveryStateDirectory = args[2],
};
using var ownership = new ShareRecoveryPathOwnership(config);
ownership.Acquire();
await File.WriteAllTextAsync(args[3], Environment.ProcessId.ToString());

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
}
catch(OperationCanceledException)
{
}

return 0;
