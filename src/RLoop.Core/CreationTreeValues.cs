using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

internal static class CreationTreeValues
{
    public static IReadOnlyDictionary<string, JsonElement> MergeFields(ApplyComponentSpec component)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in component.InitialFields ?? new Dictionary<string, JsonElement>()) fields[pair.Key] = pair.Value;
        foreach (var pair in component.Fields ?? new Dictionary<string, JsonElement>()) fields[pair.Key] = pair.Value;
        return fields.Where(pair => WritesProperty(component, pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    public static bool WritesProperty(ApplyComponentSpec component, string member) =>
        component.PropertyModes?.GetValueOrDefault(member) is not ("runtime" or "driver-owned");

    public static bool ContainsReference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && StableSelectorSyntax.TryParse(value.GetString() ?? "", out _)) return true;
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsReference);
        if (value.ValueKind == JsonValueKind.Object) return value.EnumerateObject().Any(property => ContainsReference(property.Value));
        return false;
    }

    public static string Raw(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
        _ => value.GetRawText()
    };

    public static async Task<string> ResolveAsync(JsonElement value, IReadOnlyDictionary<string, string> slots,
        IReadOnlyDictionary<string, string> components, IResoniteClient client, CancellationToken cancellationToken)
    {
        if (value.ValueKind == JsonValueKind.String)
            return await ResolveStringAsync(value.GetString() ?? string.Empty, slots, components, client, cancellationToken);
        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return (await ResolveCompositeAsync(value, slots, components, client, cancellationToken))?.ToJsonString() ?? "null";
        return Raw(value);
    }

    private static async Task<JsonNode?> ResolveCompositeAsync(JsonElement value, IReadOnlyDictionary<string, string> slots,
        IReadOnlyDictionary<string, string> components, IResoniteClient client, CancellationToken cancellationToken)
    {
        if (value.ValueKind == JsonValueKind.String)
            return JsonValue.Create(await ResolveStringAsync(value.GetString() ?? string.Empty, slots, components, client, cancellationToken));
        if (value.ValueKind == JsonValueKind.Array)
        {
            var result = new JsonArray();
            foreach (var item in value.EnumerateArray()) result.Add(await ResolveCompositeAsync(item, slots, components, client, cancellationToken));
            return result;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var result = new JsonObject();
            foreach (var item in value.EnumerateObject()) result[item.Name] = await ResolveCompositeAsync(item.Value, slots, components, client, cancellationToken);
            return result;
        }
        return JsonNode.Parse(value.GetRawText());
    }

    private static async Task<string> ResolveStringAsync(string value, IReadOnlyDictionary<string, string> slots,
        IReadOnlyDictionary<string, string> components, IResoniteClient client, CancellationToken cancellationToken)
    {
        if (!StableSelectorSyntax.TryParse(value, out var selector)) return value;
        var stable = selector!;
        if (stable.Kind == "slot")
            return slots.TryGetValue(stable.Key, out var slotId) ? slotId : MissingReference(value);
        if (stable.Kind == "component")
            return components.TryGetValue(stable.Key, out var componentId) ? componentId : MissingReference(value);
        if (stable.Kind == "slot-member")
        {
            if (!slots.TryGetValue(stable.Key, out var slotId)) return MissingReference(value);
            var slot = await client.GetSlotAsync(slotId, 0, false, cancellationToken);
            if (slot.Id != slotId) throw Unverified($"Slot member reference '{value}' returned a different exact ID.");
            return slot.Members?.TryGetValue(stable.MemberName!, out var member) == true && !string.IsNullOrWhiteSpace(member.Id)
                ? member.Id! : MissingMember(value);
        }
        if (!components.TryGetValue(stable.Key, out var targetId)) return MissingReference(value);
        var observed = await client.GetComponentAsync(targetId, cancellationToken);
        if (observed.Id != targetId) throw Unverified($"Component member reference '{value}' returned a different exact ID.");
        return observed.Members.TryGetValue(stable.MemberName!, out var componentMember) && !string.IsNullOrWhiteSpace(componentMember.Id)
            ? componentMember.Id! : MissingMember(value);
    }

    private static string MissingReference(string selector) => throw new RLoopException("APPLY_REFERENCE_NOT_FOUND",
        $"Symbolic reference '{selector}' could not be resolved in this creation operation.", ExitCodes.ValidationFailed);

    private static string MissingMember(string selector) => throw new RLoopException("APPLY_MEMBER_REFERENCE_NOT_FOUND",
        $"Member reference '{selector}' was not found.", ExitCodes.ValidationFailed);

    private static RLoopException Unverified(string message) => new("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed);
}
