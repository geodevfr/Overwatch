namespace GameDiag.Proxy;

public sealed class TapChunk
{
    public required string ConnectionId { get; init; }

    public required string ListenerName { get; init; }

    public required Decode.Direction Direction { get; init; }

    public required long Sequence { get; init; }

    public required byte[] Payload { get; init; }
}

/// <summary>
/// File bornée à un seul lecteur. Publish copie les octets et revient tout de suite :
/// si la file est pleine, l'échantillon le plus ancien est abandonné.
/// Le thread de relay n'attend jamais le décodeur.
/// </summary>
public sealed class ObservationTap
{
    private readonly int _capacity;
    private readonly Queue<TapChunk> _queue;
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _waiter;
    private bool _completed;
    private long _droppedChunks;
    private long _publishedChunks;

    public ObservationTap(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _queue = new Queue<TapChunk>(Math.Min(capacity, 1024));
    }

    public int Capacity => _capacity;

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public long PublishedChunks => Interlocked.Read(ref _publishedChunks);

    public void Publish(
        string connectionId,
        string listenerName,
        Decode.Direction direction,
        long sequence,
        ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;

        var copy = new byte[data.Length];
        data.CopyTo(copy);
        var chunk = new TapChunk
        {
            ConnectionId = connectionId,
            ListenerName = listenerName,
            Direction = direction,
            Sequence = sequence,
            Payload = copy
        };

        TaskCompletionSource<bool>? wake = null;
        lock (_gate)
        {
            if (_completed)
            {
                Interlocked.Increment(ref _droppedChunks);
                return;
            }

            if (_queue.Count == _capacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _droppedChunks);
            }

            _queue.Enqueue(chunk);
            Interlocked.Increment(ref _publishedChunks);
            if (_waiter is not null)
            {
                wake = _waiter;
                _waiter = null;
            }
        }

        wake?.TrySetResult(true);
    }

    public void Complete()
    {
        TaskCompletionSource<bool>? wake;
        lock (_gate)
        {
            _completed = true;
            wake = _waiter;
            _waiter = null;
        }

        wake?.TrySetResult(true);
    }

    public async IAsyncEnumerable<TapChunk> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var chunk = await ReadAsync(cancellationToken);
            if (chunk is null)
                yield break;
            yield return chunk;
        }
    }

    private async Task<TapChunk?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (_queue.Count > 0)
                    return _queue.Dequeue();
                if (_completed)
                    return null;

                _waiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = _waiter.Task;
            }

            try
            {
                await waitTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_waiter?.Task, waitTask) && !waitTask.IsCompleted)
                        _waiter = null;
                }

                throw;
            }
        }
    }
}
