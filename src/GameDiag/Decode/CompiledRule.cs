using System.Buffers.Binary;

namespace GameDiag.Decode;

public sealed class LengthField
{
    public required int Offset { get; init; }

    public required int Size { get; init; }

    public required bool LittleEndian { get; init; }

    public required int Bias { get; init; }

    public long ReadRaw(ReadOnlySpan<byte> window)
    {
        var slice = window.Slice(Offset, Size);
        return Size switch
        {
            1 => slice[0],
            2 => LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
                : BinaryPrimitives.ReadUInt16BigEndian(slice),
            4 => LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32BigEndian(slice),
            _ => throw new InvalidOperationException("Taille de champ de longueur inattendue.")
        };
    }
}

public sealed class FieldSpec
{
    public required string Name { get; init; }

    public required int Offset { get; init; }

    public required string Type { get; init; }

    public required int Size { get; init; }

    public required bool LittleEndian { get; init; }
}

public enum ObservationKind
{
    Trace,
    AveragePrices,
    SaleLots,
    ServerName,
    CharacterName,
    Position,
    Combat
}

public sealed class RepeatSpec
{
    public required int CountOffset { get; init; }

    public required int CountSize { get; init; }

    public required bool LittleEndian { get; init; }

    public required int EntryOffset { get; init; }

    public required int EntrySize { get; init; }

    public required FieldSpec[] Fields { get; init; }

    public bool TryReadCount(ReadOnlySpan<byte> message, out int count)
    {
        count = 0;
        if (CountOffset < 0 || message.Length < CountOffset + CountSize)
            return false;
        long raw = CountSize switch
        {
            1 => message[CountOffset],
            2 => LittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(CountOffset, 2))
                : BinaryPrimitives.ReadUInt16BigEndian(message.Slice(CountOffset, 2)),
            4 => LittleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(CountOffset, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(message.Slice(CountOffset, 4)),
            _ => -1
        };
        if (raw < 0 || raw > int.MaxValue)
            return false;
        count = (int)raw;
        return true;
    }
}

public sealed class SaleLotSpec
{
    public required int Quantity { get; init; }

    public required string TotalField { get; init; }
}

public sealed class CompiledRule
{
    public required string Id { get; init; }

    public ObservationKind Kind { get; init; } = ObservationKind.Trace;

    public RepeatSpec? Repeat { get; init; }

    public SaleLotSpec[] Lots { get; init; } = Array.Empty<SaleLotSpec>();

    public string NameField { get; init; } = "";

    public string ItemField { get; init; } = "item_id";

    public string ValueField { get; init; } = "average";

    public required string Description { get; init; }

    public required DirectionFilter Direction { get; init; }

    public required int MinLength { get; init; }

    public required int MaxLength { get; init; }

    public required byte[] Header { get; init; }

    public required LengthField? Length { get; init; }

    public required string[] Requires { get; init; }

    public required string[] Forbids { get; init; }

    public required string[] Sets { get; init; }

    public required FieldSpec[] Fields { get; init; }

    public bool Accepts(Direction direction)
    {
        return Direction switch
        {
            DirectionFilter.Any => true,
            DirectionFilter.ClientToServer => direction == Decode.Direction.ClientToServer,
            DirectionFilter.ServerToClient => direction == Decode.Direction.ServerToClient,
            _ => false
        };
    }

    public bool TryGetTotalLength(ReadOnlySpan<byte> window, out int total, out bool incomplete)
    {
        incomplete = false;
        total = 0;
        if (Length is null)
        {
            total = MinLength;
            return true;
        }

        var needed = Length.Offset + Length.Size;
        if (window.Length < needed)
        {
            incomplete = true;
            return false;
        }

        var sized = Length.ReadRaw(window) + Length.Bias;
        if (sized < MinLength || sized > MaxLength || sized < Header.Length || sized > int.MaxValue)
            return false;

        total = (int)sized;
        return true;
    }

    public bool StructureFits(ReadOnlySpan<byte> message)
    {
        foreach (var field in Fields)
        {
            if (field.Type == "utf8" && !FieldExtractor.TryReadUtf8(message, field, out _))
                return false;
        }

        if (Repeat is null)
            return true;
        if (!Repeat.TryReadCount(message, out var count) || count < 0)
            return false;

        try
        {
            var end = checked(Repeat.EntryOffset + count * Repeat.EntrySize);
            return end == message.Length;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

public enum MatchKind
{
    NoMatch,
    NeedMore,
    Skip,
    Frame,
    Withhold,
    Resync
}

public readonly struct MatchOutcome
{
    public MatchKind Kind { get; init; }

    public CompiledRule? Rule { get; init; }

    public int TotalLength { get; init; }

    public static MatchOutcome NoMatch() => new() { Kind = MatchKind.NoMatch };

    public static MatchOutcome NeedMore() => new() { Kind = MatchKind.NeedMore };

    public static MatchOutcome Skip(int totalLength) => new() { Kind = MatchKind.Skip, TotalLength = totalLength };

    public static MatchOutcome Frame(CompiledRule rule, int totalLength) =>
        new() { Kind = MatchKind.Frame, Rule = rule, TotalLength = totalLength };

    public static MatchOutcome Withhold(int totalLength) => new() { Kind = MatchKind.Withhold, TotalLength = totalLength };

    public static MatchOutcome Resync() => new() { Kind = MatchKind.Resync };
}
