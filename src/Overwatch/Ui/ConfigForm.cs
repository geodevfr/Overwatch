using Overwatch.Config;

namespace Overwatch.Ui;

public sealed class ConfigForm
{
    public bool AcceptTerms { get; set; }

    public List<ListenerForm> Listeners { get; set; } = new();

    public bool HostsEnabled { get; set; }

    public List<HostForm> Hosts { get; set; } = new();

    public bool WindowsEnabled { get; set; }
}

public sealed class ListenerForm
{
    public string Name { get; set; } = "";

    public int ListenPort { get; set; }

    public string UpstreamHost { get; set; } = "";

    public int UpstreamPort { get; set; }
}

public sealed class HostForm
{
    public string Hostname { get; set; } = "";

    public string Address { get; set; } = "127.0.0.1";
}

public static class ConfigForms
{
    public static ConfigForm From(AppConfig config)
    {
        return new ConfigForm
        {
            AcceptTerms = config.AcceptTerms,
            Listeners = config.Listeners.Select(listener => new ListenerForm
            {
                Name = listener.Name,
                ListenPort = listener.ListenPort,
                UpstreamHost = listener.UpstreamHost,
                UpstreamPort = listener.UpstreamPort
            }).ToList(),
            HostsEnabled = config.Hosts.Enabled,
            Hosts = config.Hosts.Entries.Select(entry => new HostForm
            {
                Hostname = entry.Hostname,
                Address = string.IsNullOrWhiteSpace(entry.Address) ? "127.0.0.1" : entry.Address
            }).ToList(),
            WindowsEnabled = config.Windows.Enabled
        };
    }

    public static void Apply(AppConfig config, ConfigForm form)
    {
        config.AcceptTerms = form.AcceptTerms;
        config.Listeners = (form.Listeners ?? new List<ListenerForm>())
            .Select(listener => new ListenerConfig
            {
                Name = listener.Name?.Trim() ?? "",
                ListenPort = listener.ListenPort,
                UpstreamHost = listener.UpstreamHost?.Trim() ?? "",
                UpstreamPort = listener.UpstreamPort
            })
            .ToList();
        config.Hosts ??= new HostsConfig();
        config.Hosts.Enabled = form.HostsEnabled;
        config.Hosts.Entries = (form.Hosts ?? new List<HostForm>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Hostname))
            .Select(entry => new HostsEntryConfig
            {
                Hostname = entry.Hostname.Trim(),
                Address = string.IsNullOrWhiteSpace(entry.Address) ? "127.0.0.1" : entry.Address.Trim()
            })
            .ToList();
        config.Windows ??= new WindowsConfig();
        config.Windows.Enabled = form.WindowsEnabled;
    }
}
