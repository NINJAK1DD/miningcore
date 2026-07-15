using NLog;

namespace Miningcore.Blockchain.Bitcoin.MergedMining;

internal static class MergedMiningLoggerExtensions
{
    public static void Error(this ILogger logger, Exception exception, Func<string> messageFactory)
    {
        if(logger.IsErrorEnabled)
            logger.Error(exception, messageFactory());
    }
}
