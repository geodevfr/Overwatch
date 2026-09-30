using System.Buffers.Binary;
using System.Globalization;

namespace GameDiag.Decode;

public sealed class ParsedFrame
{
    public required string RuleId { get; init; }

    public required int Length { get; init; }

    public required Dictionary<string, string> Fields { get; init; }

    public required string PayloadHex { get; init; }
}

public static class FieldExtractor
{
    public static ParsedFrame Extract(ReadOnlySpan<byte> message, CompiledRule rule, int previewCap)
    {
        var fields = new Dictionary<string, string>(rule.Fields.Length, StringComparer.Ordinal);
        foreach (var field in rule.Fields)
        {
            if (field.Offset < 0 || field.Size < 0 || field.Offset > message.Length - field.Size)
                continue;
            fields[field.Name] = Read(message.Slice(field.Offset, field.Size), field);
        }

        var previewLength = previewCap <= 0 ? 0 : Math.Min(message.Length, previewCap);
        return new ParsedFrame
        {
            RuleId = rule.Id,
            Length = message.Length,
            Fields = fields,
            PayloadHex = Convert.ToHexString(message[..previewLength])
        };
    }

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

            if (outcome.Kind == MatchKind.NoMatch)
            {
                offset++;
                ResyncBytes++;
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
