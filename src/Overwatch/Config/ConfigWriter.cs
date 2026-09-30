using System.Globalization;
using System.Text;

namespace Overwatch.Config;

public static class ConfigWriter
{
    public static void Write(string path, AppConfig config)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Render(config));
        File.Move(temporary, path, overwrite: true);
    }

    public static string Render(AppConfig config)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Overwatch observe le trafic TCP de CETTE machine. Il ne le modifie pas.");
        builder.AppendLine("# upstream_host est une adresse IP, jamais un nom DNS.");
        builder.AppendLine();
        builder.Append("accept_terms: ").AppendLine(config.AcceptTerms ? "true" : "false");
        builder.Append("listen_address: ").AppendLine(config.ListenAddress);
        builder.AppendLine();
        builder.AppendLine("listeners:");
        foreach (var listener in config.Listeners)
        {
            builder.Append("  - name: ").AppendLine(YamlScalar(listener.Name));
            builder.Append("    listen_port: ").AppendLine(listener.ListenPort.ToString(CultureInfo.InvariantCulture));
            builder.Append("    upstream_host: ").AppendLine(YamlScalar(listener.UpstreamHost));
            builder.Append("    upstream_port: ").AppendLine(listener.UpstreamPort.ToString(CultureInfo.InvariantCulture));
        }

        builder.AppendLine();
        builder.AppendLine("hosts:");
        builder.Append("  enabled: ").AppendLine(config.Hosts.Enabled ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(config.Hosts.Path))
            builder.Append("  path: ").AppendLine(YamlScalar(config.Hosts.Path));
        builder.AppendLine("  entries:");
        if (config.Hosts.Entries.Count == 0)
            builder.AppendLine("    []");
        foreach (var entry in config.Hosts.Entries)
        {
            builder.Append("    - hostname: ").AppendLine(YamlScalar(entry.Hostname));
            builder.Append("      address: ").AppendLine(YamlScalar(entry.Address));
        }

        builder.AppendLine();
        builder.AppendLine("decode:");
        builder.Append("  queue_capacity: ").AppendLine(config.Decode.QueueCapacity.ToString(CultureInfo.InvariantCulture));
        builder.Append("  max_buffer_bytes: ").AppendLine(config.Decode.MaxBufferBytes.ToString(CultureInfo.InvariantCulture));
        builder.Append("  max_payload_stored: ").AppendLine(config.Decode.MaxPayloadStored.ToString(CultureInfo.InvariantCulture));
        builder.Append("  slice_ms: ").AppendLine(config.Decode.SliceMs.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("sqlite:");
        builder.Append("  path: ").AppendLine(YamlScalar(config.Sqlite.Path));
        builder.Append("  flush_interval_ms: ").AppendLine(config.Sqlite.FlushIntervalMs.ToString(CultureInfo.InvariantCulture));
        builder.Append("  flush_batch_size: ").AppendLine(config.Sqlite.FlushBatchSize.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("watchdog:");
        builder.Append("  latency_warn_ms: ").AppendLine(config.Watchdog.LatencyWarnMs.ToString(CultureInfo.InvariantCulture));
        builder.Append("  report_interval_ms: ").AppendLine(config.Watchdog.ReportIntervalMs.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("windows:");
        builder.Append("  enabled: ").AppendLine(config.Windows.Enabled ? "true" : "false");
        builder.Append("  poll_ms: ").AppendLine(config.Windows.PollMs.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.Append("rules_path: ").AppendLine(YamlScalar(RulesPathForYaml(config)));
        return builder.ToString();
    }

    public static string RulesPathForYaml(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RulesPath))
            return "rules.yaml";
        if (!Path.IsPathRooted(config.RulesPath))
            return config.RulesPath.Replace('\\', '/');

        var name = Path.GetFileName(config.RulesPath);
        if (string.IsNullOrEmpty(config.BaseDirectory))
            return config.RulesPath.Replace('\\', '/');
        var beside = Path.GetFullPath(Path.Combine(config.BaseDirectory, name));
        return string.Equals(beside, Path.GetFullPath(config.RulesPath), StringComparison.OrdinalIgnoreCase)
            ? name
            : config.RulesPath.Replace('\\', '/');
    }

    public static string YamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (value.Any(character => char.IsWhiteSpace(character) || character is ':' or '#' or '"' or '\''))
            return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        return value;
    }
}
