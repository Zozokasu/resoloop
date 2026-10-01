using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RLoop.Core;
using Link = ResoniteLink;

namespace RLoop.ResoniteLink;

public sealed record CatalogSnapshotContent(IReadOnlyList<Link.ComponentDefinition> Components,
    IReadOnlyList<Link.TypeDefinition> Types, IReadOnlyList<Link.SyncObjectDefinition> SyncObjects,
    IReadOnlyDictionary<string, string> Aliases, IReadOnlyDictionary<string, CatalogEnum>? Enums = null);
public sealed record CatalogEnum(IReadOnlyDictionary<string, long> Values, bool IsFlags);
public sealed record CatalogSnapshot(CatalogIdentity? Identity, CatalogIdentity? EvidenceIdentity,
    string Source, bool Synthetic, string ContentHash, CatalogSnapshotContent Content);

/// <summary>SDK definitions are retained in the acquisition snapshot, and mapped only at this boundary.</summary>
public static class CatalogMapper
{
    public const string ClientPackageVersion = "0.13.1";
    public static string SnapshotHash(CatalogSnapshotContent content) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, ReflectionMetadataCache.Json))));

    public static ApplyCatalog Export(CatalogSnapshot snapshot)
    {
        // A legacy cache has no acquisition identity. Hashes alone cannot promote it.
        if (snapshot.Identity is null || snapshot.EvidenceIdentity != snapshot.Identity ||
            snapshot.Identity.ClientPackageVersion != ClientPackageVersion ||
            snapshot.Content is null || snapshot.ContentHash != SnapshotHash(snapshot.Content))
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", "Snapshot acquisition identity or content hash is missing/mismatched.", ExitCodes.ValidationFailed);
        var types = new Dictionary<string, CatalogType>(StringComparer.Ordinal);
        foreach (var type in snapshot.Content.Types) types[type.FullTypeName] = MapType(type);
        foreach (var component in snapshot.Content.Components)
            types[component.Type.FullTypeName] = MapType(component.Type) with
            { Members = component.Members.ToDictionary(p => p.Key, p => MapMember(p.Value, types), StringComparer.Ordinal), MembersComplete = true };
        foreach (var sync in snapshot.Content.SyncObjects)
            types[sync.Type.FullTypeName] = MapType(sync.Type) with
            { Members = sync.Members.ToDictionary(p => p.Key, p => MapMember(p.Value, types), StringComparer.Ordinal), MembersComplete = true };
        foreach (var item in snapshot.Content.Enums ?? new Dictionary<string, CatalogEnum>())
            if (types.TryGetValue(item.Key, out var type)) types[item.Key] = type with { EnumValues = item.Value.Values, IsFlags = item.Value.IsFlags };
        foreach (var alias in snapshot.Content.Aliases)
            if (alias.Key != alias.Value && types.TryGetValue(alias.Key, out var wireType) && types.TryGetValue(alias.Value, out var fullType))
            {
                // Only acquisition-confirmed aliases may transfer wire representation to a full identity.
                if (wireType.Representation != "other") types[alias.Value] = fullType with
                { Representation = wireType.Representation, ElementType = wireType.ElementType, TupleSize = wireType.TupleSize };
                types.Remove(alias.Key);
            }
        var content = new CatalogContent(types.Values.OrderBy(t => t.FullName, StringComparer.Ordinal).ToArray(), snapshot.Content.Aliases);
        var catalog = new ApplyCatalog("1", snapshot.Identity, snapshot.EvidenceIdentity, snapshot.Source, snapshot.Synthetic, ApplyCatalog.Hash(content), content);
        if (catalog.UnavailableReason() is { } reason)
            throw new RLoopException("APPLY_CATALOG_UNAVAILABLE", reason, ExitCodes.ValidationFailed);
        return catalog;
    }

    private static CatalogType MapType(Link.TypeDefinition type) => new(type.FullTypeName, true,
        type.Interfaces is not null, ModelMapper.Render(type.BaseType), (type.Interfaces ?? []).Select(ModelMapper.Render).Cast<string>().ToArray(),
        type.IsGenericType, Representation: type.IsEnum ? "enum" :
            type.FullTypeName is "System.Single" or "[mscorlib]System.Single" or "[System.Private.CoreLib]System.Single" ? "single" :
            type.IsValueType || type.IsEnginePrimitive ? "unknown" : "other");

    private static CatalogMember MapMember(Link.MemberDefinition definition, Dictionary<string, CatalogType> types)
    {
        var memberType = ModelMapper.Render(definition.Type);
        return definition switch
        {
            Link.FieldDefinition field => new("field", memberType, ValueType(field.ValueType, types)),
            Link.ReferenceDefinition reference => new("reference", memberType, TargetType: ModelMapper.Render(reference.TargetType)),
            Link.ListDefinition list => new("list", memberType, Element: list.ElementDefinition is null ? null : MapMember(list.ElementDefinition, types)),
            // SDK ArrayDefinition contains a value type, rather than a member definition.
            Link.ArrayDefinition array => new("array", memberType, Element: new("field", ValueType: ValueType(array.ValueType, types))),
            Link.DictionaryDefinition dictionary => new("dictionary", memberType,
                Element: dictionary.ElementDefinition is null ? null : MapMember(dictionary.ElementDefinition, types), KeyType: ValueType(dictionary.KeyType, types)),
            Link.SyncObjectMemberDefinition => new("syncObject", memberType),
            _ => new("unknown", memberType, Confirmed: false)
        };
    }

    private static string? ValueType(Link.TypeReference? reference, Dictionary<string, CatalogType> types)
    {
        if (reference is null || reference.IsGenericParameter) return null;
        var name = ModelMapper.Render(reference)!;
        // Exact SDK wire aliases only; never strip an arbitrary namespace/assembly or guess arguments.
        var kind = reference.Type switch
        {
            "float" or "System.Single" or "[mscorlib]System.Single" or "[System.Private.CoreLib]System.Single" => "single",
            "float2" or "float3" or "float4" or "floatQ" or "floatq" or "color" or "colorX" or "colorx" => "tuple",
            "Nullable<>" or "System.Nullable<>" when reference.GenericArguments?.Count == 1 => "nullable",
            "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "double" or "string" or "Uri" or "Type" => "other",
            _ => null
        };
        if (kind is null) return name; // Requires a separately acquired TypeDefinition.
        var element = kind == "nullable" ? ValueType(reference.GenericArguments![0], types) : kind == "tuple" ? ValueType(new Link.TypeReference { Type = "float" }, types) : null;
        var size = reference.Type switch { "float2" => 2, "float3" => 3, _ => 4 };
        var mapped = types.GetValueOrDefault(name) ?? new CatalogType(name, true, true, null, []);
        types[name] = mapped with { Representation = kind, ElementType = element, TupleSize = kind == "tuple" ? size : 0 };
        return name;
    }

    public static CatalogSnapshot LoadSnapshot(string file) => JsonSerializer.Deserialize<CatalogSnapshot>(File.ReadAllText(file), ReflectionMetadataCache.Json)
        ?? throw new JsonException("Snapshot is empty; legacy cache entries are not catalogs.");
    public static void SaveSnapshot(CatalogSnapshot snapshot, string file) => File.WriteAllText(file,
        JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(ReflectionMetadataCache.Json) { WriteIndented = true }), new UTF8Encoding(false));
}
