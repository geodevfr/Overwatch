using Overwatch.Config;
using Overwatch.Logging;

namespace Overwatch.Decode;

public static class RuleCompiler
{
    public static RuleSet Compile(RuleFile file)
    {
        var compiled = new List<CompiledRule>(file.Rules.Count);
        var disabled = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in file.Rules)
        {
            var id = definition.Id.Trim();
            if (!IsIdentifier(id))
                throw new ConfigException($"Identifiant de règle invalide : '{definition.Id}'.");
            if (!ids.Add(id))
                throw new ConfigException($"Identifiant de règle en double : {id}.");
            if (!definition.Enabled)
            {
                disabled.Add(id);
                continue;
            }

            compiled.Add(CompileRule(definition, id));
        }

        if (disabled.Count > 0)
            ConsoleLog.Info("Règles désactivées, en attente d'une signature confirmée : " + string.Join(", ", disabled));
        return new RuleSet(compiled, disabled);
    }

    private static CompiledRule CompileRule(RuleDefinition definition, string id)
    {
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
                "hex" or "utf8" when field.Size > 0 => field.Size,
                "hex" or "utf8" => throw new ConfigException($"Le champ {type} {id}.{name} a besoin d'une size."),
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

        var kind = ParseKind(definition.Kind, id);
        var repeat = CompileRepeat(definition.Repeat, id, defaultLittle);
        var lots = CompileLots(definition.Lots, id, fields);
        ValidateKind(kind, id, definition, fields, repeat, lots);

        return new CompiledRule
        {
            Id = id,
            Kind = kind,
            Repeat = repeat,
            Lots = lots,
            NameField = definition.NameField.Trim(),
            ItemField = string.IsNullOrWhiteSpace(definition.ItemField) ? "item_id" : definition.ItemField.Trim(),
            ValueField = string.IsNullOrWhiteSpace(definition.ValueField) ? "average" : definition.ValueField.Trim(),
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

    private static void ValidateKind(
        ObservationKind kind,
        string id,
        RuleDefinition definition,
        List<FieldSpec> fields,
        RepeatSpec? repeat,
        SaleLotSpec[] lots)
    {
        switch (kind)
        {
            case ObservationKind.AveragePrices:
                if (repeat is null)
                    throw new ConfigException($"La règle {id} (prix moyens) exige un bloc repeat. Sans lui, elle est désactivée.");
                RequireRepeatField(repeat, string.IsNullOrWhiteSpace(definition.ItemField) ? "item_id" : definition.ItemField.Trim(), id);
                RequireRepeatField(repeat, string.IsNullOrWhiteSpace(definition.ValueField) ? "average" : definition.ValueField.Trim(), id);
                break;
            case ObservationKind.SaleLots:
                if (lots.Length == 0)
                    throw new ConfigException($"La règle {id} (lots) n'a aucun palier. Elle ne doit pas deviner les quantités.");
                RequireField(fields, string.IsNullOrWhiteSpace(definition.ItemField) ? "item_id" : definition.ItemField.Trim(), id);
                break;
            case ObservationKind.ServerName:
            case ObservationKind.CharacterName:
                var nameField = definition.NameField.Trim();
                if (nameField.Length == 0)
                    throw new ConfigException($"La règle {id} exige name_field.");
                var named = RequireField(fields, nameField, id);
                if (named.Type != "utf8")
                    throw new ConfigException($"Le champ {id}.{nameField} doit être utf8. Un entier ne sera pas interprété comme un nom.");
                break;
            case ObservationKind.Position:
            case ObservationKind.Combat:
                if (fields.Count == 0)
                    throw new ConfigException($"La règle {id} n'extrait aucun champ. Rien n'est enregistré.");
                break;
        }
    }

    private static FieldSpec RequireField(List<FieldSpec> fields, string name, string ruleId)
    {
        foreach (var field in fields)
        {
            if (field.Name == name)
                return field;
        }

        throw new ConfigException($"Le champ {name} est absent de la règle {ruleId}.");
    }

    private static void RequireRepeatField(RepeatSpec repeat, string name, string ruleId)
    {
        foreach (var field in repeat.Fields)
        {
            if (field.Name == name)
                return;
        }

        throw new ConfigException($"Le champ répété {name} est absent de la règle {ruleId}.");
    }

    private static RepeatSpec? CompileRepeat(RepeatDefinition? definition, string ruleId, bool defaultLittle)
    {
        if (definition is null)
            return null;
        if (definition.CountSize is not (1 or 2 or 4))
            throw new ConfigException($"repeat.count_size de {ruleId} doit valoir 1, 2 ou 4.");
        if (definition.CountOffset < 0 || definition.EntryOffset < 0 || definition.EntrySize < 1)
            throw new ConfigException($"Géométrie repeat invalide pour {ruleId}.");
        if (definition.EntryOffset < definition.CountOffset + definition.CountSize)
            throw new ConfigException($"Les entrées de {ruleId} chevauchent le compteur.");

        var fields = new List<FieldSpec>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            var name = field.Name.Trim();
            if (!IsIdentifier(name))
                throw new ConfigException($"Champ répété invalide dans {ruleId} : '{field.Name}'.");
            if (!names.Add(name))
                throw new ConfigException($"Champ répété en double dans {ruleId} : {name}.");
            var type = field.Type.Trim().ToLowerInvariant();
            var size = type switch
            {
                "uint8" => 1,
                "uint16" or "int16" => 2,
                "uint32" or "int32" => 4,
                "hex" or "utf8" when field.Size > 0 => field.Size,
                _ => throw new ConfigException($"Type répété inconnu pour {ruleId}.{name} : {field.Type}.")
            };
            if (field.Offset < 0 || field.Offset + size > definition.EntrySize)
                throw new ConfigException($"Le champ répété {ruleId}.{name} sort de l'entrée.");
            var endian = string.IsNullOrWhiteSpace(field.Endian) ? defaultLittle : ParseEndian(field.Endian, $"{ruleId}.{name}");
            fields.Add(new FieldSpec
            {
                Name = name,
                Offset = field.Offset,
                Type = type,
                Size = size,
                LittleEndian = endian
            });
        }

        if (fields.Count == 0)
            throw new ConfigException($"Le bloc repeat de {ruleId} n'a aucun champ.");

        return new RepeatSpec
        {
            CountOffset = definition.CountOffset,
            CountSize = definition.CountSize,
            LittleEndian = ParseEndian(definition.Endian, ruleId),
            EntryOffset = definition.EntryOffset,
            EntrySize = definition.EntrySize,
            Fields = fields.ToArray()
        };
    }

    private static SaleLotSpec[] CompileLots(List<SaleLotDefinition> lots, string ruleId, List<FieldSpec> fields)
    {
        if (lots.Count == 0)
            return Array.Empty<SaleLotSpec>();
        var compiled = new SaleLotSpec[lots.Count];
        var quantities = new HashSet<int>();
        for (var index = 0; index < lots.Count; index++)
        {
            var lot = lots[index];
            if (lot.Quantity <= 0)
                throw new ConfigException($"Quantité de lot invalide dans {ruleId}.");
            if (!quantities.Add(lot.Quantity))
                throw new ConfigException($"Quantité de lot en double dans {ruleId} : {lot.Quantity}.");
            var totalField = lot.TotalField.Trim();
            if (!IsIdentifier(totalField))
                throw new ConfigException($"total_field invalide dans {ruleId}.");
            RequireField(fields, totalField, ruleId);
            compiled[index] = new SaleLotSpec { Quantity = lot.Quantity, TotalField = totalField };
        }

        return compiled;
    }

    private static ObservationKind ParseKind(string value, string ruleId)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "" or "trace" => ObservationKind.Trace,
            "average_prices" => ObservationKind.AveragePrices,
            "sale_lots" => ObservationKind.SaleLots,
            "server_name" => ObservationKind.ServerName,
            "character_name" => ObservationKind.CharacterName,
            "position" => ObservationKind.Position,
            "combat" => ObservationKind.Combat,
            _ => throw new ConfigException($"kind inconnu pour {ruleId} : {value}. La règle doit être désactivée plutôt que rangée au hasard.")
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
