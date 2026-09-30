namespace Overwatch.Hosts;

public static class CleanupTasks
{
    public const string TaskName = "OverwatchHostsCleanup";

    public static string Plan(string executable, string hostsPath)
    {
        var command = TaskCommand(executable, hostsPath);
        if (OperatingSystem.IsWindows())
            return "schtasks /Create /F /SC ONLOGON /RL HIGHEST /TN " + TaskName + " /TR \"" + command.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        return "@reboot " + command;
    }

    public static string TaskCommand(string executable, string hostsPath) =>
        "\"" + executable + "\" --remove-hosts --hosts-path \"" + hostsPath + "\"";

    public static HostsAttempt Install()
    {
        var executable = Environment.ProcessPath ?? "overwatch";
        var hostsPath = HostsFileManager.DefaultPath;
        var journal = new HostsJournal(HostsJournal.DefaultPath);
        if (executable.Contains('"', StringComparison.Ordinal) || hostsPath.Contains('"', StringComparison.Ordinal))
        {
            var refused = "échec : chemin refusé pour la tâche planifiée.";
            journal.Write("échec", refused);
            return HostsAttempt.Fail(refused);
        }

        var plan = Plan(executable, hostsPath);
        if (!OperatingSystem.IsWindows())
        {
            var detail = "Tâche non créée sur ce système. Au prochain lancement, un bloc hosts oublié est retiré. Commande indicative : " + plan;
            journal.Write("échec", detail);
            return HostsAttempt.Fail(detail);
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = "schtasks";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.ArgumentList.Add("/Create");
            process.StartInfo.ArgumentList.Add("/F");
            process.StartInfo.ArgumentList.Add("/SC");
            process.StartInfo.ArgumentList.Add("ONLOGON");
            process.StartInfo.ArgumentList.Add("/RL");
            process.StartInfo.ArgumentList.Add("HIGHEST");
            process.StartInfo.ArgumentList.Add("/TN");
            process.StartInfo.ArgumentList.Add(TaskName);
            process.StartInfo.ArgumentList.Add("/TR");
            process.StartInfo.ArgumentList.Add(TaskCommand(executable, hostsPath));
            if (!process.Start())
            {
                var detail = "échec : schtasks n'a pas démarré. " + plan;
                journal.Write("échec", detail);
                return HostsAttempt.Fail(detail);
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var combined = string.Join(" ", new[] { output, error }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
            if (process.ExitCode != 0)
            {
                var detail = "échec : tâche " + TaskName + " non créée (code " + process.ExitCode + "). " + combined
                    + " Relancez en administrateur. Aucun droit du système n'a été modifié.";
                journal.Write("échec", detail);
                return HostsAttempt.Fail(detail);
            }

            var success = "succès : tâche " + TaskName + " créée. Au démarrage de session elle retire un bloc hosts oublié, elle ne réécrit pas la redirection. " + combined;
            journal.Write("succès", success);
            return HostsAttempt.Success(HostsChange.Installed, success);
        }
        catch (Exception exception)
        {
            var detail = "échec : tâche planifiée non créée (" + exception.Message + "). " + plan;
            journal.Write("échec", detail);
            return HostsAttempt.Fail(detail);
        }
    }
}
