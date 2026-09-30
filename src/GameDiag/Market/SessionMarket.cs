using System.Globalization;
using GameDiag.Decode;
using GameDiag.Persist;

namespace GameDiag.Market;

public sealed class ClientSession
{
    private const int PendingCap = 4096;
    private readonly List<PendingAverage> _pendingAverages = new();
    private readonly List<PendingLot> _pendingLots = new();

    public ClientSession(string connectionId)
    {
        ConnectionId = connectionId;
    }

    public string ConnectionId { get; }

    public string? ServerRaw { get; private set; }

    public string? ServerKey { get; private set; }

    public string? CharacterName { get; private set; }

    public string? WindowTitle { get; private set; }

    public string? Position { get; private set; }

    public string? Combat { get; private set; }

    public bool SetServer(string raw)
    {
        var key = ServerNames.Normalize(raw);
        if (key.Length == 0 || key == ServerKey)
            return false;
        ServerRaw = raw.Trim();
        ServerKey = key;
        return true;
    }

    public bool SetCharacter(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed == CharacterName)
            return false;
        CharacterName = trimmed;
        return true;
    }

    public bool SetWindowTitle(string? title)
    {
        if (title == WindowTitle)
            return false;
        WindowTitle = title;
        return true;
    }

    public bool SetPosition(string value)
    {
        if (value == Position)
            return false;
        Position = value;
        return true;
    }

    public bool SetCombat(string value)
    {
        if (value == Combat)
            return false;
        Combat = value;
        return true;
    }

    public bool HoldAverage(long itemId, long average)
    {
        if (PendingCount >= PendingCap)
            return false;
        _pendingAverages.Add(new PendingAverage(itemId, average));
        return true;
    }

    public bool HoldLot(long itemId, int quantity, long total)
    {
        if (PendingCount >= PendingCap)
            return false;
        _pendingLots.Add(new PendingLot(itemId, quantity, total));
        return true;
    }

    public void DrainPending(List<PendingAverage> averages, List<PendingLot> lots)
    {
        averages.AddRange(_pendingAverages);
        lots.AddRange(_pendingLots);
        _pendingAverages.Clear();
        _pendingLots.Clear();
    }

    private int PendingCount => _pendingAverages.Count + _pendingLots.Count;

    public readonly record struct PendingAverage(long ItemId, long Average);

    public readonly record struct PendingLot(long ItemId, int Quantity, long Total);
}

public sealed class SessionMarket
{
    private readonly PriceBook _book = new();
    private readonly Dictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);

    public PriceBook Prices => _book;

    public ClientSession Session(string connectionId)
    {
        if (!_sessions.TryGetValue(connectionId, out var session))
        {
            session = new ClientSession(connectionId);
            _sessions[connectionId] = session;
        }

        return session;
    }

    public IReadOnlyDictionary<string, ClientSession> Sessions => _sessions;

    public MarketUpdate Accept(string connectionId, CompiledRule rule, ParsedFrame frame)
    {
        var session = Session(connectionId);
        var prices = new List<PriceRow>();
        var sessionChanged = false;

        switch (rule.Kind)
        {
            case ObservationKind.ServerName:
                sessionChanged = AcceptServer(session, rule, frame, prices);
                break;
            case ObservationKind.CharacterName:
                sessionChanged = AcceptCharacter(session, rule, frame);
                break;
            case ObservationKind.AveragePrices:
                AcceptAverages(session, rule, frame, prices);
                break;
            case ObservationKind.SaleLots:
                AcceptLots(session, rule, frame, prices);
                break;
            case ObservationKind.Position:
                sessionChanged = AcceptText(session, rule, frame, session.SetPosition);
                break;
            case ObservationKind.Combat:
                sessionChanged = AcceptText(session, rule, frame, session.SetCombat);
                break;
        }

        return new MarketUpdate(prices, sessionChanged ? Snapshot(session) : null);
    }

    public IReadOnlyList<SessionRow> CorrelateTitles(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0)
            return Array.Empty<SessionRow>();

        var people = new List<(string Id, string Name)>();
        foreach (var session in _sessions.Values)
        {
            if (!string.IsNullOrWhiteSpace(session.CharacterName))
                people.Add((session.ConnectionId, session.CharacterName));
        }

        var matches = WindowCorrelator.Match(people, titles);
        var changed = new List<SessionRow>();
        foreach (var session in _sessions.Values)
        {
            if (string.IsNullOrWhiteSpace(session.CharacterName))
                continue;
            var title = matches.TryGetValue(session.ConnectionId, out var found) ? found : null;
            if (!session.SetWindowTitle(title))
                continue;
            changed.Add(Snapshot(session));
        }

        return changed;
    }

    private bool AcceptServer(ClientSession session, CompiledRule rule, ParsedFrame frame, List<PriceRow> prices)
    {
        if (!frame.Fields.TryGetValue(rule.NameField, out var raw) || !session.SetServer(raw))
            return false;

        var averages = new List<ClientSession.PendingAverage>();
        var lots = new List<ClientSession.PendingLot>();
        session.DrainPending(averages, lots);
        foreach (var pending in averages)
            Keep(_book.TrySetAverage(session.ServerKey!, pending.ItemId, pending.Average, session.ConnectionId, out var row), row, prices);
        foreach (var pending in lots)
            Keep(_book.TrySetLot(session.ServerKey!, pending.ItemId, pending.Quantity, pending.Total, session.ConnectionId, out var row), row, prices);
        return true;
    }

    private static bool AcceptCharacter(ClientSession session, CompiledRule rule, ParsedFrame frame)
    {
        return frame.Fields.TryGetValue(rule.NameField, out var name) && session.SetCharacter(name);
    }

    private void AcceptAverages(ClientSession session, CompiledRule rule, ParsedFrame frame, List<PriceRow> prices)
    {
        if (frame.Entries is null)
            return;
        foreach (var entry in frame.Entries)
        {
            if (!TryLong(entry, rule.ItemField, out var itemId) || !TryLong(entry, rule.ValueField, out var average))
                continue;
            if (average <= 0)
                continue;
            if (session.ServerKey is null)
            {
                session.HoldAverage(itemId, average);
                continue;
            }

            Keep(_book.TrySetAverage(session.ServerKey, itemId, average, session.ConnectionId, out var row), row, prices);
        }
    }

    private void AcceptLots(ClientSession session, CompiledRule rule, ParsedFrame frame, List<PriceRow> prices)
    {
        if (!TryLong(frame.Fields, rule.ItemField, out var itemId))
            return;

        foreach (var lot in rule.Lots)
        {
            if (!TryLong(frame.Fields, lot.TotalField, out var total) || total == 0)
                continue;
            if (session.ServerKey is null)
            {
                session.HoldLot(itemId, lot.Quantity, total);
                continue;
            }

            Keep(_book.TrySetLot(session.ServerKey, itemId, lot.Quantity, total, session.ConnectionId, out var row), row, prices);
        }
    }

    private static bool AcceptText(ClientSession session, CompiledRule rule, ParsedFrame frame, Func<string, bool> assign)
    {
        foreach (var field in rule.Fields)
        {
            if (!frame.Fields.ContainsKey(field.Name))
                return false;
        }

        var text = string.Join(';', rule.Fields.Select(field => $"{field.Name}={frame.Fields[field.Name]}"));
        return assign(text);
    }

    private static void Keep(PriceDecision decision, PriceRow? row, List<PriceRow> prices)
    {
        if (decision == PriceDecision.Stored && row is not null)
            prices.Add(row);
    }

    private static SessionRow Snapshot(ClientSession session)
    {
        return new SessionRow(
            session.ConnectionId,
            session.ServerKey,
            session.CharacterName,
            session.WindowTitle,
            session.Position,
            session.Combat,
            DateTimeOffset.UtcNow);
    }

    private static bool TryLong(IReadOnlyDictionary<string, string> fields, string name, out long value)
    {
        value = 0;
        return fields.TryGetValue(name, out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

public readonly record struct MarketUpdate(IReadOnlyList<PriceRow> Prices, SessionRow? Session);
