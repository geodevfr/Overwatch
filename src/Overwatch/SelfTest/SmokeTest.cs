using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Overwatch.Config;
using Overwatch.Decode;
using Overwatch.Hosts;
using Overwatch.Proxy;
using Overwatch.Sample;

namespace Overwatch.SelfTest;

public static class SmokeTest
{
    public static async Task<string?> RunAsync()
    {
        var hosts = CheckHosts();
        if (hosts is not null)
            return hosts;
        return await CheckRelayAsync();
    }

    private static string? CheckHosts()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts");
        try
        {
            var path = Path.Combine(directory.FullName, "hosts");
            const string original = "127.0.0.1 localhost\n# conserve\n";
            File.WriteAllText(path, original);
            var manager = new HostsFileManager(path);
            var entry = HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1");

            if (manager.Install(new[] { entry }) is not { Ok: true, Change: HostsChange.Installed })
                return "Le premier ajout hosts n'a pas écrit le bloc.";
            if (manager.Install(new[] { entry }) is not { Ok: true, Change: HostsChange.Unchanged })
                return "Le second ajout hosts n'est pas idempotent.";

            var text = File.ReadAllText(path);
            if (Count(text, HostsFileManager.BeginMarker) != 1 || Count(text, HostsFileManager.EndMarker) != 1)
                return "Le bloc hosts est dupliqué.";
            if (!text.Contains("127.0.0.1 localhost", StringComparison.Ordinal) || !text.Contains("# conserve", StringComparison.Ordinal))
                return "Les lignes hors bloc ont été modifiées.";

            File.WriteAllText(path, original + HostsFileManager.BeginMarker + "\n10.1.2.3 residuel.exemple\n");
            if (manager.Install(new[] { entry }) is not { Ok: true, Change: HostsChange.Installed })
                return "La reprise après bloc tronqué a échoué.";
            var recovered = File.ReadAllText(path);
            if (recovered.Contains("residuel.exemple", StringComparison.Ordinal))
                return "Un bloc sans marqueur de fin n'a pas été retiré.";
            if (Count(recovered, HostsFileManager.BeginMarker) != 1)
                return "La reprise n'a pas réécrit un bloc unique.";

            if (manager.Remove() is not { Ok: true, Change: HostsChange.Removed })
                return "Le retrait du bloc hosts a échoué.";
            if (manager.Remove() is not { Ok: true, Change: HostsChange.Unchanged })
                return "Le second retrait hosts n'est pas idempotent.";
            if (File.ReadAllText(path) != original)
                return "Le fichier hosts n'est pas revenu à son contenu d'origine.";
            return null;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<string?> CheckRelayAsync()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-relay");
        var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        DiagnosticRuntime? runtime = null;
        try
        {
            var hello = FictionalProtocol.Hello(1, 7, 9);
            var tick = FictionalProtocol.Tick(3, 0x01020304, 0x00FF00FF);
            var requestSeen = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var upstreamTask = Task.Run(async () =>
            {
                using var remote = await upstream.AcceptTcpClientAsync();
                remote.NoDelay = true;
                var stream = remote.GetStream();
                var received = new byte[hello.Length];
                await stream.ReadExactlyAsync(received);
                requestSeen.TrySetResult(received);
                await stream.WriteAsync(tick.AsMemory(0, 6));
                await stream.WriteAsync(tick.AsMemory(6));
                await Task.Delay(300);
            });

            var config = new AppConfig
            {
                AcceptTerms = true,
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
                },
                Decode = new DecodeConfig { QueueCapacity = 128, MaxBufferBytes = 65_536, MaxPayloadStored = 64 },
                Sqlite = new SqliteConfig
                {
                    Path = Path.Combine(directory.FullName, "essai.db"),
                    FlushIntervalMs = 50,
                    FlushBatchSize = 20
                },
                Watchdog = new WatchdogConfig { LatencyWarnMs = 50, ReportIntervalMs = 1000 },
                RulesPath = "inline"
            };

            var rules = RuleCatalog.Create(RuleCompiler.Compile(RuleLoader.Parse(SampleRules.Yaml)));
            runtime = DiagnosticRuntime.Create(config, rules);
            runtime.Start();
            await runtime.Proxy.Listening.WaitAsync(TimeSpan.FromSeconds(5));

            using var client = new TcpClient();
            client.NoDelay = true;
            await client.ConnectAsync(IPAddress.Loopback, runtime.Proxy.GetBoundPort("essai"));
            var clientStream = client.GetStream();
            await clientStream.WriteAsync(hello.AsMemory(0, 3));
            await clientStream.WriteAsync(hello.AsMemory(3, 3));
            await clientStream.WriteAsync(hello.AsMemory(6));

            var response = new byte[tick.Length];
            await clientStream.ReadExactlyAsync(response);
            var forwarded = await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!forwarded.AsSpan().SequenceEqual(hello))
                return "Les octets client → serveur ont été modifiés.";
            if (!response.AsSpan().SequenceEqual(tick))
                return "Les octets serveur → client ont été modifiés.";

            await upstreamTask.WaitAsync(TimeSpan.FromSeconds(5));
            client.Close();
            await runtime.DisposeAsync();
            runtime = null;

            var rows = Persist.SqliteSink.ReadAll(config.Sqlite.Path);
            var helloRow = rows.FirstOrDefault(row => row.RuleId == "session_hello");
            var tickRow = rows.FirstOrDefault(row => row.RuleId == "market_tick");
            if (helloRow is null || tickRow is null)
                return $"Messages attendus absents de SQLite ({rows.Count} ligne(s)).";

            using var fields = JsonDocument.Parse(tickRow.FieldsJson);
            if (fields.RootElement.GetProperty("item_id").GetString() != "16909060")
                return "item_id mal décodé.";
            if (fields.RootElement.GetProperty("price").GetString() != "16711935")
                return "price mal décodé.";
            if (tickRow.Direction != "s2c" || helloRow.Direction != "c2s")
                return "Sens des messages incorrect.";
            return null;
        }
        catch (Exception exception)
        {
            return $"Auto-test réseau : {exception.GetType().Name} : {exception.Message}";
        }
        finally
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
            upstream.Stop();
            directory.Delete(recursive: true);
        }
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
