using Overwatch.Decode;
using Overwatch.Market;
using Overwatch.Persist;

namespace Overwatch.Ui;

public static class BoardComposer
{
    public static object Compose(RuleSet rules, IReadOnlyList<PriceRow> prices, IReadOnlyList<SessionRow> sessions)
    {
        var hotelReady = false;
        var mapReady = false;
        var monstersReady = false;
        foreach (var rule in rules.Rules)
        {
            if (rule.Kind is ObservationKind.AveragePrices or ObservationKind.SaleLots)
                hotelReady = true;
            if (rule.Kind == ObservationKind.Position)
                mapReady = true;
            if (rule.Kind == ObservationKind.Combat)
                monstersReady = true;
        }

        return new
        {
            detection = new
            {
                hotel = hotelReady ? "ready" : "unknown",
                map = mapReady ? "ready" : "unknown",
                monsters = monstersReady ? "ready" : "unknown"
            },
            prices = prices.Select(row => new
            {
                server = row.ServerKey,
                itemId = row.ItemId,
                source = row.Source,
                quantity = row.LotQuantity,
                total = row.Total,
                unit = row.UnitPrice
            }),
            places = sessions
                .Where(row => row.Position is not null || row.CharacterName is not null || row.ServerKey is not null)
                .Select(row => new
                {
                    connectionId = row.ConnectionId,
                    character = row.CharacterName,
                    server = row.ServerKey,
                    position = row.Position,
                    title = row.WindowTitle
                }),
            encounters = sessions
                .Where(row => !string.IsNullOrWhiteSpace(row.Combat))
                .Select(row => new
                {
                    connectionId = row.ConnectionId,
                    character = row.CharacterName,
                    combat = row.Combat
                })
        };
    }
}
