using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RLoop.Core;

// These records describe evidence, never CLR types or SDK wire models.
public sealed record CatalogIdentity(string ResoniteVersion, string ResoniteLinkVersion,
    string ClientPackageVersion, string MapperVersion, DateTimeOffset RetrievedAt);
public sealed record CatalogMember(string Kind, string? MemberType = null, string? ValueType = null,
    string? TargetType = null, CatalogMember? Element = null, string? KeyType = null, bool Confirmed = true);
public sealed record CatalogType(string FullName, bool Confirmed, bool ClosureComplete,
    string? BaseType, IReadOnlyList<string> Interfaces, bool IsGeneric = false,
    IReadOnlyDictionary<string, CatalogMember>? Members = null, bool MembersComplete = false,
    string Representation = "other", string? ElementType = null, int TupleSize = 0,
    IReadOnlyDictionary<string, long>? EnumValues = null, bool? IsFlags = null);
public sealed record CatalogContent(IReadOnlyList<CatalogType> Types,
    IReadOnlyDictionary<string, string> Aliases);
public sealed record ApplyCatalog(string FormatVersion, CatalogIdentity? Identity,
    CatalogIdentity? EvidenceIdentity, string Source, bool Synthetic, string ContentHash, CatalogContent Content)
{
    public const string CurrentMapperVersion = "1";
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };

    // Hash canonical JSON (sorted object keys; array order retained), not provenance.
    public static string Hash(CatalogContent content)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(content, Json));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, json.RootElement);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var item in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Name); WriteCanonical(writer, item.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }

    public string? UnavailableReason(CatalogIdentity? expectedIdentity = null)
    {
        if (FormatVersion != "1" || Identity is null || EvidenceIdentity is null)
            return "Catalog format or acquisition identity is missing/unsupported.";
        if (new[] { Identity.ResoniteVersion, Identity.ResoniteLinkVersion, Identity.ClientPackageVersion,
            Identity.MapperVersion }.Any(string.IsNullOrWhiteSpace) || Identity.RetrievedAt == default)
            return "Catalog identity is incomplete.";
        if (Identity != EvidenceIdentity || expectedIdentity is not null && Identity != expectedIdentity ||
            Identity.MapperVersion != CurrentMapperVersion)
            return "Catalog identity does not match acquisition/expected identity or mapper version.";
        if (Source is not ("live" or "version-cache")) return "Catalog source is unverified.";
        if (Content?.Types is null || Content.Aliases is null || Content.Types.Any(t => t is null ||
            string.IsNullOrWhiteSpace(t.FullName) || t.Interfaces is null) ||
            Content.Types.Select(t => t.FullName).Distinct(StringComparer.Ordinal).Count() != Content.Types.Count)
            return "Catalog type definitions are malformed or ambiguous.";
        if (ContentHash != Hash(Content)) return "Catalog content hash does not match.";
        if (Content.Aliases.Any(a => string.IsNullOrWhiteSpace(a.Key) || !Content.Types.Any(t => t.FullName == a.Value) ||
            Content.Types.Any(t => t.FullName == a.Key && t.FullName != a.Value)))
            return "Catalog aliases are unresolved or ambiguous.";
        return null;
    }

    public CatalogType? Find(string? name)
    {
        if (name is null) return null;
        var full = Content.Aliases.GetValueOrDefault(name) ?? name;
        return Content.Types.SingleOrDefault(t => t.FullName == full && t.Confirmed);
    }

    public static ApplyCatalog Load(string path) => JsonSerializer.Deserialize<ApplyCatalog>(File.ReadAllText(path), Json)
        ?? throw new JsonException("Catalog was empty.");
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false));
}
