using Overwatch.Config;
using Overwatch.Decode;
using Overwatch.Hosts;
using Overwatch.Logging;
using Overwatch.Market;
using Overwatch.Persist;
using Overwatch.Proxy;

namespace Overwatch.Ui;

public sealed class Dashboard : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _configPath;
    private DiagnosticRuntime? _runtime;
    private HostsSession? _hosts;
    private CancellationTokenSource? _reportStop;
    private Task? _reportTask;
    private string? _error;
    private string? _hostsWarning;

    public Dashboard(string configPath, CaptureRecorder captures)
    {
        _configPath = configPath;
        Captures = captures;
    }

    public CaptureRecorder Captures { get; }

    public string ConfigPath => _configPath;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _runtime is not null;
        }
    }

    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
            throw new ConfigException($"Fichier de configuration introuvable : {_configPath}");
        return ConfigLoader.Load(_configPath);
    }

    public IReadOnlyList<string> Save(ConfigForm form)
    {
        if (IsRunning)
            return new[] { "Arrêtez l'observateur avant de modifier la configuration." };

        var config = File.Exists(_configPath)
            ? ConfigLoader.Load(_configPath)
            : new AppConfig { RulesPath = "rules.yaml", BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(_configPath)) ?? "" };
        ConfigForms.Apply(config, form);
        if (string.IsNullOrWhiteSpace(config.BaseDirectory))
            config.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(_configPath)) ?? "";
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
            return errors;

        ConfigWriter.Write(_configPath, config);
        ConsoleLog.Info($"Configuration enregistrée : {_configPath}");
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<string>> StartAsync()
    {
        if (IsRunning)
            return new[] { "L'observateur tourne déjà." };

        AppConfig config;
        try
        {
            config = Load();
        }
        catch (ConfigException exception)
        {
            return new[] { exception.Message };
        }

        var errors = ConfigValidator.Validate(config).ToList();
        if (config.Listeners.Any(listener => listener.UpstreamHost.StartsWith("203.0.113.", StringComparison.Ordinal)))
            errors.Add("203.0.113.0/24 est un exemple. Indiquez l'adresse IP réelle du serveur de jeu.");
        if (!File.Exists(config.RulesPath))
            errors.Add($"Fichier de règles introuvable : {config.RulesPath}");
        if (errors.Count > 0)
            return errors;

        if (!Path.IsPathRooted(config.Sqlite.Path))
            config.Sqlite.Path = Path.Combine(config.BaseDirectory, config.Sqlite.Path);

        DiagnosticRuntime? runtime = null;
        HostsSession? hosts = null;
        string? hostsWarning = null;
        try
        {
            var attached = HostsInstaller.Attach(config);
            hosts = attached.Session;
            hostsWarning = attached.Warning;
            var rules = RuleCatalog.FromFile(config.RulesPath);
            runtime = DiagnosticRuntime.Create(config, rules, Captures);
            runtime.Start();
            await runtime.Proxy.Listening.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            if (runtime is not null)
                await runtime.DisposeAsync();
            hosts?.Dispose();
            var message = exception.GetBaseException().Message;
            lock (_gate)
                _error = message;
            ConsoleLog.Error(message);
            return new[] { message };
        }

        var stop = new CancellationTokenSource();
        lock (_gate)
        {
            _runtime = runtime;
            _hosts = hosts;
            _hostsWarning = hostsWarning;
            _reportStop = stop;
            _error = null;
            _reportTask = ReportAsync(runtime, TimeSpan.FromMilliseconds(config.Watchdog.ReportIntervalMs), config.Watchdog.LatencyWarnMs, stop.Token);
        }

        ConsoleLog.Info("Observateur démarré.");
        return Array.Empty<string>();
    }

    public async Task StopAsync()
    {
        DiagnosticRuntime? runtime;
        HostsSession? hosts;
        CancellationTokenSource? reportStop;
        Task? report;
        lock (_gate)
        {
            runtime = _runtime;
            hosts = _hosts;
            reportStop = _reportStop;
            report = _reportTask;
            _runtime = null;
            _hosts = null;
            _reportStop = null;
            _reportTask = null;
        }

        if (runtime is null)
            return;

        runtime.RequestStop();
        reportStop?.Cancel();
        if (report is not null)
        {
            try
            {
                await report;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await runtime.DisposeAsync();
        if (hosts is not null)
        {
            var removed = hosts.Release();
            if (!removed.Ok)
            {
                lock (_gate)
                    _hostsWarning = HostsInstaller.ExplainFailure(removed.Detail);
            }
        }

        await Captures.DrainAsync();
        ConsoleLog.Info("Observateur arrêté.");
    }

    public object Status()
    {
        DiagnosticRuntime? runtime;
        string? error;
        string? hostsWarning;
        lock (_gate)
        {
            runtime = _runtime;
            error = _error;
            hostsWarning = _hostsWarning;
        }

        if (runtime is null)
        {
            return new
            {
                running = false,
                error,
                hostsWarning,
                bytesClientToServer = 0L,
                bytesServerToClient = 0L,
                sessions = 0,
                messages = 0L,
                gaps = 0L,
                withheld = 0L,
                tapDropped = 0L,
                captureArmed = Captures.Armed,
                captureDropped = Captures.Dropped,
                listeners = Array.Empty<object>()
            };
        }

        var listeners = new List<object>();
        try
        {
            foreach (var pair in runtime.Proxy.BoundPorts())
                listeners.Add(new { name = pair.Name, port = pair.Port });
        }
        catch (ObjectDisposedException)
        {
        }

        return new
        {
            running = true,
            error,
            hostsWarning,
            bytesClientToServer = runtime.Proxy.BytesClientToServer,
            bytesServerToClient = runtime.Proxy.BytesServerToClient,
            sessions = runtime.Proxy.ActiveConnections,
            messages = runtime.Decoder.Messages,
            gaps = runtime.Decoder.Gaps,
            withheld = runtime.Decoder.Withheld,
            tapDropped = runtime.Tap.DroppedChunks,
            captureArmed = Captures.Armed,
            captureDropped = Captures.Dropped,
            listeners
        };
    }

    public object Rules()
    {
        try
        {
            var config = Load();
            if (!File.Exists(config.RulesPath))
                return new { error = "Fichier de règles introuvable.", versions = Array.Empty<object>() };
            var file = RuleLoader.LoadFile(config.RulesPath);
            var compiled = RuleCompiler.Compile(file);
            return new
            {
                error = (string?)null,
                client = file.Client,
                activeRules = compiled.Rules.Count,
                versions = file.Detection.Select(entry => new
                {
                    version = entry.Version,
                    status = entry.Status,
                    pending = entry.Pending,
                    signatures = entry.Rules.Count
                })
            };
        }
        catch (Exception exception) when (exception is ConfigException or IOException or YamlDotNet.Core.YamlException)
        {
            return new { error = exception.Message, versions = Array.Empty<object>() };
        }
    }

    public object Journal()
    {
        try
        {
            var path = SqlitePath();
            if (!File.Exists(path))
                return new { observations = Array.Empty<object>(), prices = Array.Empty<object>() };

            var observations = SqliteSink.ReadAll(path)
                .TakeLast(30)
                .Select(row => new
                {
                    at = row.ObservedAt,
                    connectionId = row.ConnectionId,
                    direction = row.Direction,
                    ruleId = row.RuleId,
                    length = row.Length,
                    fields = row.FieldsJson
                });
            var prices = SqliteSink.ReadPrices(path).TakeLast(40).Select(row => new
            {
                server = row.ServerKey,
                itemId = row.ItemId,
                source = row.Source,
                quantity = row.LotQuantity,
                total = row.Total,
                unit = row.UnitPrice
            });
            return new { observations, prices };
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new { error = exception.Message, observations = Array.Empty<object>(), prices = Array.Empty<PriceRow>() };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private string SqlitePath()
    {
        var config = Load();
        return Path.IsPathRooted(config.Sqlite.Path)
            ? config.Sqlite.Path
            : Path.Combine(config.BaseDirectory, config.Sqlite.Path);
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
                $"relay c2s {runtime.Proxy.BytesClientToServer} o, s2c {runtime.Proxy.BytesServerToClient} o, " +
                $"sessions {runtime.Proxy.ActiveConnections}, messages {runtime.Decoder.Messages}, " +
                $"pertes tap {runtime.Tap.DroppedChunks}, écarts {runtime.Decoder.Gaps}");
            if (snapshot.Samples > 0 && snapshot.MaxSincePreviousSnapshot.TotalMilliseconds > warnMs)
            {
                var packet = string.IsNullOrEmpty(snapshot.Offender) ? "inconnu" : snapshot.Offender;
                ConsoleLog.Warn($"Décodage lent {snapshot.MaxSincePreviousSnapshot.TotalMilliseconds:0} ms, règle {packet}. Le relais continue.");
            }
        }
    }
}
