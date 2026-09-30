using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Overwatch.Config;
using Overwatch.Decode;
using Overwatch.Proxy;
using Overwatch.Ui;

namespace Overwatch.Tests;

public class LocalUiTests
{
    [Fact]
    public void Config_roundtrip_keeps_the_addresses()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-config");
        try
        {
            var path = Path.Combine(directory.FullName, "config.yaml");
            var config = new AppConfig
            {
                AcceptTerms = true,
                ListenAddress = "127.0.0.1",
                Listeners =
                {
                    new ListenerConfig
                    {
                        Name = "jeu-principal",
                        ListenPort = 5555,
                        UpstreamHost = "198.51.100.8",
                        UpstreamPort = 5555
                    }
                },
                Hosts =
                {
                    Enabled = true,
                    Entries = { new HostsEntryConfig { Hostname = "jeu.exemple", Address = "127.0.0.1" } }
                },
                BaseDirectory = directory.FullName,
                RulesPath = "rules.yaml"
            };

            ConfigWriter.Write(path, config);
            var loaded = ConfigLoader.Load(path);
            Assert.True(loaded.AcceptTerms);
            Assert.Equal("198.51.100.8", loaded.Listeners[0].UpstreamHost);
            Assert.Equal("jeu.exemple", loaded.Hosts.Entries[0].Hostname);
            Assert.True(loaded.Hosts.Enabled);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Capture_writes_both_directions_only_when_armed()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-capture");
        try
        {
            await using var recorder = new CaptureRecorder(directory.FullName);
            var ignored = "nope"u8.ToArray();
            recorder.Offer("abcdef012345", "jeu", Direction.ClientToServer, ignored, ignored.Length);
            await recorder.DrainAsync();
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.bin"));

            recorder.SetArmed(true);
            var clientBytes = new byte[] { 0x16, 0x03, 0x01, 0x00 };
            var serverBytes = new byte[] { 1, 2, 3, 4, 5 };
            recorder.Offer("abcdef012345", "jeu", Direction.ClientToServer, clientBytes, clientBytes.Length);
            recorder.Offer("abcdef012345", "jeu", Direction.ServerToClient, serverBytes, serverBytes.Length);
            recorder.End("abcdef012345");
            await recorder.DrainAsync();

            var sessions = CaptureCatalog.List(directory.FullName);
            var session = Assert.Single(sessions);
            Assert.Equal("jeu", session.Listener);
            var client = Assert.Single(session.Directions, direction => direction.Direction == "c2s");
            var server = Assert.Single(session.Directions, direction => direction.Direction == "s2c");
            Assert.True(client.LooksLikeTls);
            Assert.False(server.LooksLikeTls);
            Assert.Equal(5, server.Bytes);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(Path.Combine(directory.FullName, "abcdef012345.s2c.bin")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Browser_api_rejects_the_example_address_and_serves_the_page()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-ui");
        try
        {
            var rules = Path.Combine(directory.FullName, "rules.yaml");
            await File.WriteAllTextAsync(rules, """
                version: 1
                client: dofus3
                detection:
                  - version: "3.6.11.13"
                    status: unknown
                    pending: [sale_lots]
                    rules: []
                rules: []
                """);
            var configPath = Path.Combine(directory.FullName, "config.yaml");
            ConfigWriter.Write(configPath, new AppConfig
            {
                AcceptTerms = false,
                Listeners =
                {
                    new ListenerConfig { Name = "jeu-principal", ListenPort = 5555, UpstreamHost = "203.0.113.10", UpstreamPort = 5555 }
                },
                BaseDirectory = directory.FullName,
                RulesPath = rules
            });

            var webRoot = RepoFile("src/Overwatch/wwwroot/index.html");
            await using var server = await LocalServer.StartAsync(new LocalServerOptions
            {
                Port = 0,
                ConfigPath = configPath,
                CaptureDirectory = Path.Combine(directory.FullName, "captures"),
                WebRoot = Path.GetDirectoryName(webRoot)
            });

            using var client = new HttpClient { BaseAddress = server.BaseAddress };
            var html = await client.GetStringAsync("/");
            Assert.Contains("Overwatch", html, StringComparison.Ordinal);
            Assert.Contains("Armer la capture", html, StringComparison.Ordinal);

            var rulesBody = await client.GetFromJsonAsync<JsonElement>("/api/rules");
            Assert.Equal("unknown", rulesBody.GetProperty("versions")[0].GetProperty("status").GetString());

            var save = await client.PutAsJsonAsync("/api/config", new
            {
                acceptTerms = true,
                listeners = new[]
                {
                    new { name = "jeu-principal", listenPort = 5555, upstreamHost = "203.0.113.10", upstreamPort = 5555 }
                },
                hostsEnabled = false,
                hosts = Array.Empty<object>(),
                windowsEnabled = false
            });
            Assert.True(save.IsSuccessStatusCode);

            var start = await client.PostAsync("/api/relay/start", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
            Assert.Contains("203.0.113", await start.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var upstream = new TcpListener(IPAddress.Loopback, 0);
            upstream.Start();
            var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
            var listen = FreePort();
            var real = await client.PutAsJsonAsync("/api/config", new
            {
                acceptTerms = true,
                listeners = new[]
                {
                    new { name = "jeu-principal", listenPort = listen, upstreamHost = "127.0.0.1", upstreamPort = upstreamPort }
                },
                hostsEnabled = false,
                hosts = Array.Empty<object>(),
                windowsEnabled = false
            });
            Assert.True(real.IsSuccessStatusCode, await real.Content.ReadAsStringAsync());

            var started = await client.PostAsync("/api/relay/start", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync());
            await client.PostAsJsonAsync("/api/capture", new { armed = true });

            var accepted = upstream.AcceptTcpClientAsync();
            using var game = new TcpClient();
            await game.ConnectAsync(IPAddress.Loopback, listen);
            using var remote = await accepted;
            var payload = new byte[] { 9, 8, 7, 6 };
            await game.GetStream().WriteAsync(payload);
            var buffer = new byte[4];
            await remote.GetStream().ReadExactlyAsync(buffer);
            Assert.Equal(payload, buffer);

            await client.PostAsync("/api/relay/stop", new StringContent("{}", Encoding.UTF8, "application/json"));
            var captures = await client.GetFromJsonAsync<JsonElement>("/api/captures");
            Assert.True(captures.GetArrayLength() >= 1);
            Assert.True(captures[0].GetProperty("directions")[0].GetProperty("bytes").GetInt64() >= 4);
            upstream.Stop();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
