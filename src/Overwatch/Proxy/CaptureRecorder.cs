using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Overwatch.Decode;

namespace Overwatch.Proxy;

/// <summary>
/// Copie disque des octets déjà relaiés. L'écriture est hors du chemin réseau :
/// une file pleine abandonne l'échantillon, elle n'attend pas le disque.
/// </summary>
public sealed class CaptureRecorder : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Channel<Item> _channel;
    private readonly ConcurrentDictionary<string, byte> _closed = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private int _armed;
    private long _dropped;

    public CaptureRecorder(string directory)
    {
        _directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        _channel = Channel.CreateBounded<Item>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(WriteLoop);
    }

    public string Directory => _directory;

    public bool Armed => Volatile.Read(ref _armed) == 1;

    public long Dropped => Interlocked.Read(ref _dropped);

    public void SetArmed(bool armed) => Volatile.Write(ref _armed, armed ? 1 : 0);

    public void Offer(string connectionId, string listenerName, Direction direction, byte[] data, int length)
    {
        if (!Armed || length <= 0 || _closed.ContainsKey(connectionId))
            return;

        var copy = new byte[length];
        Buffer.BlockCopy(data, 0, copy, 0, length);
        if (!_channel.Writer.TryWrite(new Item(connectionId, listenerName, direction, copy, End: false, Drain: null)))
            Interlocked.Increment(ref _dropped);
    }

    public void End(string connectionId)
    {
        _closed.TryAdd(connectionId, 0);
        _channel.Writer.TryWrite(new Item(connectionId, "", Direction.ClientToServer, null, End: true, Drain: null));
    }

    public async Task DrainAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (!_channel.Writer.TryWrite(new Item("", "", Direction.ClientToServer, null, End: false, done)))
            await Task.Delay(5);
        await done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker;
    }

    private async Task WriteLoop()
    {
        var streams = new Dictionary<string, CaptureStreams>(StringComparer.Ordinal);
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync())
            {
                if (item.Drain is not null)
                {
                    foreach (var pair in streams.Values)
                    {
                        await pair.Bin.FlushAsync();
                        await pair.Index.FlushAsync();
                    }

                    item.Drain.TrySetResult();
                    continue;
                }

                if (item.End)
                {
                    await ClosePairAsync(streams, item.ConnectionId);
                    continue;
                }

                if (item.Data is null)
                    continue;

                var key = Key(item.ConnectionId, item.Direction);
                if (!streams.TryGetValue(key, out var open))
                {
                    var path = Path.Combine(_directory, FileName(item.ConnectionId, item.Direction));
                    open = new CaptureStreams(
                        new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                        new FileStream(path + ".idx", FileMode.Append, FileAccess.Write, FileShare.Read));
                    streams[key] = open;
                    var meta = Path.Combine(_directory, item.ConnectionId + ".listener");
                    if (!File.Exists(meta))
                        await File.WriteAllTextAsync(meta, item.ListenerName);
                }

                var length = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(length, item.Data.Length);
                await open.Index.WriteAsync(length);
                await open.Bin.WriteAsync(item.Data);
            }
        }
        finally
        {
            foreach (var open in streams.Values)
            {
                await open.Bin.DisposeAsync();
                await open.Index.DisposeAsync();
            }
        }
    }

    private static async Task ClosePairAsync(Dictionary<string, CaptureStreams> streams, string connectionId)
    {
        foreach (var direction in new[] { Direction.ClientToServer, Direction.ServerToClient })
        {
            var key = Key(connectionId, direction);
            if (!streams.Remove(key, out var open))
                continue;
            await open.Bin.FlushAsync();
            await open.Index.FlushAsync();
            await open.Bin.DisposeAsync();
            await open.Index.DisposeAsync();
        }
    }

    public static string FileName(string connectionId, Direction direction) =>
        connectionId + (direction == Direction.ClientToServer ? ".c2s.bin" : ".s2c.bin");

    private static string Key(string connectionId, Direction direction) => connectionId + "/" + direction;

    private sealed record CaptureStreams(FileStream Bin, FileStream Index);

    private readonly record struct Item(
        string ConnectionId,
        string ListenerName,
        Direction Direction,
        byte[]? Data,
        bool End,
        TaskCompletionSource? Drain);
}
