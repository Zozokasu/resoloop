using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Core;

public sealed record CatalogTypeFallback(string Type, string? Member, string Reason);
public sealed record CatalogTypesDeclaration(string Text, string ContentHash, string GeneratorVersion,
    IReadOnlyList<CatalogTypeFallback> Fallbacks)
{
    public string? RuntimeText { get; init; }
}

/// <summary>Editor hints from acquired catalog evidence; ApplyCatalogValidator remains authoritative.</summary>
public static class CatalogTypesGenerator
{
    public const string GeneratorVersion = "3";

    public static CatalogTypesDeclaration Generate(ApplyCatalog catalog) => Generate(catalog, "catalog-fields.ts");

    public static CatalogTypesDeclaration Generate(ApplyCatalog catalog, string runtimeFileName)
    {
        if (string.IsNullOrWhiteSpace(runtimeFileName) || Path.IsPathRooted(runtimeFileName) ||
            runtimeFileName is "." or ".." || runtimeFileName.IndexOfAny([Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar, '/', '\\']) >= 0)
            throw new RLoopException("INVALID_ARGUMENT", "Runtime catalog filename must be a leaf filename.", ExitCodes.InvalidArguments);
        if (catalog.UnavailableReason() is { } reason)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", reason, ExitCodes.ValidationFailed);
        if (catalog.Synthetic)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Synthetic catalogs cannot generate authoring types.", ExitCodes.ValidationFailed);

        var fallbacks = new List<CatalogTypeFallback>();
        var entries = catalog.Content.Types.ToDictionary(t => t.FullName, StringComparer.Ordinal);
        foreach (var alias in catalog.Content.Aliases) entries[alias.Key] = entries[alias.Value];
        var orderedEntries = entries.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var runtimeTypes = new SortedDictionary<string, RuntimeType>(StringComparer.Ordinal);
        var metadataAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedMetadataAliases = new List<(string Json, string Alias)>();
        var registry = new StringBuilder();

        foreach (var (name, owner) in orderedEntries)
        {
            var closed = owner.Confirmed && owner.MembersComplete;
            if (!closed) fallbacks.Add(new(name, null, "Member names remain open: type or complete member evidence is unavailable."));
            var members = new SortedDictionary<string, CatalogMemberRuntime>(StringComparer.Ordinal);
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var values = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (memberName, member) in (owner.Members ?? new Dictionary<string, CatalogMember>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var evidence = CatalogFieldMetadata.Describe(catalog, owner, member);
                var descriptor = new CatalogMemberRuntime(evidence.Kind, evidence.CatalogKind, evidence.Confirmed,
                    evidence.MemberType, evidence.ValueType, evidence.TargetType, evidence.Scalar, evidence.FieldInterface,
                    evidence.FieldTarget is null ? null : new(evidence.FieldTarget.Representation,
                        evidence.FieldTarget.ValueType, evidence.FieldTarget.TargetType));
                members[memberName] = descriptor;
                var descriptorJson = JsonSerializer.Serialize(descriptor, ApplyCatalog.Json);
                if (!metadataAliases.TryGetValue(descriptorJson, out var metadataAlias))
                {
                    metadataAlias = "CM" + metadataAliases.Count;
                    metadataAliases.Add(descriptorJson, metadataAlias);
                    orderedMetadataAliases.Add((descriptorJson, metadataAlias));
                }
                metadata[memberName] = metadataAlias;

                var value = ValueFor(evidence, owner, member);
                values[memberName] = value;
                if (value is null) fallbacks.Add(new(name, memberName, "Value remains JsonValue: unsupported or unconfirmed member/value evidence or generic closure."));
            }
            runtimeTypes[name] = new(owner.FullName, owner.Confirmed, owner.MembersComplete, members);

            registry.Append("    ").Append(Quote(name)).Append(": {\n      members: {\n");
            foreach (var (memberName, value) in values)
                registry.Append("        ").Append(Quote(memberName)).Append(": ").Append(value ?? "JsonValue").Append(";\n");
            registry.Append("      };\n      memberMetadata: {");
            var firstMetadata = true;
            foreach (var (memberName, alias) in metadata)
            {
                if (!firstMetadata) registry.Append(' ');
                registry.Append(Quote(memberName)).Append(": ").Append(alias).Append(';');
                firstMetadata = false;
            }
            registry.Append("};\n      membersComplete: ").Append(closed ? "true" : "false").Append(";\n    };\n");
        }
        var declarations = new StringBuilder();
        declarations.Append("import \"resoloop-jsx\";\nimport type { JsonValue, FieldRef, UncheckedReference } from \"resoloop-jsx\";\nexport {};\n\n");
        if (metadataAliases.Count > 0)
        {
            foreach (var (json, alias) in orderedMetadataAliases)
                declarations.Append("type ").Append(alias).Append(" = ").Append(json).Append(";\n");
            declarations.Append('\n');
        }
        declarations.Append("declare module \"resoloop-jsx\" {\n  interface CatalogComponentRegistry {\n")
            .Append(registry).Append("  }\n}\n");
        foreach (var fallback in fallbacks)
            declarations.Append("// resoloop-catalog-fallback: ").Append(JsonSerializer.Serialize(fallback, ApplyCatalog.Json)).Append('\n');

        var runtime = new RuntimeTable("1", GeneratorVersion, catalog.ContentHash, runtimeTypes);
        using var runtimeDocument = JsonDocument.Parse(JsonSerializer.Serialize(runtime, ApplyCatalog.Json));
        var runtimeJson = RuntimeLiteral(runtimeDocument.RootElement);
        var runtimeText = "// resoloop-catalog-fields: " + JsonSerializer.Serialize(new
        { formatVersion = "1", contentHash = catalog.ContentHash, generatorVersion = GeneratorVersion }) +
            "\nimport type { CatalogRuntimeTable } from \"resoloop-jsx\";\nexport const catalog = " + runtimeJson +
            " as const satisfies CatalogRuntimeTable;\n";
        var runtimeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeText)));
        var header = "// resoloop-catalog-types: " + JsonSerializer.Serialize(new
        { formatVersion = "1", contentHash = catalog.ContentHash, generatorVersion = GeneratorVersion,
            runtimeFile = runtimeFileName, runtimeSha256 = runtimeHash }) + "\n";
        return new(header + declarations, catalog.ContentHash, GeneratorVersion, fallbacks) { RuntimeText = runtimeText };

        string RuntimeLiteral(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var objectText = new StringBuilder("{");
                    var firstProperty = true;
                    foreach (var property in value.EnumerateObject())
                    {
                        if (!firstProperty) objectText.Append(',');
                        objectText.Append(property.NameEquals("__proto__") ? "[\"__proto__\"]" : Quote(property.Name));
                        objectText.Append(':').Append(RuntimeLiteral(property.Value));
                        firstProperty = false;
                    }
                    return objectText.Append('}').ToString();
                case JsonValueKind.Array:
                    var arrayText = new StringBuilder("[");
                    var firstItem = true;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!firstItem) arrayText.Append(',');
                        arrayText.Append(RuntimeLiteral(item));
                        firstItem = false;
                    }
                    return arrayText.Append(']').ToString();
                case JsonValueKind.String:
                    return JsonSerializer.Serialize(value.GetString(), ApplyCatalog.Json);
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                case JsonValueKind.Null: return "null";
                default: throw new InvalidOperationException($"Unsupported JSON token {value.ValueKind}.");
            }
        }

        string? ValueFor(CatalogMemberEvidence evidence, CatalogType owner, CatalogMember member)
        {
            if (!evidence.Confirmed) return null;
            if (evidence.Kind == "field") return evidence.Scalar is { } scalar ? Scalar(scalar) : Value(member.ValueType, []);
            if (evidence.Kind == "reference" && (!owner.IsGeneric || owner.ClosureComplete) && !UnclosedGenericTarget(member.TargetType))
            {
                if (evidence.FieldTarget is { } target)
                    return $"FieldRef<{Scalar(target.Representation)}, {Quote(target.Representation)}> | UncheckedReference | null";
                return "string | null";
            }
            return null;
        }

        bool UnclosedGenericTarget(string? targetName) => targetName is not null &&
            entries.TryGetValue(targetName, out var target) && target.IsGeneric && (!target.Confirmed || !target.ClosureComplete);

        string? Value(string? name, HashSet<string> active)
        {
            var type = catalog.Find(name);
            if (type is null || type.IsGeneric && !type.ClosureComplete || !active.Add(type.FullName)) return null;
            try
            {
                switch (type.Representation)
                {
                    case "single": return "number";
                    case "boolean": return "boolean";
                    case "int32": return "number";
                    case "string": return "string";
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

    private static string Scalar(string representation) => representation switch
    {
        "boolean" => "boolean", "int32" or "single" => "number", "string" => "string",
        _ => throw new InvalidOperationException($"Unsupported scalar representation '{representation}'.")
    };

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private sealed record RuntimeTable(string FormatVersion, string GeneratorVersion, string ContentHash,
        SortedDictionary<string, RuntimeType> Types);
    private sealed record RuntimeType(string CanonicalType, bool Confirmed, bool MembersComplete,
        SortedDictionary<string, CatalogMemberRuntime> Members);
    private sealed record CatalogMemberRuntime(string Kind, string CatalogKind, bool Confirmed,
        string? MemberType, string? ValueType, string? TargetType, string? Scalar, string? FieldInterface,
        FieldTarget? FieldTarget);
    private sealed record FieldTarget(string Representation, string ValueType, string TargetType);
}
