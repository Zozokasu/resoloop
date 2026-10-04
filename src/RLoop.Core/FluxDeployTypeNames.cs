namespace RLoop.Core;

// Shared plain type-name normalization; names only, never runtime assignability.
public static class FluxDeployTypeNames
{
    public static string Normalize(string type)
    {
        var text = type.Trim();
        if (text.Length == 0) return text;
        if (text.EndsWith('?')) return $"Nullable<{Normalize(text[..^1])}>";
        if (text[0] == '[')
        {
            var close = Matching(text, 0);
            if (close < 0) return text;
            // "[Type, Assembly, Version=…]" (a CLR generic argument) or "[Assembly]Type" (a ResoniteLink prefix).
            if (close == text.Length - 1) return Normalize(SplitTopLevel(text[1..^1])[0]);
            text = text[(close + 1)..].Trim();
        }

        string name, suffix = string.Empty;
        var arguments = new List<string>();
        var tick = text.IndexOf('`');
        var angle = text.IndexOf('<');
        if (tick >= 0 && (angle < 0 || tick < angle))
        {
            name = text[..tick];
            var open = text.IndexOf('[', tick);
            if (open >= 0 && Matching(text, open) is var close and > 0)
            {
                arguments.AddRange(SplitTopLevel(text[(open + 1)..close]).Select(Normalize));
                suffix = text[(close + 1)..];
            }
        }
        else if (angle >= 0 && Matching(text, angle) is var close and > 0)
        {
            name = text[..angle];
            arguments.AddRange(SplitTopLevel(text[(angle + 1)..close]).Select(Normalize));
            suffix = text[(close + 1)..];
        }
        else name = text;

        var simple = Alias(name.Trim()[(name.Trim().LastIndexOf('.') + 1)..]);
        return arguments.Count == 0 ? simple + suffix : $"{simple}<{string.Join(",", arguments)}>{suffix}";
    }

    /// <summary>Equality of two normalized names (case-insensitive, as type names in manifests are written by hand).</summary>
    public static bool SameType(string normalizedExpected, string normalizedActual) =>
        string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase);

    /// <summary>The .NET naming convention for interfaces (<c>IButton</c>), on a normalized name.</summary>
    public static bool IsInterfaceName(string normalized) =>
        normalized.Length > 1 && normalized[0] == 'I' && char.IsUpper(normalized[1]);

    private static string Alias(string name) => name.ToLowerInvariant() switch
    {
        "boolean" or "bool" => "Boolean",
        "byte" or "uint8" => "Byte",
        "sbyte" or "int8" => "SByte",
        "short" or "int16" => "Int16",
        "ushort" or "uint16" => "UInt16",
        "int" or "int32" => "Int32",
        "uint" or "uint32" => "UInt32",
        "long" or "int64" => "Int64",
        "ulong" or "uint64" => "UInt64",
        "float" or "single" or "float32" => "Single",
        "double" or "float64" => "Double",
        "char" => "Char",
        "string" => "String",
        _ => name
    };

    /// <summary>Index of the bracket that closes the one at <paramref name="open"/>, counting <c>[]</c> and <c>&lt;&gt;</c>; -1 when unbalanced.</summary>
    private static int Matching(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length; index++)
        {
            if (text[index] is '[' or '<') depth++;
            else if (text[index] is ']' or '>' && --depth == 0) return index;
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '[' or '<') depth++;
            else if (text[index] is ']' or '>') depth--;
            else if (text[index] == ',' && depth == 0) { parts.Add(text[start..index].Trim()); start = index + 1; }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }
}
