using Overwatch.Config;
using Overwatch.Decode;
using Overwatch.Logging;
using Overwatch.Market;
using Overwatch.Persist;
using Overwatch.Watchdog;

namespace Overwatch.Proxy;

public sealed class DiagnosticRuntime : IAsyncDisposable
{
    private readonly CancellationTokenSource _network = new();
    private readonly WindowsConfig _windows;
    private Task? _proxyTask;
    private Task? _decodeTask;
    private Task? _storeTask;
    private Task? _titlesTask;
    private int _disposed;

    private DiagnosticRuntime(
        TcpRelayProxy proxy,
        ObservationTap tap,
        SqliteSink store,
        DecodeWorker decoder,
        DecoderWatchdog watchdog,
        RuleCatalog rules,
        WindowsConfig windows)
    {
        Proxy = proxy;
        Tap = tap;
        Store = store;
        Decoder = decoder;
        Watchdog = watchdog;
        Rules = rules;
        _windows = windows;
    }

    public TcpRelayProxy Proxy { get; }

    public ObservationTap Tap { get; }

    public SqliteSink Store { get; }

    public DecodeWorker Decoder { get; }

    public DecoderWatchdog Watchdog { get; }

    public RuleCatalog Rules { get; }

    public static DiagnosticRuntime Create(AppConfig config, RuleCatalog rules, CaptureRecorder? captures = null)
    {
        var tap = new ObservationTap(config.Decode.QueueCapacity);
        var store = new SqliteSink(config.Sqlite.Path, config.Sqlite.FlushIntervalMs, config.Sqlite.FlushBatchSize);
        var watchdog = new DecoderWatchdog(TimeSpan.FromMilliseconds(config.Watchdog.LatencyWarnMs));
        var decoder = new DecodeWorker(
            tap,
            rules,
            store,
            watchdog,
            config.Decode.MaxBufferBytes,
            config.Decode.MaxPayloadStored,
            config.Decode.SliceMs);
        var proxy = new TcpRelayProxy(config, tap, captures);
        return new DiagnosticRuntime(proxy, tap, store, decoder, watchdog, rules, config.Windows);
    }

    public void Start()
    {
        _storeTask = Store.RunAsync();
        _decodeTask = Decoder.RunAsync();
        _proxyTask = Proxy.RunAsync(_network.Token);
        if (_windows.Enabled)
            _titlesTask = PollTitlesAsync(_network.Token);
    }

    private async Task PollTitlesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Decoder.PostTitles(WindowTitleReader.Read());
            try
            {
                await Task.Delay(_windows.PollMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void RequestStop() => _network.Cancel();

    public async Task WhenStoppedAsync()
    {
        if (_proxyTask is null)
            return;
        await _proxyTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _network.Cancel();
        if (_titlesTask is not null)
        {
            try
            {
                await _titlesTask;
            }
            catch (Exception exception)
            {
                ConsoleLog.Warn($"Lecture des titres de fenêtres arrêtée ({exception.Message}).");
            }
        }

        if (_proxyTask is not null)
        {
            try
            {
                await _proxyTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or ConfigException)
            {
            }
            catch (Exception exception)
            {
                ConsoleLog.Error($"Arrêt du proxy : {exception.Message}");
            }
        }

        Tap.Complete();
        if (_decodeTask is not null)
            await _decodeTask;

        Store.Complete();
        if (_storeTask is not null)
            await _storeTask;

        Rules.Dispose();
        _network.Dispose();
    }
}
