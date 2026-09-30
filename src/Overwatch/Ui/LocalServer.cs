using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Overwatch.Config;
using Overwatch.Hosts;
using Overwatch.Logging;
using Overwatch.Proxy;

namespace Overwatch.Ui;

public sealed class LocalServerOptions
{
    public int Port { get; init; } = 47321;

    public string? ConfigPath { get; init; }

    public string? CaptureDirectory { get; init; }

    public string? WebRoot { get; init; }
}

public sealed class LocalServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CaptureRecorder _captures;

    private LocalServer(WebApplication app, Dashboard dashboard, CaptureRecorder captures, Uri baseAddress)
    {
        _app = app;
        _captures = captures;
        Dashboard = dashboard;
        BaseAddress = baseAddress;
    }

    public Dashboard Dashboard { get; }

    public Uri BaseAddress { get; }

    public static async Task<LocalServer> StartAsync(LocalServerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new LocalServerOptions();
        var configPath = options.ConfigPath ?? DefaultConfigPath();
        var captureDirectory = options.CaptureDirectory
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? AppContext.BaseDirectory, "captures");
        var captures = new CaptureRecorder(captureDirectory);
        var dashboard = new Dashboard(configPath, captures);
        dashboard.NoteHostsWarning(SweepHosts(configPath));

        var assemblyDir = Path.GetDirectoryName(typeof(LocalServer).Assembly.Location) ?? AppContext.BaseDirectory;
        var webRoot = options.WebRoot ?? Path.Combine(assemblyDir, "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = assemblyDir,
            WebRootPath = webRoot
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

        var app = builder.Build();
        if (Directory.Exists(webRoot))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
        }

        MapApi(app, dashboard);

        await app.StartAsync(cancellationToken);
        var address = new Uri(app.Urls.First());
        return new LocalServer(app, dashboard, captures, address);
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Dashboard.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _captures.DisposeAsync();
    }

    private static void MapApi(WebApplication app, Dashboard dashboard)
    {
        app.MapGet("/api/status", () => Results.Ok(dashboard.Status()));

        app.MapGet("/api/log", (long since) =>
        {
            var lines = ConsoleLog.Since(since).Select(entry => new
            {
                id = entry.Id,
                at = entry.At.ToString("HH:mm:ss"),
                level = entry.Level,
                message = entry.Message
            });
            return Results.Ok(lines);
        });

        app.MapGet("/api/config", () =>
        {
            try
            {
                var config = dashboard.Load();
                return Results.Ok(new { path = dashboard.ConfigPath, form = ConfigForms.From(config), errors = Array.Empty<string>() });
            }
            catch (ConfigException exception)
            {
                return Results.Ok(new { path = dashboard.ConfigPath, form = new ConfigForm(), errors = new[] { exception.Message } });
            }
        });

        app.MapPut("/api/config", (ConfigForm form) =>
        {
            var errors = dashboard.Save(form);
            return errors.Count == 0
                ? Results.Ok(new { errors })
                : Results.BadRequest(new { errors });
        });

        app.MapPost("/api/relay/start", async () =>
        {
            var errors = await dashboard.StartAsync();
            return errors.Count == 0 ? Results.Ok(new { errors }) : Results.BadRequest(new { errors });
        });

        app.MapPost("/api/relay/stop", async () =>
        {
            await dashboard.StopAsync();
            return Results.Ok(new { stopped = true });
        });

        app.MapPost("/api/capture", (CaptureToggle toggle) =>
        {
            dashboard.Captures.SetArmed(toggle.Armed);
            ConsoleLog.Info(toggle.Armed ? "Capture disque armée." : "Capture disque arrêtée. Les fichiers déjà écrits restent.");
            return Results.Ok(new { armed = dashboard.Captures.Armed });
        });

        app.MapGet("/api/captures", () => Results.Ok(CaptureCatalog.List(dashboard.Captures.Directory)));

        app.MapGet("/api/captures/{id}/{direction}", (string id, string direction) =>
        {
            var path = CaptureCatalog.Resolve(dashboard.Captures.Directory, id, direction);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "application/octet-stream", Path.GetFileName(path));
        });

        app.MapGet("/api/rules", () => Results.Ok(dashboard.Rules()));

        app.MapGet("/api/journal", () => Results.Ok(dashboard.Journal()));

        app.MapGet("/api/connections", () => Results.Ok(ConnectionSurvey.EstablishedRemotes()));

        app.MapGet("/api/hosts", () =>
        {
            try
            {
                return Results.Ok(HostsInstaller.Glance(dashboard.Load()));
            }
            catch (ConfigException exception)
            {
                return Results.Ok(new HostsGlance(HostsFileManager.DefaultPath, false, exception.Message));
            }
        });

        app.MapPost("/api/hosts/remove", () =>
        {
            if (dashboard.IsRunning)
                return Results.BadRequest(new { error = "Arrêtez l'observateur avant de retirer le bloc hosts." });
            try
            {
                var glance = HostsInstaller.Glance(dashboard.Load());
                var attempt = new HostsFileManager(glance.Path, new HostsJournal(HostsJournal.DefaultPath)).Remove();
                return attempt.Ok
                    ? Results.Ok(new { change = HostsSession.Describe(attempt.Change), path = glance.Path })
                    : Results.BadRequest(new { error = HostsInstaller.ExplainFailure(attempt.Detail) });
            }
            catch (Exception exception)
            {
                return Results.BadRequest(new { error = HostsInstaller.ExplainFailure(exception.Message) });
            }
        });

        app.MapPost("/api/hosts/cleanup-task", () =>
        {
            var attempt = CleanupTasks.Install();
            return attempt.Ok
                ? Results.Ok(new { detail = attempt.Detail })
                : Results.BadRequest(new { error = attempt.Detail });
        });
    }

    private static string? SweepHosts(string configPath)
    {
        try
        {
            string? hostsPath = null;
            if (File.Exists(configPath))
                hostsPath = ConfigLoader.Load(configPath).Hosts.Path;
            return HostsInstaller.SweepLeftover(hostsPath);
        }
        catch (Exception exception)
        {
            return HostsInstaller.ExplainFailure(exception.Message);
        }
    }

    private static string DefaultConfigPath()
    {
        var current = Path.Combine(Directory.GetCurrentDirectory(), "config.yaml");
        if (File.Exists(current))
            return current;
        return Path.Combine(AppContext.BaseDirectory, "config.yaml");
    }
}

public sealed class CaptureToggle
{
    public bool Armed { get; set; }
}
