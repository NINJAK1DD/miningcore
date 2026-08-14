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

    internal static void ConfigureGracefulClose(Socket socket)
    {
        if(socket == null)
            return;

        try
        {
            // Accepted sockets begin abortive so a hard process termination does not strand an
            // exclusive listener in TIME_WAIT. A clean EOF or bounded host shutdown deliberately
            // disarms that default before disposal so queued response bytes can drain with FIN.
            socket.LingerState = new LingerOption(false, 0);
        }
        catch(Exception ex) when(ex is SocketException or
            ObjectDisposedException)
        {
            // The connection may have completed between classification and cleanup.
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
