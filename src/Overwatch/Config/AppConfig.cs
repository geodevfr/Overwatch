using YamlDotNet.Serialization;

namespace Overwatch.Config;

public sealed class AppConfig
{
    public bool AcceptTerms { get; set; }

    public string ListenAddress { get; set; } = "127.0.0.1";

    public List<ListenerConfig> Listeners { get; set; } = new();

    public HostsConfig Hosts { get; set; } = new();

    public DecodeConfig Decode { get; set; } = new();

    public SqliteConfig Sqlite { get; set; } = new();

    public WatchdogConfig Watchdog { get; set; } = new();

    public WindowsConfig Windows { get; set; } = new();

    public string RulesPath { get; set; } = "rules.yaml";

    [YamlIgnore]
    public string BaseDirectory { get; set; } = "";
}

public sealed class ListenerConfig
{
    public string Name { get; set; } = "";

    public int ListenPort { get; set; }

    public string UpstreamHost { get; set; } = "";

    public int UpstreamPort { get; set; }
}

public sealed class HostsConfig
{
    public bool Enabled { get; set; }

    public string Path { get; set; } = "";

    public List<HostsEntryConfig> Entries { get; set; } = new();
}

public sealed class HostsEntryConfig
{
    public string Hostname { get; set; } = "";

    public string Address { get; set; } = "127.0.0.1";
}

public sealed class DecodeConfig
{
    public int QueueCapacity { get; set; } = 4096;

    public int MaxBufferBytes { get; set; } = 1_048_576;

    public int MaxPayloadStored { get; set; } = 4096;

    public int SliceMs { get; set; } = 1;
}

public sealed class SqliteConfig
{
    public string Path { get; set; } = "overwatch.db";

    public int FlushIntervalMs { get; set; } = 500;

    public int FlushBatchSize { get; set; } = 200;
}

public sealed class WatchdogConfig
{
    public int LatencyWarnMs { get; set; } = 300;

    public int ReportIntervalMs { get; set; } = 5000;
}

public sealed class WindowsConfig
{
    public bool Enabled { get; set; }

    public int PollMs { get; set; } = 1000;
}
