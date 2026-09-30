using System.Net.Sockets;

namespace Overwatch.Proxy;

public static class SocketSetup
{
    public static void Configure(Socket socket)
    {
        socket.NoDelay = true;
        if (!socket.NoDelay)
            throw new InvalidOperationException("TCP_NODELAY n'a pas pu être activé.");
    }
}
