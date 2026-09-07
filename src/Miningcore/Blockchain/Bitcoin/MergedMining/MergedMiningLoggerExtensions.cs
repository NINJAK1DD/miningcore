using Miningcore.Rpc;
using NLog;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class MergedMiningLoggerExtensions
{
    public static void Error(this ILogger logger, Exception exception, Func<string> messageFactory)
    {
        if(logger.IsErrorEnabled)
            RpcConsumerDiagnostics.Write(logger, NLog.LogLevel.Error, "MergedMiningLoggerExtensions.Error", failure: exception);
    }
}
