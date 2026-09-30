using System.Text;

namespace GameDiag.Market;

public static class ServerNames
{
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
        {
            if (char.IsWhiteSpace(character))
                continue;
            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
