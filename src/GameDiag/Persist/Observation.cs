namespace GameDiag.Persist;

public sealed record Observation(
    DateTimeOffset ObservedAt,
    string ConnectionId,
    string Listener,
    string Direction,
    string RuleId,
    int Length,
    string FieldsJson,
    string PayloadHex);
