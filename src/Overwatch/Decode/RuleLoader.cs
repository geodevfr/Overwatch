using Overwatch.Config;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Overwatch.Decode;

public static class RuleLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static RuleFile LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"Fichier de règles introuvable : {path}");
        return Parse(File.ReadAllText(path));
    }

    public static RuleFile Parse(string yaml)
    {
        var file = Deserializer.Deserialize<RuleFile>(yaml) ?? new RuleFile();
        file.Rules ??= new List<RuleDefinition>();
        foreach (var rule in file.Rules)
        {
            rule.Extract ??= new List<FieldDefinition>();
            rule.Lots ??= new List<SaleLotDefinition>();
            if (rule.Repeat is not null)
                rule.Repeat.Fields ??= new List<FieldDefinition>();
            if (rule.Context is not null)
            {
                rule.Context.Requires ??= new List<string>();
                rule.Context.Forbids ??= new List<string>();
                rule.Context.Sets ??= new List<string>();
            }
        }

        return file;
    }
}
