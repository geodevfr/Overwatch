using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GameDiag.Decode;

public sealed class ParsedFrame
{
    public required string RuleId { get; init; }

    public required int Length { get; init; }

    public required Dictionary<string, string> Fields { get; init; }

    public required string PayloadHex { get; init; }

    public IReadOnlyList<Dictionary<string, string>>? Entries { get; init; }
}

public static class FieldExtractor
{
    public static ParsedFrame Extract(ReadOnlySpan<byte> message, CompiledRule rule, int previewCap)
    {
        var fields = new Dictionary<string, string>(rule.Fields.Length, StringComparer.Ordinal);
        foreach (var field in rule.Fields)
        {
            if (!TryReadField(message, field, out var value))
                continue;
            fields[field.Name] = value;
        }

        IReadOnlyList<Dictionary<string, string>>? entries = null;
        if (rule.Repeat is not null)
            entries = ExtractEntries(message, rule.Repeat);

        var previewLength = previewCap <= 0 ? 0 : Math.Min(message.Length, previewCap);
        return new ParsedFrame
        {
            RuleId = rule.Id,
            Length = message.Length,
            Fields = fields,
            PayloadHex = Convert.ToHexString(message[..previewLength]),
            Entries = entries
        };
    }

    public static bool TryReadUtf8(ReadOnlySpan<byte> message, FieldSpec field, out string text)
    {
        text = "";
        if (field.Offset < 0 || field.Size <= 0 || field.Offset > message.Length - field.Size)
            return false;
        return TryDecodeUtf8(message.Slice(field.Offset, field.Size), out text);
    }

    private static bool TryReadField(ReadOnlySpan<byte> message, FieldSpec field, out string value)
    {
        value = "";
        if (field.Offset < 0 || field.Size < 0 || field.Offset > message.Length - field.Size)
            return false;
        if (field.Type == "utf8")
            return TryDecodeUtf8(message.Slice(field.Offset, field.Size), out value);
        value = Read(message.Slice(field.Offset, field.Size), field);
        return true;
    }

    private static List<Dictionary<string, string>>? ExtractEntries(ReadOnlySpan<byte> message, RepeatSpec repeat)
    {
        if (!repeat.TryReadCount(message, out var count))
            return null;
        var entries = new List<Dictionary<string, string>>(count);
        for (var index = 0; index < count; index++)
        {
            var start = repeat.EntryOffset + index * repeat.EntrySize;
            if (start < 0 || repeat.EntrySize < 0 || start > message.Length - repeat.EntrySize)
                return null;
            var entryBytes = message.Slice(start, repeat.EntrySize);
            var entry = new Dictionary<string, string>(repeat.Fields.Length, StringComparer.Ordinal);
            foreach (var field in repeat.Fields)
            {
                if (!TryReadField(entryBytes, field, out var value))
                    return null;
                entry[field.Name] = value;
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        text = "";
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0)
            end--;
        for (var index = 0; index < end; index++)
        {
            if (bytes[index] == 0)
                return false;
        }

        try
        {
            text = StrictUtf8.GetString(bytes[..end]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static string Read(ReadOnlySpan<byte> bytes, FieldSpec field)
    {
        return field.Type switch
        {
            "uint8" => bytes[0].ToString(CultureInfo.InvariantCulture),
            "uint16" => (field.LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt16BigEndian(bytes)).ToString(CultureInfo.InvariantCulture),
            "int16" => (field.LittleEndian
                ? BinaryPrimitives.ReadInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadInt16BigEndian(bytes)).ToString(CultureInfo.InvariantCulture),
            "uint32" => (field.LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32BigEndian(bytes)).ToString(CultureInfo.InvariantCulture),
            "int32" => (field.LittleEndian
                ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
                : BinaryPrimitives.ReadInt32BigEndian(bytes)).ToString(CultureInfo.InvariantCulture),
            "hex" => Convert.ToHexString(bytes),
            _ => ""
        };
    }
}

public sealed class FrameReassembler
{
    private readonly byte[] _buffer;
    private int _count;

    public FrameReassembler(int maxBufferBytes)
    {
        if (maxBufferBytes < 256)
            throw new ArgumentOutOfRangeException(nameof(maxBufferBytes));
        _buffer = new byte[maxBufferBytes];
    }

    public int ResyncBytes { get; private set; }

    public int Withheld { get; private set; }

    public int Overflows { get; private set; }

    public int Buffered => _count;

    public void Reset() => _count = 0;

    public List<ParsedFrame> Push(
        ReadOnlySpan<byte> data,
        RuleSet rules,
        Direction direction,
        ConversationState conversation,
        int previewCap)
    {
        var found = new List<ParsedFrame>();
        if (data.IsEmpty)
            return found;

        if (data.Length > _buffer.Length || _count > _buffer.Length - data.Length)
        {
            _count = 0;
            Overflows++;
            if (data.Length > _buffer.Length)
                return found;
        }

        data.CopyTo(_buffer.AsSpan(_count));
        _count += data.Length;

        var offset = 0;
        while (offset < _count)
        {
            var outcome = rules.Match(_buffer.AsSpan(offset, _count - offset), direction, conversation);
            if (outcome.Kind == MatchKind.NeedMore)
                break;

            if (outcome.Kind is MatchKind.NoMatch or MatchKind.Resync)
            {
                offset++;
                if (outcome.Kind == MatchKind.Resync)
                    Withheld++;
                else
                    ResyncBytes++;
                continue;
            }

            if (outcome.Kind == MatchKind.Withhold)
            {
                Withheld++;
                offset += outcome.TotalLength;
                continue;
            }

            if (outcome.Kind == MatchKind.Frame)
            {
                found.Add(FieldExtractor.Extract(_buffer.AsSpan(offset, outcome.TotalLength), outcome.Rule!, previewCap));
                conversation.Apply(outcome.Rule!);
            }

            offset += outcome.TotalLength;
        }

        if (offset > 0)
        {
            var remaining = _count - offset;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, offset, _buffer, 0, remaining);
            _count = remaining;
        }

        return found;
    }
}
