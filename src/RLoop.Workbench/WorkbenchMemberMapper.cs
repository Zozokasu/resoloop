using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>The mapped result of a member.read call: the member when known, else the reason it is unknown.</summary>
internal sealed record MemberReadResult(string? ComponentType, MemberValue? Member, string? UnknownReason);

/// <summary>
/// Maps Workbench member payloads (ObservedMember / MemberReadback JSON, see
/// .scratch/wb-core/WorldModel) onto Core models. Pure conversion: no RPC, no transport.
/// </summary>
internal static class WorkbenchMemberMapper
{
    /// <summary>Maps one ObservedMember JSON object ({"kind":"field|reference|list|syncObject|opaque", ...}) to a MemberValue.</summary>
    public static MemberValue MapMember(JsonElement observedMember)
    {
        JsonElement member = WireJson.Object(observedMember, "observed member");
        string? id = WireJson.OptionalString(member, "id");
        string kind = WireJson.String(member, "kind", "observed member");

        if (kind.Equals("field", StringComparison.OrdinalIgnoreCase))
        {
            // The direct path fills Type with the CLR name (System.Single); the Workbench reports
            // wire names (float, float3). Not translatable without guessing, so Type stays null
            // except for enums, where enumType carries the same type name the direct path reports.
            string valueType = WireJson.String(member, "valueType", "observed field");
            string? type = valueType.Equals("enum", StringComparison.OrdinalIgnoreCase)
                ? WireJson.OptionalString(member, "enumType")
                : null;
            return new MemberValue("field", id, type, FieldValue(member));
        }
        if (kind.Equals("reference", StringComparison.OrdinalIgnoreCase))
            return new MemberValue("reference", id,
                TargetId: WireJson.OptionalString(member, "targetId"),
                TargetType: WireJson.OptionalString(member, "targetType"));
        if (kind.Equals("list", StringComparison.OrdinalIgnoreCase))
            return new MemberValue("list", id,
                Elements: WireJson.Array(member, "elements", "observed list")
                    .EnumerateArray().Select(MapMember).ToArray());
        if (kind.Equals("syncObject", StringComparison.OrdinalIgnoreCase))
            return new MemberValue("syncObject", id,
                Members: WireJson.ObjectProperty(member, "members", "observed sync object")
                    .EnumerateObject()
                    .ToDictionary(pair => pair.Name, pair => MapMember(pair.Value), StringComparer.Ordinal));
        if (kind.Equals("opaque", StringComparison.OrdinalIgnoreCase))
        {
            // Arrays, dictionaries, playback and empty members arrive opaque: the Workbench does
            // not decode their contents, so unlike the direct path there is no Members, Elements
            // or Value to fill. Only the kind survives; playback additionally reports the
            // well-known SyncPlayback type so kind checks keep working.
            string wireType = WireJson.String(member, "wireType", "observed opaque member");
            if (wireType.Equals("empty", StringComparison.OrdinalIgnoreCase))
                return new MemberValue("empty", id);
            if (wireType.Equals("playback", StringComparison.OrdinalIgnoreCase))
                return new MemberValue("SyncPlayback", id, "[FrooxEngine]FrooxEngine.SyncPlayback");
            return new MemberValue("opaque", id, wireType);
        }
        throw WorkbenchErrors.Unavailable($"The Workbench returned an observed member with unknown kind '{kind}'.");
    }

    /// <summary>Maps the `result` of member.read (MemberReadback JSON).</summary>
    public static MemberReadResult MapReadback(JsonElement memberReadResult)
    {
        JsonElement result = WireJson.Object(memberReadResult, "member readback");
        string? componentType = WireJson.OptionalString(result, "componentType");
        if (result.TryGetProperty("member", out JsonElement memberElement) &&
            memberElement.ValueKind != JsonValueKind.Null)
            return new MemberReadResult(componentType, MapMember(memberElement), null);
        return new MemberReadResult(componentType, null, WireJson.OptionalString(result, "unknownReason"));
    }

    private static JsonNode? FieldValue(JsonElement member)
    {
        // valueJson is the wire value as a compact JSON string; null or absent means the read did
        // not report a value. A reported JSON literal "null" also maps to null - the two are
        // indistinguishable here and Core treats both the same.
        if (!member.TryGetProperty("valueJson", out JsonElement valueJson) ||
            valueJson.ValueKind == JsonValueKind.Null)
            return null;
        if (valueJson.ValueKind != JsonValueKind.String)
            throw WorkbenchErrors.Unavailable("The Workbench returned a field whose valueJson is not a string.");
        try
        {
            return JsonNode.Parse(valueJson.GetString()!);
        }
        catch (JsonException ex)
        {
            throw WorkbenchErrors.Unavailable("The Workbench returned a field whose valueJson is not valid JSON.", ex);
        }
    }
}

/// <summary>Small strict readers over a Workbench JSON object. Malformed shapes raise WORKBENCH_UNAVAILABLE.</summary>
internal static class WireJson
{
    public static JsonElement Object(JsonElement element, string what) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} (expected an object, got {element.ValueKind}).");

    public static JsonElement ObjectProperty(JsonElement element, string name, string what) =>
        Object(element, what).TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} ('{name}' is missing or not an object).");

    public static JsonElement? OptionalObject(JsonElement element, string name, string what)
    {
        if (!Object(element, what).TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} ('{name}' is not an object).");
    }

    public static JsonElement Array(JsonElement element, string name, string what) =>
        Object(element, what).TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} ('{name}' is missing or not an array).");

    public static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string String(JsonElement element, string name, string what) =>
        OptionalString(element, name)
        ?? throw WorkbenchErrors.Unavailable(
            $"The Workbench returned a malformed {what} ('{name}' is missing or not a string).");

    public static bool Bool(JsonElement element, string name, string what) =>
        Object(element, what).TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} ('{name}' is missing or not a boolean).");

    public static long Int64(JsonElement element, string name, string what) =>
        Object(element, what).TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : throw WorkbenchErrors.Unavailable(
                $"The Workbench returned a malformed {what} ('{name}' is missing or not an integer).");
}
