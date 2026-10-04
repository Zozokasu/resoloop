using System.Text;
using System.Text.Json;

namespace RLoop.Core;

public sealed record CatalogTypeFallback(string Type, string? Member, string Reason);
public sealed record CatalogTypesDeclaration(string Text, string ContentHash, string GeneratorVersion,
    IReadOnlyList<CatalogTypeFallback> Fallbacks);

/// <summary>Editor hints from acquired catalog evidence; ApplyCatalogValidator remains authoritative.</summary>
public static class CatalogTypesGenerator
{
    public const string GeneratorVersion = "1";

    public static CatalogTypesDeclaration Generate(ApplyCatalog catalog)
    {
        if (catalog.UnavailableReason() is { } reason)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", reason, ExitCodes.ValidationFailed);
        if (catalog.Synthetic)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Synthetic catalogs cannot generate authoring types.", ExitCodes.ValidationFailed);

        var fallbacks = new List<CatalogTypeFallback>();
        var text = new StringBuilder();
        text.Append("// resoloop-catalog-types: ").Append(JsonSerializer.Serialize(new
        { formatVersion = "1", contentHash = catalog.ContentHash, generatorVersion = GeneratorVersion })).Append('\n');
        text.Append("import \"resoloop-jsx\";\nimport type { JsonValue } from \"resoloop-jsx\";\nexport {};\n\n");
        text.Append("declare module \"resoloop-jsx\" {\n  interface CatalogComponentRegistry {\n");

        var entries = catalog.Content.Types.ToDictionary(t => t.FullName, StringComparer.Ordinal);
        foreach (var alias in catalog.Content.Aliases) entries[alias.Key] = entries[alias.Value];
        foreach (var (name, owner) in entries.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var closed = owner.Confirmed && owner.MembersComplete;
            if (!closed) fallbacks.Add(new(name, null, "Member names remain open: type or complete member evidence is unavailable."));
            text.Append("    ").Append(Quote(name)).Append(": {\n      members: {\n");
            foreach (var (memberName, member) in (owner.Members ?? new Dictionary<string, CatalogMember>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string? value = null;
                if (owner.Confirmed && (!owner.IsGeneric || owner.ClosureComplete) && member is { Confirmed: true })
                    value = member.Kind switch
                    {
                        "field" => Value(member.ValueType, []),
                        "reference" when !UnclosedGenericTarget(member.TargetType) => "string | null",
                        _ => null
                    };
                if (value is null) fallbacks.Add(new(name, memberName, "Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."));
                text.Append("        ").Append(Quote(memberName)).Append(": ").Append(value ?? "JsonValue").Append(";\n");
            }
            text.Append("      };\n      membersComplete: ").Append(closed ? "true" : "false").Append(";\n    };\n");
        }
        text.Append("  }\n}\n");
        // JSON one-line comments preserve exact names safely, including embedded newlines/comment delimiters.
        foreach (var fallback in fallbacks)
            text.Append("// resoloop-catalog-fallback: ").Append(JsonSerializer.Serialize(fallback, ApplyCatalog.Json)).Append('\n');
        return new(text.ToString(), catalog.ContentHash, GeneratorVersion, fallbacks);

        bool UnclosedGenericTarget(string? name) => name is not null && entries.TryGetValue(name, out var target) &&
            target.IsGeneric && (!target.Confirmed || !target.ClosureComplete);

        string? Value(string? name, HashSet<string> active)
        {
            var type = catalog.Find(name);
            if (type is null || type.IsGeneric && !type.ClosureComplete || !active.Add(type.FullName)) return null;
            try
            {
                switch (type.Representation)
                {
                    case "single": return "number";
                    case "nullable":
                        var element = Value(type.ElementType, active);
                        return element is null ? null : $"({element}) | null";
                    case "tuple" when type.TupleSize is >= 2 and <= 4:
                        var scalar = Value(type.ElementType, active);
                        if (scalar is null) return null;
                        var tuple = "[" + string.Join(", ", Enumerable.Repeat(scalar, type.TupleSize)) + "]";
                        string Object(string[] axes) => "{ " + string.Join(" ", axes.Take(type.TupleSize).Select(axis => axis + ": " + scalar + ";")) + " }";
                        return tuple + " | " + Object(["x", "y", "z", "w"]) + " | " + Object(["r", "g", "b", "a"]);
                    case "enum" when type.EnumValues is not null && type.IsFlags is not null:
                        return type.IsFlags.Value ? "string | number" : string.Join(" | ", type.EnumValues.Keys.Order(StringComparer.Ordinal).Select(Quote).Append("number"));
                    default: return null;
                }
            }
            finally { active.Remove(type.FullName); }
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
