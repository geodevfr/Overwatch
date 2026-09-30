namespace GameDiag.Market;

public static class WindowCorrelator
{
    public static Dictionary<string, string> Match(
        IReadOnlyList<(string Id, string Name)> people,
        IReadOnlyList<string> titles)
    {
        var pairs = new List<(string Id, string Title)>();
        foreach (var person in people)
        {
            var name = person.Name.Trim();
            if (name.Length == 0)
                continue;
            foreach (var title in titles)
            {
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                if (ContainsName(title, name))
                    pairs.Add((person.Id, title));
            }
        }

        var titlesById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var idsByTitle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (!titlesById.TryGetValue(pair.Id, out var titleSet))
            {
                titleSet = new HashSet<string>(StringComparer.Ordinal);
                titlesById[pair.Id] = titleSet;
            }

            titleSet.Add(pair.Title);
            if (!idsByTitle.TryGetValue(pair.Title, out var idSet))
            {
                idSet = new HashSet<string>(StringComparer.Ordinal);
                idsByTitle[pair.Title] = idSet;
            }

            idSet.Add(pair.Id);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, titleSet) in titlesById)
        {
            if (titleSet.Count != 1)
                continue;
            var title = titleSet.First();
            if (idsByTitle[title].Count != 1)
                continue;
            result[id] = title;
        }

        return result;
    }

    private static bool ContainsName(string title, string name)
    {
        var index = 0;
        while (index < title.Length)
        {
            var found = title.IndexOf(name, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            var before = found == 0 || !char.IsLetter(title[found - 1]);
            var afterIndex = found + name.Length;
            var after = afterIndex >= title.Length || !char.IsLetter(title[afterIndex]);
            if (before && after)
                return true;
            index = found + 1;
        }

        return false;
    }
}
