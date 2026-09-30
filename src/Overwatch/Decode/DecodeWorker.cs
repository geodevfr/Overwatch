using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Overwatch.Market;
using Overwatch.Persist;
using Overwatch.Proxy;
using Overwatch.Watchdog;

namespace Overwatch.Decode;

public sealed class DecodeWorker
{
    private readonly ObservationTap _tap;
    private readonly RuleCatalog _rules;
    private readonly SqliteSink _store;
    private readonly DecoderWatchdog _watchdog;
    private readonly int _maxBufferBytes;
    private readonly int _previewCap;
    private readonly int _sliceMs;
    private readonly SessionMarket _market = new();
    private readonly ConcurrentQueue<string[]> _titles = new();
    private long _messages;
    private long _gaps;
    private long _resyncs;
    private long _withheld;

    public DecodeWorker(
        ObservationTap tap,
        RuleCatalog rules,
        SqliteSink store,
        DecoderWatchdog watchdog,
        int maxBufferBytes,
        int previewCap,
        int sliceMs = 1)
    {
        _tap = tap;
        _rules = rules;
        _store = store;
        _watchdog = watchdog;
        _maxBufferBytes = maxBufferBytes;
        _previewCap = previewCap;
        _sliceMs = Math.Max(1, sliceMs);
    }

    public SessionMarket Market => _market;

    public void PostTitles(IReadOnlyList<string> titles) => _titles.Enqueue(titles.ToArray());

    public long Messages => Interlocked.Read(ref _messages);

    public long Gaps => Interlocked.Read(ref _gaps);

    public long Resyncs => Interlocked.Read(ref _resyncs);

    public long Withheld => Interlocked.Read(ref _withheld);

    public async Task RunAsync()
    {
        var states = new Dictionary<string, ConnectionState>(StringComparer.Ordinal);
        var operations = 0;
        var slice = Stopwatch.StartNew();
        await foreach (var chunk in _tap.ReadAllAsync())
        {
            while (_titles.TryDequeue(out var titles))
            {
                foreach (var row in _market.CorrelateTitles(titles))
                    _store.TryEnqueueMarket(Array.Empty<PriceRow>(), row);
            }

            var started = Stopwatch.GetTimestamp();
            var packetId = ChunkId(chunk);
            try
            {
                packetId = Process(chunk, states) ?? packetId;
            }
            finally
            {
                _watchdog.Record(Stopwatch.GetElapsedTime(started), packetId);
            }

            if ((++operations & 0x3FF) == 0)
                Sweep(states);

            if (slice.ElapsedMilliseconds >= _sliceMs)
            {
                await Task.Yield();
                slice.Restart();
            }
        }
    }

    private string? Process(TapChunk chunk, Dictionary<string, ConnectionState> states)
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
        var withheldBefore = reassembler.Withheld;
        var catalog = _rules.Current;
        var frames = reassembler.Push(
            chunk.Payload,
            catalog,
            chunk.Direction,
            state.Conversation,
            _previewCap);
        var resyncDelta = reassembler.ResyncBytes - resyncBefore;
        if (resyncDelta > 0)
            Interlocked.Add(ref _resyncs, resyncDelta);
        var withheldDelta = reassembler.Withheld - withheldBefore;
        if (withheldDelta > 0)
            Interlocked.Add(ref _withheld, withheldDelta);

        string? packetId = null;
        foreach (var frame in frames)
        {
            packetId = packetId is null ? frame.RuleId : packetId + "+" + frame.RuleId;
            CompiledRule? rule = null;
            foreach (var candidate in catalog.Rules)
            {
                if (candidate.Id == frame.RuleId)
                {
                    rule = candidate;
                    break;
                }
            }

            IReadOnlyList<PriceRow> prices = Array.Empty<PriceRow>();
            SessionRow? session = null;
            if (rule is not null && rule.Kind != ObservationKind.Trace)
            {
                var update = _market.Accept(chunk.ConnectionId, rule, frame);
                prices = update.Prices;
                session = update.Session;
            }

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
            if (prices.Count > 0 || session is not null)
                _store.TryEnqueueMarket(prices, session);
            Interlocked.Increment(ref _messages);
        }

        return packetId;
    }

    private static string ChunkId(TapChunk chunk)
    {
        var prefixLength = Math.Min(4, chunk.Payload.Length);
        var prefix = Convert.ToHexString(chunk.Payload.AsSpan(0, prefixLength));
        var direction = chunk.Direction == Direction.ClientToServer ? "c2s" : "s2c";
        return $"flux:{direction}:{chunk.Payload.Length}:{prefix}";
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
