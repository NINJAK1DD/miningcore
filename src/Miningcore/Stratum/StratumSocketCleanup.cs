using System.Net.Sockets;

namespace Miningcore.Stratum;

internal static class StratumSocketCleanup
{
    internal static void ConfigureAbortiveClose(Socket socket)
    {
        if(socket == null)
            return;

        try
        {
            // Server-initiated disconnects are terminal. Abortive close prevents the local
            // Stratum endpoint from being stranded in TIME_WAIT now that listener sockets
            // are deliberately exclusive and do not use SO_REUSEADDR.
            socket.LingerState = new LingerOption(true, 0);
        }
        catch(Exception ex) when(ex is SocketException or
            ObjectDisposedException)
        {
            // The connection may have completed between cancellation and this cleanup.
        }
    }

    internal static void CloseAbortively(Socket socket)
    {
        if(socket == null)
            return;

        ConfigureAbortiveClose(socket);
        socket.Dispose();
    }
}
