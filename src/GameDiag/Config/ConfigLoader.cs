using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GameDiag.Config;

public static class ConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Fichier de configuration introuvable : {path}");

        var config = Deserializer.Deserialize<AppConfig>(File.ReadAllText(path))
            ?? throw new ConfigException("La configuration est vide.");

        config.Listeners ??= new List<ListenerConfig>();
        config.Hosts ??= new HostsConfig();
        config.Hosts.Entries ??= new List<HostsEntryConfig>();
        config.Decode ??= new DecodeConfig();
        config.Sqlite ??= new SqliteConfig();
        config.Watchdog ??= new WatchdogConfig();
        config.ListenAddress = string.IsNullOrWhiteSpace(config.ListenAddress)
            ? "127.0.0.1"
            : config.ListenAddress.Trim();
        config.RulesPath = string.IsNullOrWhiteSpace(config.RulesPath) ? "rules.yaml" : config.RulesPath.Trim();
        config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
        if (!Path.IsPathRooted(config.RulesPath))
            config.RulesPath = Path.GetFullPath(Path.Combine(config.BaseDirectory, config.RulesPath));

        return config;
    }
}
