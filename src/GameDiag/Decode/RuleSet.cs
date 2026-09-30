namespace GameDiag.Decode;

public sealed class RuleSet
{
    public RuleSet(IReadOnlyList<CompiledRule> rules)
    {
        Rules = rules;
    }

    public IReadOnlyList<CompiledRule> Rules { get; }

    public MatchOutcome Match(ReadOnlySpan<byte> window, Direction direction, ConversationState conversation)
    {
        var needMore = false;
        int? contextSkip = null;

        foreach (var rule in Rules)
        {
            if (!rule.Accepts(direction))
                continue;

            var header = rule.Header;
            if (window.Length < header.Length)
            {
                if (header.AsSpan().StartsWith(window))
                    needMore = true;
                continue;
            }

            if (!window.StartsWith(header))
                continue;

            if (!rule.TryGetTotalLength(window, out var total, out var incomplete))
            {
                if (incomplete)
                    needMore = true;
                continue;
            }

            if (window.Length < total)
            {
                needMore = true;
                continue;
            }

            if (!conversation.Satisfies(rule))
            {
                contextSkip ??= total;
                continue;
            }

            return MatchOutcome.Frame(rule, total);
        }

        if (contextSkip is int skipLength)
            return MatchOutcome.Skip(skipLength);
        return needMore ? MatchOutcome.NeedMore() : MatchOutcome.NoMatch();
    }
}

public sealed class ConversationState
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public bool Satisfies(CompiledRule rule)
    {
        foreach (var required in rule.Requires)
        {
            if (!_flags.Contains(required))
                return false;
        }

        foreach (var forbidden in rule.Forbids)
        {
            if (_flags.Contains(forbidden))
                return false;
        }

        return true;
    }

    public void Apply(CompiledRule rule)
    {
        foreach (var flag in rule.Sets)
            _flags.Add(flag);
    }

    public void Clear() => _flags.Clear();
}
