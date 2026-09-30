namespace Overwatch.Market;

public enum PriceDecision
{
    Ignored,
    Duplicate,
    Stored
}

public sealed record PriceRow(
    string ServerKey,
    long ItemId,
    string Source,
    int LotQuantity,
    long? Total,
    long? UnitPrice,
    string ConnectionId,
    DateTimeOffset UpdatedAt);

public sealed class PriceBook
{
    private readonly Dictionary<string, ServerShelf> _servers = new(StringComparer.Ordinal);

    public PriceDecision TrySetAverage(string serverName, long itemId, long average, string connectionId, out PriceRow? row)
    {
        row = null;
        var serverKey = ServerNames.Normalize(serverName);
        if (serverKey.Length == 0 || itemId < 0 || average <= 0)
            return PriceDecision.Ignored;

        var server = Shelf(serverKey);
        if (!server.Seen.Add($"avg|{itemId}|{average}"))
            return PriceDecision.Duplicate;

        row = new PriceRow(serverKey, itemId, "average", 0, null, average, connectionId, DateTimeOffset.UtcNow);
        server.Values[(itemId, "average", 0)] = row;
        return PriceDecision.Stored;
    }

    public PriceDecision TrySetLot(
        string serverName,
        long itemId,
        int quantity,
        long total,
        string connectionId,
        out PriceRow? row)
    {
        row = null;
        var serverKey = ServerNames.Normalize(serverName);
        if (serverKey.Length == 0 || itemId < 0 || quantity <= 0 || total == 0)
            return PriceDecision.Ignored;

        var server = Shelf(serverKey);
        if (!server.Seen.Add($"lot|{itemId}|{quantity}|{total}"))
            return PriceDecision.Duplicate;

        long? unit = total % quantity == 0 ? total / quantity : null;
        row = new PriceRow(serverKey, itemId, "lot", quantity, total, unit, connectionId, DateTimeOffset.UtcNow);
        server.Values[(itemId, "lot", quantity)] = row;
        return PriceDecision.Stored;
    }

    public long? BestUnitPrice(string serverName, long itemId)
    {
        var serverKey = ServerNames.Normalize(serverName);
        if (!_servers.TryGetValue(serverKey, out var server))
            return null;

        long? best = null;
        foreach (var pair in server.Values)
        {
            if (pair.Key.ItemId != itemId || pair.Key.Source != "lot" || pair.Value.UnitPrice is not long unit)
                continue;
            if (best is null || unit < best)
                best = unit;
        }

        return best;
    }

    public long? AveragePrice(string serverName, long itemId)
    {
        var serverKey = ServerNames.Normalize(serverName);
        if (!_servers.TryGetValue(serverKey, out var server))
            return null;
        return server.Values.TryGetValue((itemId, "average", 0), out var row) ? row.UnitPrice : null;
    }

    private ServerShelf Shelf(string serverKey)
    {
        if (!_servers.TryGetValue(serverKey, out var server))
        {
            server = new ServerShelf();
            _servers[serverKey] = server;
        }

        return server;
    }

    private sealed class ServerShelf
    {
        public Dictionary<(long ItemId, string Source, int Quantity), PriceRow> Values { get; } = new();

        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }
}
