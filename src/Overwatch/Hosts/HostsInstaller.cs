using Overwatch.Config;
using Overwatch.Logging;

namespace Overwatch.Hosts;

public static class HostsInstaller
{
    public static HostsSession? Attach(AppConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.Hosts.Path) ? HostsFileManager.DefaultPath : config.Hosts.Path;
        var manager = new HostsFileManager(path);
        if (!config.Hosts.Enabled)
        {
            try
            {
                if (manager.ContainsManagedBlock())
                {
                    var change = manager.Remove();
                    ConsoleLog.Info($"Bloc hosts laissé par un arrêt brutal : {HostsSession.Describe(change)}.");
                }
            }
            catch (Exception exception)
            {
                ConsoleLog.Warn($"Impossible de vérifier le fichier hosts ({exception.Message}).");
            }

            return null;
        }

        var entries = config.Hosts.Entries
            .Select(entry => HostsFileManager.Normalize(entry.Hostname, entry.Address))
            .ToList();
        var installed = manager.Install(entries);
        ConsoleLog.Info($"Fichier hosts {path} : {HostsSession.Describe(installed)}.");
        return new HostsSession(manager);
    }

    public static HostsGlance Glance(AppConfig config)
    {
        var path = string.IsNullOrWhiteSpace(config.Hosts.Path) ? HostsFileManager.DefaultPath : config.Hosts.Path;
        try
        {
            var manager = new HostsFileManager(path);
            return new HostsGlance(path, manager.ContainsManagedBlock(), null);
        }
        catch (Exception exception)
        {
            return new HostsGlance(path, false, exception.Message);
        }
    }
}

public sealed record HostsGlance(string Path, bool ManagedBlockPresent, string? Error);
