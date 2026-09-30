using System.Diagnostics;
using System.Runtime.InteropServices;
using Overwatch.Config;
using Overwatch.Hosts;
using Overwatch.Logging;
using Overwatch.Proxy;
using Overwatch.SelfTest;
using Overwatch.Ui;

namespace Overwatch;

public static class Program
{
    public const string Banner = """
        Overwatch — observateur TCP local, lecture seule.
        Les octets sont relaiés sans modification, sans injection et sans attendre le décodeur.
        Aucun déchiffrement TLS. Aucune action dans le jeu. Aucun envoi vers un tiers.
        L'usage d'un outil tiers peut être contraire aux conditions du jeu : c'est à vous de l'assumer.
        Overwatch ne lit pas le programme, ne le modifie pas, et n'essaie pas d'échapper à un contrôle.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (HasFlag(args, "--self-test"))
            {
                Console.WriteLine(Banner);
                var error = await SmokeTest.RunAsync();
                if (error is null)
                {
                    ConsoleLog.Info("Auto-test réussi : octets inchangés, messages reconstitués, hosts idempotent.");
                    return 0;
                }

                ConsoleLog.Error(error);
                return 1;
            }

            if (HasFlag(args, "--remove-hosts"))
                return RemoveHosts(args);

            if (HasFlag(args, "--install-cleanup-task"))
            {
                var task = CleanupTasks.Install();
                return task.Ok ? 0 : 2;
            }

            if (!HasFlag(args, "--relay"))
                return await RunUiAsync(args);

            var configPath = ResolveConfigPath(args);
            var config = ConfigLoader.Load(configPath);
            var errors = ConfigValidator.Validate(config);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    ConsoleLog.Error(error);
                return 2;
            }

            if (!File.Exists(config.RulesPath))
            {
                ConsoleLog.Error($"Fichier de règles introuvable : {config.RulesPath}");
                return 2;
            }

            Console.WriteLine(Banner);
            var ports = config.Listeners.Select(listener => listener.ListenPort).ToHashSet();
            if (!ports.Contains(5555) || !ports.Contains(443))
                ConsoleLog.Warn("Écoutez 5555 et 443. Si un réseau bloque l'un des deux, le client bascule sur l'autre.");
            if (config.Listeners.Any(listener => listener.UpstreamHost.StartsWith("203.0.113.", StringComparison.Ordinal)))
            {
                ConsoleLog.Warn("203.0.113.0/24 est un exemple documentaire. Remplacez upstream_host par l'IP réelle du serveur.");
            }

            var attached = HostsInstaller.Attach(config);
            using var hosts = attached.Session;
            if (attached.Warning is not null)
                ConsoleLog.Error(attached.Warning);
            using var rules = Decode.RuleCatalog.FromFile(config.RulesPath);
            await using var runtime = DiagnosticRuntime.Create(config, rules);
            using var stop = new CancellationTokenSource();
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                runtime.RequestStop();
                stop.Cancel();
            });
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                runtime.RequestStop();
                stop.Cancel();
            });
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                runtime.RequestStop();
                stop.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => hosts?.Dispose();

            runtime.Start();
            var report = ReportAsync(runtime, TimeSpan.FromMilliseconds(config.Watchdog.ReportIntervalMs), config.Watchdog.LatencyWarnMs, stop.Token);
            try
            {
                await runtime.WhenStoppedAsync();
            }
            finally
            {
                runtime.RequestStop();
                stop.Cancel();
                try
                {
                    await report;
                }
                catch (OperationCanceledException)
                {
                }
            }

            return 0;
        }
        catch (ConfigException exception)
        {
            ConsoleLog.Error(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            ConsoleLog.Error(exception.Message);
            return 1;
        }
    }

    private static int RemoveHosts(string[] args)
    {
        var overridePath = ArgValue(args, "--hosts-path");
        string path;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = overridePath;
        }
        else
        {
            var configFlag = ArgValue(args, "--config");
            path = configFlag is not null && File.Exists(configFlag) && !string.IsNullOrWhiteSpace(ConfigLoader.Load(configFlag).Hosts.Path)
                ? ConfigLoader.Load(configFlag).Hosts.Path
                : HostsFileManager.DefaultPath;
        }

        var attempt = new HostsFileManager(path, new HostsJournal(HostsJournal.DefaultPath)).Remove();
        return attempt.Ok ? 0 : 2;
    }

    private static async Task<int> RunUiAsync(string[] args)
    {
        Console.WriteLine(Banner);
        var port = UiPort(args);
        var configPath = ArgValue(args, "--config");
        await using var server = await LocalServer.StartAsync(new LocalServerOptions
        {
            Port = port,
            ConfigPath = string.IsNullOrWhiteSpace(configPath) ? null : Path.GetFullPath(configPath)
        });
        var url = server.BaseAddress.ToString().TrimEnd('/');
        ConsoleLog.Info($"Écran local : {url}");
        ConsoleLog.Info("Cette page reste sur cet ordinateur. Fermez cette fenêtre pour quitter.");
        TryOpenBrowser(url);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        await server.WaitAsync(stop.Token);
        return 0;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            ConsoleLog.Warn($"Ouvrez vous-même le navigateur sur {url}");
        }
    }

    private static int UiPort(string[] args)
    {
        var raw = ArgValue(args, "--ui-port");
        if (string.IsNullOrWhiteSpace(raw))
            return 47321;
        if (!int.TryParse(raw, out var port) || port is < 0 or > 65535)
            throw new ConfigException("--ui-port doit être un port entre 0 et 65535.");
        return port;
    }

    private static async Task ReportAsync(DiagnosticRuntime runtime, TimeSpan interval, int warnMs, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var snapshot = runtime.Watchdog.SnapshotAndResetMax();
            ConsoleLog.Info(
                $"relay c2s {FormatBytes(runtime.Proxy.BytesClientToServer)} s2c {FormatBytes(runtime.Proxy.BytesServerToClient)} | " +
                $"sessions {runtime.Proxy.ActiveConnections} | messages {runtime.Decoder.Messages} | " +
                $"file {runtime.Tap.PendingCount}/{runtime.Tap.Capacity} | pertes tap {runtime.Tap.DroppedChunks} | " +
                $"écarts {runtime.Decoder.Gaps} | incertains {runtime.Decoder.Withheld} | decode max {snapshot.MaxSincePreviousSnapshot.TotalMilliseconds:0.0} ms | " +
                $"sqlite {runtime.Store.Pending} en attente, {runtime.Store.Written} écrits, {runtime.Store.Dropped} perdus");
            if (snapshot.Samples > 0 && snapshot.MaxSincePreviousSnapshot.TotalMilliseconds > warnMs)
            {
                var packet = string.IsNullOrEmpty(snapshot.Offender) ? "inconnu" : snapshot.Offender;
                ConsoleLog.Warn($"Blocage décodeur {snapshot.MaxSincePreviousSnapshot.TotalMilliseconds:0} ms, paquet {packet}. Le relay continue.");
            }
        }
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024)
            return $"{value} o";
        if (value < 1024 * 1024)
            return $"{value / 1024.0:0.0} Ko";
        return $"{value / (1024.0 * 1024.0):0.0} Mo";
    }

    private static string ResolveConfigPath(string[] args)
    {
        var flagged = ArgValue(args, "--config");
        if (!string.IsNullOrWhiteSpace(flagged))
            return Path.GetFullPath(flagged);

        var positional = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (!string.IsNullOrWhiteSpace(positional))
            return Path.GetFullPath(positional);

        var current = Path.Combine(Directory.GetCurrentDirectory(), "config.yaml");
        if (File.Exists(current))
            return current;
        return Path.Combine(AppContext.BaseDirectory, "config.yaml");
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(argument => string.Equals(argument, flag, StringComparison.Ordinal));

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.Ordinal))
                continue;
            if (i + 1 >= args.Length)
                throw new ConfigException($"Argument manquant après {name}.");
            return args[i + 1];
        }

        return null;
    }
}
