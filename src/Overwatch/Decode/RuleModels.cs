namespace Overwatch.Decode;

public sealed class RuleFile
{
    public int Version { get; set; } = 1;

    public List<RuleDefinition> Rules { get; set; } = new();
}

public sealed class RuleDefinition
{
    public bool Enabled { get; set; } = true;

    public string Id { get; set; } = "";

    public string Description { get; set; } = "";

    public string Kind { get; set; } = "trace";

    public string Direction { get; set; } = "any";

    public int MinLength { get; set; }

    public int MaxLength { get; set; }

    public string HeaderHex { get; set; } = "";

    public LengthFieldDefinition? LengthField { get; set; }

    public RuleContextDefinition? Context { get; set; }

    public List<FieldDefinition> Extract { get; set; } = new();

    public RepeatDefinition? Repeat { get; set; }

    public List<SaleLotDefinition> Lots { get; set; } = new();

    public string NameField { get; set; } = "";

    public string ItemField { get; set; } = "";

    public string ValueField { get; set; } = "";
}

public sealed class RepeatDefinition
{
    public int CountOffset { get; set; }

    public int CountSize { get; set; } = 2;

    public string Endian { get; set; } = "little";

    public int EntryOffset { get; set; }

    public int EntrySize { get; set; }

    public List<FieldDefinition> Fields { get; set; } = new();
}

public sealed class SaleLotDefinition
{
    public int Quantity { get; set; }

    public string TotalField { get; set; } = "";
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
