using Overwatch.Config;
using Overwatch.Logging;

namespace Overwatch.Decode;

/// <summary>
/// Instantané immuable des règles, remplacé d'un bloc lors d'un rechargement.
/// Une erreur de syntaxe conserve l'ensemble précédent.
/// </summary>
public sealed class RuleCatalog : IDisposable
{
    private readonly string? _path;
    private RuleSet _current;
    private FileSystemWatcher? _watcher;
    private readonly object _reloadGate = new();
    private CancellationTokenSource? _debounce;
    private int _disposed;

    private RuleCatalog(string? path, RuleSet current)
    {
        _path = path;
        _current = current;
    }

    public RuleSet Current => Volatile.Read(ref _current);

    public event Action? Reloaded;

    public static RuleCatalog Create(RuleSet rules) => new(null, rules);

    public static RuleCatalog FromFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var catalog = new RuleCatalog(fullPath, RuleCompiler.Compile(RuleLoader.LoadFile(fullPath)));
        catalog.Watch();
        return catalog;
    }

    public bool Reload()
    {
        if (_path is null)
            return false;

        RuleSet? next = null;
        lock (_reloadGate)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    next = RuleCompiler.Compile(RuleLoader.LoadFile(_path));
                    Volatile.Write(ref _current, next);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(40);
                }
                catch (Exception exception) when (exception is ConfigException or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
                {
                    ConsoleLog.Error($"Rechargement ignoré ({exception.Message}). Les règles précédentes restent actives.");
                    return false;
                }
            }
        }

        if (next is null)
            return false;

        ConsoleLog.Info($"Règles rechargées : {next.Rules.Count} depuis {_path}");
        Reloaded?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _watcher?.Dispose();
        lock (_reloadGate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
        }
    }

    private void Watch()
    {
        var directory = Path.GetDirectoryName(_path);
        var name = Path.GetFileName(_path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
            return;

        _watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        CancellationToken token;
        lock (_reloadGate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            token = _debounce.Token;
        }

        _ = DebounceAsync(token);
    }

    private async Task DebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            Reload();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
