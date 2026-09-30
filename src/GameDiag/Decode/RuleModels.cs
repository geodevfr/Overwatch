namespace GameDiag.Decode;

public sealed class RuleFile
{
    public int Version { get; set; } = 1;

    public List<RuleDefinition> Rules { get; set; } = new();
}

public sealed class RuleDefinition
{
    public string Id { get; set; } = "";

    public string Description { get; set; } = "";

    public string Direction { get; set; } = "any";

    public int MinLength { get; set; }

    public int MaxLength { get; set; }

    public string HeaderHex { get; set; } = "";

    public LengthFieldDefinition? LengthField { get; set; }

    public RuleContextDefinition? Context { get; set; }

    public List<FieldDefinition> Extract { get; set; } = new();
}

public sealed class LengthFieldDefinition
{
    public int Offset { get; set; }

    public int Size { get; set; } = 2;

    public string Endian { get; set; } = "little";

    public int Bias { get; set; }
}

public sealed class RuleContextDefinition
{
    public List<string> Requires { get; set; } = new();

    public List<string> Forbids { get; set; } = new();

    public List<string> Sets { get; set; } = new();
}

public sealed class FieldDefinition
{
    public string Name { get; set; } = "";

    public int Offset { get; set; }

    public string Type { get; set; } = "uint32";

    public int Size { get; set; }

    public string Endian { get; set; } = "";
}
