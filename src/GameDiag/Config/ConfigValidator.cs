using System.Net;
using System.Net.Sockets;

namespace GameDiag.Config;

public static class ConfigValidator
{
    public static IReadOnlyList<string> Validate(AppConfig config)
    {
        var errors = new List<string>();

        if (!config.AcceptTerms)
        {
            errors.Add(
                "accept_terms est à false. Passez-le à true pour confirmer que l'observation porte sur votre propre trafic, sur votre machine.");
        }

        if (!IPAddress.TryParse(config.ListenAddress, out var listenAddress))
        {
            errors.Add($"listen_address invalide : {config.ListenAddress}");
        }
        else if (!IPAddress.IsLoopback(listenAddress))
        {
            errors.Add("listen_address doit être une adresse de bouclage (127.0.0.1 ou ::1). GameDiag n'écoute pas sur le réseau.");
        }

        if (config.Listeners.Count == 0)
            errors.Add("Au moins un listener est requis.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        foreach (var listener in config.Listeners)
        {
            if (string.IsNullOrWhiteSpace(listener.Name))
                errors.Add("Chaque listener a besoin d'un name.");
            else if (!names.Add(listener.Name))
                errors.Add($"Nom de listener en double : {listener.Name}");

            if (listener.ListenPort is < 1 or > 65535)
                errors.Add($"Port d'écoute hors plage pour {listener.Name} : {listener.ListenPort}");
            else if (!ports.Add(listener.ListenPort))
                errors.Add($"Port d'écoute en double : {listener.ListenPort}");

            if (listener.UpstreamPort is < 1 or > 65535)
                errors.Add($"Port upstream hors plage pour {listener.Name} : {listener.UpstreamPort}");

            if (!IPAddress.TryParse(listener.UpstreamHost, out var upstream))
            {
                errors.Add(
                    $"upstream_host de {listener.Name} doit être une adresse IP, pas un nom DNS. Un nom résolu par le fichier hosts rebouclerait vers le proxy.");
            }
            else if (IPAddress.IsLoopback(upstream) && upstream.AddressFamily == listenAddress?.AddressFamily
                     && listener.UpstreamPort == listener.ListenPort)
            {
                errors.Add(
                    $"Le listener {listener.Name} redirige vers lui-même ({listener.UpstreamHost}:{listener.UpstreamPort}).");
            }
        }

        if (config.Decode.QueueCapacity < 1)
            errors.Add("decode.queue_capacity doit être au moins 1.");
        if (config.Decode.MaxBufferBytes < 65_536)
            errors.Add("decode.max_buffer_bytes doit être au moins 65536, au-dessus de la lecture réseau de 16 Ko.");
        if (config.Decode.MaxPayloadStored < 0)
            errors.Add("decode.max_payload_stored ne peut pas être négatif.");

        if (config.Sqlite.FlushIntervalMs < 50)
            errors.Add("sqlite.flush_interval_ms doit être au moins 50.");
        if (config.Sqlite.FlushBatchSize < 1)
            errors.Add("sqlite.flush_batch_size doit être au moins 1.");
        if (string.IsNullOrWhiteSpace(config.Sqlite.Path))
            errors.Add("sqlite.path est vide.");

        if (config.Watchdog.LatencyWarnMs < 1)
            errors.Add("watchdog.latency_warn_ms doit être au moins 1.");
        if (config.Watchdog.ReportIntervalMs < 200)
            errors.Add("watchdog.report_interval_ms doit être au moins 200.");

        if (string.IsNullOrWhiteSpace(config.RulesPath))
            errors.Add("rules_path est vide.");

        if (config.Hosts.Enabled)
        {
            if (config.Hosts.Entries.Count == 0)
                errors.Add("hosts.enabled est true mais aucune entrée n'est définie.");

            foreach (var entry in config.Hosts.Entries)
            {
                if (!Hosts.HostsFileManager.IsSafeHostname(entry.Hostname))
                    errors.Add($"Nom d'hôte refusé (caractères autorisés : lettres, chiffres, '.' et '-') : {entry.Hostname}");
                if (!IPAddress.TryParse(entry.Address, out _))
                    errors.Add($"Adresse hosts invalide pour {entry.Hostname} : {entry.Address}");
            }
        }

        return errors;
    }
}
