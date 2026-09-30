using Overwatch.Hosts;
using Overwatch.Logging;

namespace Overwatch.Hosts;

public static class CleanupTasks
{
    public static string Plan(string executable, string hostsPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return "schtasks /Create /F /SC ONLOGON /TN OverwatchHostsCleanup /TR \"\\\""
                + executable + "\\\" --remove-hosts --hosts-path \\\"" + hostsPath + "\\\"\"";
        }

        return "@reboot " + executable + " --remove-hosts --hosts-path " + hostsPath;
    }

    public static int Install()
    {
        var executable = Environment.ProcessPath ?? "overwatch";
        var plan = Plan(executable, HostsFileManager.DefaultPath);
        ConsoleLog.Info("Tâche de nettoyage du fichier hosts, au démarrage de session :");
        ConsoleLog.Info(plan);
        if (!OperatingSystem.IsWindows())
        {
            ConsoleLog.Warn("Sous Linux, ajoutez cette ligne avec crontab -e. Le fichier hosts n'est pas modifié maintenant.");
            return 0;
        }

        ConsoleLog.Warn("Lancez la commande ci-dessus dans un terminal administrateur pour l'enregistrer.");
        return 0;
    }
}
