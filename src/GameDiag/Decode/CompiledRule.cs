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

public sealed class CompiledRule
{
    public required string Id { get; init; }

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
}

public enum MatchKind
{
    NoMatch,
    NeedMore,
    Skip,
    Frame
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
}
