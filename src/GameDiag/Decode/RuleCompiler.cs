using GameDiag.Config;

namespace GameDiag.Decode;

public static class RuleCompiler
{
    public static RuleSet Compile(RuleFile file)
    {
        var compiled = new List<CompiledRule>(file.Rules.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in file.Rules)
            compiled.Add(CompileRule(definition, ids));
        return new RuleSet(compiled);
    }

    private static CompiledRule CompileRule(RuleDefinition definition, HashSet<string> ids)
    {
        var id = definition.Id.Trim();
        if (!IsIdentifier(id))
            throw new ConfigException($"Identifiant de règle invalide : '{definition.Id}'.");
        if (!ids.Add(id))
            throw new ConfigException($"Identifiant de règle en double : {id}.");

        if (definition.MinLength < 1)
            throw new ConfigException($"min_length de {id} doit être au moins 1.");
        if (definition.MaxLength < definition.MinLength)
            throw new ConfigException($"max_length de {id} est inférieur à min_length.");

        var header = ParseHex(definition.HeaderHex, id);
        if (header.Length == 0)
            throw new ConfigException($"header_hex de {id} est vide.");
        if (header.Length > definition.MinLength)
            throw new ConfigException($"Le préfixe de {id} est plus long que min_length.");

        LengthField? length = null;
        if (definition.LengthField is null)
        {
            if (definition.MinLength != definition.MaxLength)
            {
                throw new ConfigException(
                    $"La règle {id} n'a pas de length_field : min_length et max_length doivent être égaux (trame de taille fixe).");
            }
        }
        else
        {
            var field = definition.LengthField;
            if (field.Size is not (1 or 2 or 4))
                throw new ConfigException($"length_field.size de {id} doit valoir 1, 2 ou 4.");
            if (field.Offset < 0)
                throw new ConfigException($"length_field.offset de {id} est négatif.");
            if (field.Offset + field.Size > definition.MinLength)
                throw new ConfigException($"Le champ de longueur de {id} dépasse min_length.");
            length = new LengthField
            {
                Offset = field.Offset,
                Size = field.Size,
                LittleEndian = ParseEndian(field.Endian, id),
                Bias = field.Bias
            };
        }

        var defaultLittle = length?.LittleEndian ?? true;
        var context = definition.Context;
        var fields = new List<FieldSpec>(definition.Extract.Count);
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in definition.Extract)
        {
            var name = field.Name.Trim();
            if (!IsIdentifier(name))
                throw new ConfigException($"Champ invalide dans {id} : '{field.Name}'.");
            if (!fieldNames.Add(name))
                throw new ConfigException($"Champ en double dans {id} : {name}.");
            if (field.Offset < 0)
                throw new ConfigException($"Offset négatif pour {id}.{name}.");

            var type = field.Type.Trim().ToLowerInvariant();
            var size = type switch
            {
                "uint8" => 1,
                "uint16" or "int16" => 2,
                "uint32" or "int32" => 4,
                "hex" when field.Size > 0 => field.Size,
                "hex" => throw new ConfigException($"Le champ hex {id}.{name} a besoin d'une size."),
                _ => throw new ConfigException($"Type inconnu pour {id}.{name} : {field.Type}.")
            };

            var endian = string.IsNullOrWhiteSpace(field.Endian)
                ? defaultLittle
                : ParseEndian(field.Endian, $"{id}.{name}");
            fields.Add(new FieldSpec
            {
                Name = name,
                Offset = field.Offset,
                Type = type,
                Size = size,
                LittleEndian = endian
            });
        }

        return new CompiledRule
        {
            Id = id,
            Description = definition.Description?.Trim() ?? "",
            Direction = ParseDirection(definition.Direction, id),
            MinLength = definition.MinLength,
            MaxLength = definition.MaxLength,
            Header = header,
            Length = length,
            Requires = NormalizeFlags(context?.Requires, id),
            Forbids = NormalizeFlags(context?.Forbids, id),
            Sets = NormalizeFlags(context?.Sets, id),
            Fields = fields.ToArray()
        };
    }

    private static string[] NormalizeFlags(List<string>? flags, string ruleId)
    {
        if (flags is null || flags.Count == 0)
            return Array.Empty<string>();
        var values = new string[flags.Count];
        for (var i = 0; i < flags.Count; i++)
        {
            var flag = flags[i].Trim();
            if (!IsIdentifier(flag))
                throw new ConfigException($"Drapeau de contexte invalide dans {ruleId} : '{flags[i]}'.");
            values[i] = flag;
        }

        return values;
    }

    private static DirectionFilter ParseDirection(string value, string ruleId)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "any" or "" => DirectionFilter.Any,
            "c2s" or "client_to_server" => DirectionFilter.ClientToServer,
            "s2c" or "server_to_client" => DirectionFilter.ServerToClient,
            _ => throw new ConfigException($"direction inconnue pour {ruleId} : {value}. Utilisez c2s, s2c ou any.")
        };
    }

    private static bool ParseEndian(string value, string owner)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "little" or "le" => true,
            "big" or "be" => false,
            _ => throw new ConfigException($"endian inconnu pour {owner} : {value}. Utilisez little ou big.")
        };
    }

    public static byte[] ParseHex(string hex, string owner)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Array.Empty<byte>();

        var span = hex.AsSpan().Trim();
        var compactLength = 0;
        foreach (var character in span)
        {
            if (character is ' ' or '\t' or ':' or '-')
                continue;
            compactLength++;
        }

        if (compactLength == 0 || (compactLength & 1) == 1)
            throw new ConfigException($"header_hex impair ou vide pour {owner}.");

        var bytes = new byte[compactLength / 2];
        var nibble = -1;
        var index = 0;
        foreach (var character in span)
        {
            if (character is ' ' or '\t' or ':' or '-')
                continue;
            var value = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => throw new ConfigException($"Caractère hexadécimal invalide dans {owner}.")
            };
            if (nibble < 0)
            {
                nibble = value;
            }
            else
            {
                bytes[index++] = (byte)((nibble << 4) | value);
                nibble = -1;
            }
        }

        return bytes;
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
            return false;
        if (value[0] is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'))
            return false;
        foreach (var character in value)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return false;
        }

        return true;
    }
}
