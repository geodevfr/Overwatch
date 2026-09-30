using System.Net;
using System.Net.NetworkInformation;

namespace Overwatch.Ui;

public sealed record RemoteConnection(string Address, int Port);

public static class ConnectionSurvey
{
    public static IReadOnlyList<RemoteConnection> EstablishedRemotes()
    {
        TcpConnectionInformation[] connections;
        try
        {
            connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<RemoteConnection>();
        }

        var found = new List<RemoteConnection>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            if (connection.State != TcpState.Established)
                continue;
            var address = connection.RemoteEndPoint.Address;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                continue;
            var text = address.ToString();
            var key = text + ":" + connection.RemoteEndPoint.Port.ToString();
            if (!seen.Add(key))
                continue;
            found.Add(new RemoteConnection(text, connection.RemoteEndPoint.Port));
            if (found.Count == 40)
                break;
        }

        return found;
    }
}
