using System.Net;
using System.Net.Sockets;
using GameDiag.Config;
using GameDiag.Decode;
using GameDiag.Proxy;
using GameDiag.SelfTest;

namespace GameDiag.Tests;

public class ConfigAndRelayTests
{
    [Fact]
    public void Sample_config_requires_explicit_consent_then_validates()
    {
        var config = ConfigLoader.Load(RepoFile("src/GameDiag/config.yaml"));
        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("accept_terms", StringComparison.Ordinal));

        config.AcceptTerms = true;
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Sample_rules_compile()
    {
        var rules = RuleCompiler.Compile(RuleLoader.LoadFile(RepoFile("src/GameDiag/rules.yaml")));
        Assert.Contains(rules.Rules, rule => rule.Id == "market_tick");
        Assert.Contains(rules.Rules, rule => rule.Id == "session_hello");
    }

    [Fact]
    public void Rejects_a_non_loopback_listen_address_and_a_dns_upstream()
    {
        var config = new AppConfig
        {
            AcceptTerms = true,
            ListenAddress = "0.0.0.0",
            Listeners =
            {
                new ListenerConfig
                {
                    Name = "jeu",
                    ListenPort = 5555,
                    UpstreamHost = "jeu.exemple",
                    UpstreamPort = 5555
                }
            }
        };

        var errors = ConfigValidator.Validate(config);
        Assert.Contains(errors, error => error.Contains("bouclage", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("adresse IP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_reload_keeps_the_previous_rules()
    {
        var directory = Directory.CreateTempSubdirectory("gamediag-rules");
        try
        {
            var path = Path.Combine(directory.FullName, "rules.yaml");
            await File.WriteAllTextAsync(path, SampleRules.Yaml);
            using var catalog = RuleCatalog.FromFile(path);
            Assert.Equal(2, catalog.Current.Rules.Count);

            await File.WriteAllTextAsync(path, "rules: [\n");
            Assert.False(catalog.Reload());
            Assert.Equal("session_hello", catalog.Current.Rules[0].Id);

            var updated = SampleRules.Yaml.Replace("session_hello", "session_hello_v2", StringComparison.Ordinal);
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            catalog.Reloaded += () => reloaded.TrySetResult();
            await File.WriteAllTextAsync(path, updated);
            var completed = await Task.WhenAny(reloaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(reloaded.Task, completed);
            Assert.Contains(catalog.Current.Rules, rule => rule.Id == "session_hello_v2");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Relay_forwards_bytes_while_the_decoder_is_not_reading()
    {
        var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();
        var upstreamPort = ((IPEndPoint)upstreamListener.LocalEndpoint).Port;
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var accept = Task.Run(async () =>
        {
            using var remote = await upstreamListener.AcceptTcpClientAsync();
            remote.NoDelay = true;
            var buffer = new byte[payload.Length];
            await remote.GetStream().ReadExactlyAsync(buffer);
            received.TrySetResult(buffer);
        });

        var tap = new ObservationTap(1);
        var proxy = new TcpRelayProxy(new AppConfig
        {
            ListenAddress = "127.0.0.1",
            Listeners =
            {
                new ListenerConfig
                {
                    Name = "essai",
                    ListenPort = 0,
                    UpstreamHost = "127.0.0.1",
                    UpstreamPort = upstreamPort
                }
            }
        }, tap);

        using var stop = new CancellationTokenSource();
        var running = proxy.RunAsync(stop.Token);
        try
        {
            await proxy.Listening.WaitAsync(TimeSpan.FromSeconds(5));
            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(IPAddress.Loopback, proxy.GetBoundPort("essai"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            await client.GetStream().WriteAsync(payload);
            var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            started.Stop();

            Assert.Equal(payload, actual);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"Le relay a attendu {started.Elapsed}.");
            Assert.True(tap.DroppedChunks > 0);
            Assert.InRange(tap.PendingCount, 0, 1);
            Assert.Equal(payload.LongLength, proxy.BytesClientToServer);
        }
        finally
        {
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            upstreamListener.Stop();
            await accept;
        }
    }

    [Fact]
    public async Task End_to_end_smoke_test()
    {
        var error = await SmokeTest.RunAsync();
        Assert.Null(error);
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
