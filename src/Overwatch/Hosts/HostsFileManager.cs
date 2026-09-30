using System.Net;
using System.Text;
using Overwatch.Config;

namespace Overwatch.Hosts;

public enum HostsChange
{
    Unchanged,
    Installed,
    Removed
}

public readonly record struct HostsAttempt(bool Ok, HostsChange Change, string Detail)
{
    public static HostsAttempt Success(HostsChange change, string detail) => new(true, change, detail);

    public static HostsAttempt Fail(string detail) => new(false, HostsChange.Unchanged, detail);
}

public sealed record HostsEntry(string Hostname, string Address);

internal interface IHostsStore
{
    bool Exists();

    string Read();

    void Write(string content);

    bool IsReadOnly();

    void SetReadOnly(bool value);
}

/// <summary>
/// Maintient un bloc borné par des marqueurs dans le fichier hosts.
/// L'installation retire d'abord tout bloc existant, y compris un bloc
/// laissé par un arrêt brutal. L'attribut lecture seule est retiré le
/// temps de l'écriture, puis remis. Les ACL ne sont jamais modifiées.
/// </summary>
public sealed class HostsFileManager
{
    public const string BeginMarker = "# >>> overwatch >>>";
    public const string EndMarker = "# <<< overwatch <<<";
    public const string LegacyBeginMarker = "# OVERWATCH-BEGIN";
    public const string LegacyEndMarker = "# OVERWATCH-END";

    private static readonly (string Begin, string End)[] MarkerPairs =
    {
        (BeginMarker, EndMarker),
        (LegacyBeginMarker, LegacyEndMarker)
    };

    private readonly object _gate = new();
    private readonly HostsJournal? _journal;
    private readonly IHostsStore _store;

    public HostsFileManager(string path, HostsJournal? journal = null)
        : this(path, journal, new HostsDiskStore(path))
    {
    }

    internal HostsFileManager(string path, HostsJournal? journal, IHostsStore store)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Chemin du fichier hosts vide.", nameof(path));
        Path = path;
        _journal = journal;
        _store = store;
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
            if (!_store.Exists())
                return false;
            var text = _store.Read();
            return text.Contains(BeginMarker, StringComparison.Ordinal)
                || text.Contains(LegacyBeginMarker, StringComparison.Ordinal);
        }
    }

    public HostsAttempt Install(IReadOnlyList<HostsEntry> entries)
    {
        var normalized = NormalizeAll(entries);
        lock (_gate)
        {
            return Commit(original => Compose(original, normalized), normalized.Count == 0 ? HostsChange.Removed : HostsChange.Installed);
        }
    }

    public HostsAttempt Remove()
    {
        lock (_gate)
            return Commit(RemoveManagedBlock, HostsChange.Removed);
    }

    public static string RemoveManagedBlock(string content)
    {
        var current = content;
        foreach (var pair in MarkerPairs)
            current = RemovePair(current, pair.Begin, pair.End);
        return current;
    }

    private HostsAttempt Commit(Func<string, string> transform, HostsChange whenChanged)
    {
        string original;
        try
        {
            original = _store.Exists() ? _store.Read() : "";
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Report(FailRead(exception));
        }

        var next = transform(original);
        if (next == original)
            return Report(HostsAttempt.Success(HostsChange.Unchanged, $"fichier hosts {Path} : inchangé."));

        var attributeNote = "Attribut lecture seule : absent.";
        var cleared = false;
        try
        {
            if (_store.Exists() && _store.IsReadOnly())
            {
                _store.SetReadOnly(false);
                cleared = true;
                attributeNote = "Attribut lecture seule : retiré avant écriture.";
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Report(HostsAttempt.Fail(
                $"échec : attribut lecture seule non retiré sur {Path} ({exception.Message}). Le fichier n'a pas été modifié."));
        }

        HostsAttempt? failure = null;
        try
        {
            _store.Write(next);
            var actual = _store.Read();
            if (actual != next)
            {
                failure = HostsAttempt.Fail(
                    $"échec silencieux : {Path} n'a pas conservé le bloc Overwatch. Un antivirus ou l'accès contrôlé aux dossiers a pu annuler l'écriture.");
            }
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            failure = FailWrite(exception);
        }
        finally
        {
            if (cleared)
            {
                try
                {
                    _store.SetReadOnly(true);
                    if (failure is null)
                        attributeNote = "Attribut lecture seule : retiré avant écriture, puis restauré.";
                }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    attributeNote = $"Attribut lecture seule : retiré, restauration impossible ({exception.Message}). Les ACL n'ont pas été modifiées.";
                }
            }
        }

        if (failure is HostsAttempt failed)
            return Report(failed);
        return Report(HostsAttempt.Success(whenChanged, $"fichier hosts {Path} : {HostsSession.Describe(whenChanged)}. {attributeNote}"));
    }

    private HostsAttempt Report(HostsAttempt attempt)
    {
        if (_journal is null)
        {
            if (attempt.Ok)
                Logging.ConsoleLog.Info(attempt.Detail);
            else
                Logging.ConsoleLog.Error(attempt.Detail);
        }
        else
        {
            _journal.Write(attempt.Ok ? "succès" : "échec", attempt.Detail);
        }

        return attempt;
    }

    private HostsAttempt FailRead(Exception exception) =>
        HostsAttempt.Fail($"échec : lecture impossible de {Path} ({DescribeStorage(exception)}).");

    private HostsAttempt FailWrite(Exception exception) =>
        HostsAttempt.Fail($"échec : écriture refusée de {Path} ({DescribeStorage(exception)}). Le fichier hosts n'a pas été modifié.");

    private static string DescribeStorage(Exception exception)
    {
        if (exception is UnauthorizedAccessException)
            return "accès refusé, droits insuffisants ou accès contrôlé aux dossiers : " + exception.Message;
        return exception.Message;
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException or NotSupportedException;

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
        builder.Append("# Bloc géré par Overwatch. Retiré à l'arrêt du processus.").Append(newline);
        foreach (var entry in entries)
            builder.Append(entry.Address).Append(' ').Append(entry.Hostname).Append(newline);
        builder.Append(EndMarker).Append(newline);
        return builder.ToString();
    }

    private static string RemovePair(string content, string beginMarker, string endMarker)
    {
        while (true)
        {
            var begin = IndexOfLineMarker(content, beginMarker);
            if (begin < 0)
                return content;

            var end = IndexOfLineMarker(content, endMarker, begin + beginMarker.Length);
            int cutEnd;
            if (end < 0)
            {
                cutEnd = content.Length;
            }
            else
            {
                cutEnd = end + endMarker.Length;
                if (cutEnd < content.Length && content[cutEnd] == '\r')
                    cutEnd++;
                if (cutEnd < content.Length && content[cutEnd] == '\n')
                    cutEnd++;
            }

            content = content.Remove(begin, cutEnd - begin);
        }
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
}

internal sealed class HostsDiskStore : IHostsStore
{
    public HostsDiskStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public bool Exists() => File.Exists(Path);

    public string Read() => File.ReadAllText(Path);

    public bool IsReadOnly() =>
        Exists() && (File.GetAttributes(Path) & FileAttributes.ReadOnly) != 0;

    public void SetReadOnly(bool value)
    {
        if (!Exists())
            return;
        var attributes = File.GetAttributes(Path);
        var next = value
            ? attributes | FileAttributes.ReadOnly
            : attributes & ~FileAttributes.ReadOnly;
        if (next != attributes)
            File.SetAttributes(Path, next);
    }

    public void Write(string content)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) ?? ".";
        var temporary = System.IO.Path.Combine(directory, $".overwatch-{Guid.NewGuid():N}.tmp");
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
