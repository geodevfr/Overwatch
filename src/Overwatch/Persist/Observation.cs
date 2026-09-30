namespace Overwatch.Persist;

public sealed record Observation(
    DateTimeOffset ObservedAt,
    string ConnectionId,
    string Listener,
    string Direction,
    string RuleId,
    int Length,
    string FieldsJson,
    string PayloadHex);

public sealed record SessionRow(
    string ConnectionId,
    string? ServerKey,
    string? CharacterName,
    string? WindowTitle,
    string? Position,
    string? Combat,
    DateTimeOffset UpdatedAt);

public sealed class StoredWork
{
    public Observation? Observation { get; init; }

    public IReadOnlyList<Overwatch.Market.PriceRow>? Prices { get; init; }

    public SessionRow? Session { get; init; }
}
