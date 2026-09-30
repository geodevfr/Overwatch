using System.Diagnostics;
using System.Text.Json;
using GameDiag.Persist;
using GameDiag.Proxy;
using GameDiag.Watchdog;

namespace GameDiag.Decode;

public sealed class DecodeWorker
{
    private readonly ObservationTap _tap;
    private readonly RuleCatalog _rules;
    private readonly SqliteSink _store;
    private readonly DecoderWatchdog _watchdog;
    private readonly int _maxBufferBytes;
    private readonly int _previewCap;
    private long _messages;
    private long _gaps;
    private long _resyncs;

    public DecodeWorker(
        ObservationTap tap,
        RuleCatalog rules,
        SqliteSink store,
        DecoderWatchdog watchdog,
        int maxBufferBytes,
        int previewCap)
    {
        _tap = tap;
        _rules = rules;
        _store = store;
        _watchdog = watchdog;
        _maxBufferBytes = maxBufferBytes;
        _previewCap = previewCap;
    }

    public long Messages => Interlocked.Read(ref _messages);

    public long Gaps => Interlocked.Read(ref _gaps);

    public long Resyncs => Interlocked.Read(ref _resyncs);

    public async Task RunAsync()
    {
        var states = new Dictionary<string, ConnectionState>(StringComparer.Ordinal);
        var operations = 0;
        await foreach (var chunk in _tap.ReadAllAsync())
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                Process(chunk, states);
            }
            finally
            {
                _watchdog.Record(Stopwatch.GetElapsedTime(started));
            }

            if ((++operations & 0x3FF) == 0)
                Sweep(states);
        }
    }

    private void Process(TapChunk chunk, Dictionary<string, ConnectionState> states)
    {
        if (!states.TryGetValue(chunk.ConnectionId, out var state))
        {
            state = new ConnectionState(_maxBufferBytes);
            states[chunk.ConnectionId] = state;
        }

        state.LastSeen = DateTimeOffset.UtcNow;
        var expected = state.Expected(chunk.Direction);
        if (chunk.Sequence != expected)
        {
            state.Reassembler(chunk.Direction).Reset();
            state.Conversation.Clear();
            Interlocked.Increment(ref _gaps);
        }

        state.SetExpected(chunk.Direction, chunk.Sequence + 1);
        var reassembler = state.Reassembler(chunk.Direction);
        var resyncBefore = reassembler.ResyncBytes;
        var frames = reassembler.Push(
            chunk.Payload,
            _rules.Current,
            chunk.Direction,
            state.Conversation,
            _previewCap);
        var resyncDelta = reassembler.ResyncBytes - resyncBefore;
        if (resyncDelta > 0)
            Interlocked.Add(ref _resyncs, resyncDelta);

        foreach (var frame in frames)
        {
            var observation = new Observation(
                DateTimeOffset.UtcNow,
                chunk.ConnectionId,
                chunk.ListenerName,
                chunk.Direction == Direction.ClientToServer ? "c2s" : "s2c",
                frame.RuleId,
                frame.Length,
                JsonSerializer.Serialize(frame.Fields),
                frame.PayloadHex);
            _store.TryEnqueue(observation);
            Interlocked.Increment(ref _messages);
        }
    }

    private static void Sweep(Dictionary<string, ConnectionState> states)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(-2);
        List<string>? stale = null;
        foreach (var pair in states)
        {
            if (pair.Value.LastSeen >= deadline)
                continue;
            stale ??= new List<string>();
            stale.Add(pair.Key);
        }

        if (stale is null)
            return;
        foreach (var key in stale)
            states.Remove(key);
    }

    private sealed class ConnectionState
    {
        public ConnectionState(int maxBufferBytes)
        {
            ClientToServer = new FrameReassembler(maxBufferBytes);
            ServerToClient = new FrameReassembler(maxBufferBytes);
        }

        public FrameReassembler ClientToServer { get; }

        public FrameReassembler ServerToClient { get; }

        public ConversationState Conversation { get; } = new();

        public long ExpectedClientToServer { get; set; } = 1;

        public long ExpectedServerToClient { get; set; } = 1;

        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

        public FrameReassembler Reassembler(Direction direction) =>
            direction == Direction.ClientToServer ? ClientToServer : ServerToClient;

        public long Expected(Direction direction) =>
            direction == Direction.ClientToServer ? ExpectedClientToServer : ExpectedServerToClient;

        public void SetExpected(Direction direction, long value)
        {
            if (direction == Direction.ClientToServer)
                ExpectedClientToServer = value;
            else
                ExpectedServerToClient = value;
        }
    }
}
