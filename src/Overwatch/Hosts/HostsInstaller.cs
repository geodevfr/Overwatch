using Overwatch.Config;

namespace Overwatch.Hosts;

public static class HostsInstaller
{
    /// <summary>
    /// Au lancement du processus, retire un bloc laissé par un arrêt brutal.
    /// Sans droit d'écriture, retourne un message et ne lance pas d'exception.
    /// </summary>
    public static string? SweepLeftover(string? hostsPath)
    {
        var path = string.IsNullOrWhiteSpace(hostsPath) ? HostsFileManager.DefaultPath : hostsPath;
        try
        {
            var manager = new HostsFileManager(path, new HostsJournal(HostsJournal.DefaultPath));
            if (!manager.ContainsManagedBlock())
                return null;
            var removed = manager.Remove();
            return removed.Ok ? null : ExplainFailure(removed.Detail);
        }
        catch (Exception exception)
        {
            return ExplainFailure(exception.Message);
        }
    }

    public static HostsAttachResult Attach(AppConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.Hosts.Path) ? HostsFileManager.DefaultPath : config.Hosts.Path;
        var manager = new HostsFileManager(path, new HostsJournal(HostsJournal.DefaultPath));
        if (!config.Hosts.Enabled)
        {
            if (!manager.ContainsManagedBlock())
                return new HostsAttachResult(null, null, false);

            var removed = manager.Remove();
            return removed.Ok
                ? new HostsAttachResult(null, null, false)
                : new HostsAttachResult(null, ExplainFailure(removed.Detail), false);
        }

        HostsAttempt installed;
        try
        {
            var entries = config.Hosts.Entries
                .Select(entry => HostsFileManager.Normalize(entry.Hostname, entry.Address))
                .ToList();
            installed = manager.Install(entries);
        }
        catch (Exception exception)
        {
            return new HostsAttachResult(null, ExplainFailure(exception.Message), false);
        }

        if (!installed.Ok)
            return new HostsAttachResult(null, ExplainFailure(installed.Detail), false);

        return new HostsAttachResult(new HostsSession(manager), null, true);
    }

    public static string ExplainFailure(string detail) =>
        "La redirection du fichier hosts a échoué (" + detail + "). "
        + "Le relais peut tourner, mais le jeu ne passera pas par Overwatch : aucune capture du jeu n'est possible tant que ce fichier n'est pas modifié. "
        + "Relancez le programme avec un clic droit, Exécuter en tant qu'administrateur. "
        + "Si l'accord est déjà donné et que l'écriture échoue encore, l'accès contrôlé aux dossiers ou un antivirus bloque le fichier hosts. "
        + "Les permissions du fichier n'ont pas été changées.";

    public static HostsGlance Glance(AppConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.Hosts.Path) ? HostsFileManager.DefaultPath : config.Hosts.Path;
        try
        {
            var manager = new HostsFileManager(path);
            return new HostsGlance(path, manager.ContainsManagedBlock(), null, HostsJournal.DefaultPath);
        }
        catch (Exception exception)
        {
            return new HostsGlance(path, false, exception.Message, HostsJournal.DefaultPath);
        }
    }
}

public sealed record HostsAttachResult(HostsSession? Session, string? Warning, bool Redirected);

public sealed record HostsGlance(string Path, bool ManagedBlockPresent, string? Error, string? JournalPath = null);
