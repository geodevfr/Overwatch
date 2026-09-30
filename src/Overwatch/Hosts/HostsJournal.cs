using Overwatch.Logging;

namespace Overwatch.Hosts;

/// <summary>
/// Journal dédié aux écritures du fichier hosts. Il reste dans le profil
/// de l'utilisateur : le dossier système du fichier hosts n'est pas utilisé.
/// </summary>
public sealed class HostsJournal
{
    public HostsJournal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Chemin de journal vide.", nameof(path));
        Path = path;
    }

    public string Path { get; }

    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = System.IO.Path.GetTempPath();
            return System.IO.Path.Combine(root, "Overwatch", "hosts.log");
        }
    }

    public void Write(string outcome, string detail)
    {
        var line = $"{DateTimeOffset.Now:O}\t{outcome}\t{detail}{Environment.NewLine}";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(Path, line);
        }
        catch (Exception exception)
        {
            ConsoleLog.Warn($"Journal hosts non écrit ({exception.Message}). {outcome} : {detail}");
            return;
        }

        if (string.Equals(outcome, "échec", StringComparison.Ordinal))
            ConsoleLog.Error(detail);
        else
            ConsoleLog.Info(detail);
    }
}
