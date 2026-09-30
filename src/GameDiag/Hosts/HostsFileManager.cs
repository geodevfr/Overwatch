using System.Net;
using System.Text;
using GameDiag.Config;

namespace GameDiag.Hosts;

public enum HostsChange
{
    Unchanged,
    Installed,
    Removed
}

public sealed record HostsEntry(string Hostname, string Address);

/// <summary>
/// Maintient un bloc borné par des marqueurs dans le fichier hosts.
/// L'installation retire d'abord tout bloc existant : un processus tué
/// par SIGKILL laisse le bloc, et le lancement suivant le remplace.
/// </summary>
public sealed class HostsFileManager
{
    public const string BeginMarker = "# GAMEDIAG-BEGIN";
    public const string EndMarker = "# GAMEDIAG-END";

    private readonly object _gate = new();

    public HostsFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Chemin du fichier hosts vide.", nameof(path));
        Path = path;
    }

    public string Path { get; }

    public static string DefaultPath =>
        OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts")
            : "/etc/hosts";

    public static bool IsSafeHostname(string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Length > 253)
            return false;
        if (hostname.StartsWith('-') || hostname.StartsWith('.') || hostname.EndsWith('-') || hostname.EndsWith('.'))
            return false;
        if (hostname.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var character in hostname)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '-'))
                return false;
        }

        return true;
    }

    public static HostsEntry Normalize(string hostname, string address)
    {
        if (!IsSafeHostname(hostname))
            throw new ConfigException($"Nom d'hôte refusé : {hostname}");
        if (!IPAddress.TryParse(address, out var ip))
            throw new ConfigException($"Adresse IP refusée pour le fichier hosts : {address}");
        return new HostsEntry(hostname, ip.ToString());
    }

    public bool ContainsManagedBlock()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
                return false;
            return File.ReadAllText(Path).Contains(BeginMarker, StringComparison.Ordinal);
        }
    }

    public HostsChange Install(IReadOnlyList<HostsEntry> entries)
    {
        var normalized = NormalizeAll(entries);
        lock (_gate)
        {
            var original = File.Exists(Path) ? File.ReadAllText(Path) : "";
            var next = Compose(original, normalized);
            if (next == original)
                return HostsChange.Unchanged;
            AtomicWrite(next);
            return normalized.Count == 0 ? HostsChange.Removed : HostsChange.Installed;
        }
    }

    public HostsChange Remove()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
                return HostsChange.Unchanged;
            var original = File.ReadAllText(Path);
            var stripped = RemoveManagedBlock(original);
            if (stripped == original)
                return HostsChange.Unchanged;
            AtomicWrite(stripped);
            return HostsChange.Removed;
        }
    }

    public static string RemoveManagedBlock(string content)
    {
        var begin = IndexOfLineMarker(content, BeginMarker);
        if (begin < 0)
            return content;

        var end = IndexOfLineMarker(content, EndMarker, begin + BeginMarker.Length);
        int cutEnd;
        if (end < 0)
        {
            cutEnd = content.Length;
        }
        else
        {
            cutEnd = end + EndMarker.Length;
            if (cutEnd < content.Length && content[cutEnd] == '\r')
                cutEnd++;
            if (cutEnd < content.Length && content[cutEnd] == '\n')
                cutEnd++;
        }

        return content.Remove(begin, cutEnd - begin);
    }

    private static List<HostsEntry> NormalizeAll(IReadOnlyList<HostsEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<HostsEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var safe = Normalize(entry.Hostname, entry.Address);
            if (!seen.Add(safe.Hostname))
                throw new ConfigException($"Nom d'hôte en double dans le bloc hosts : {safe.Hostname}");
            normalized.Add(safe);
        }

        return normalized;
    }

    private static string Compose(string original, IReadOnlyList<HostsEntry> entries)
    {
        var stripped = RemoveManagedBlock(original);
        if (entries.Count == 0)
            return stripped;

        var newline = stripped.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var builder = new StringBuilder(stripped.Length + 128);
        builder.Append(stripped);
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.Append(newline);
        builder.Append(BeginMarker).Append(newline);
        builder.Append("# Bloc géré par GameDiag. Retiré à l'arrêt du processus.").Append(newline);
        foreach (var entry in entries)
            builder.Append(entry.Address).Append(' ').Append(entry.Hostname).Append(newline);
        builder.Append(EndMarker).Append(newline);
        return builder.ToString();
    }

    private static int IndexOfLineMarker(string content, string marker, int start = 0)
    {
        var index = start;
        while (index < content.Length)
        {
            var found = content.IndexOf(marker, index, StringComparison.Ordinal);
            if (found < 0)
                return -1;
            var atLineStart = found == 0 || content[found - 1] == '\n';
            var atLineEnd = found + marker.Length == content.Length
                || content[found + marker.Length] is '\r' or '\n';
            if (atLineStart && atLineEnd)
                return found;
            index = found + marker.Length;
        }

        return -1;
    }

    private void AtomicWrite(string content)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".";
        var temporary = System.IO.Path.Combine(directory, $".gamediag-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content);
        try
        {
            File.Move(temporary, Path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
