using System.Text.Json;
using System.Text.Json.Serialization;
using ResoniteWorkbench.Protocol;
using RLoop.Core;

namespace RLoop.Workbench;

/// <summary>
/// Converts the result of a <c>world.observe</c> call (<c>WorldQueryResult&lt;WorldSnapshot&gt;</c>
/// serialized with <see cref="WorkbenchJson.Options"/>) into the <see cref="SlotInfo"/> tree
/// <see cref="IResoniteClient.GetSlotAsync"/> returns on the direct-link backend. Pure mapping:
/// no RPC and no shared state, so all malformed input becomes <c>WORKBENCH_UNAVAILABLE</c>.
/// </summary>
internal static class WorkbenchSlotMapper
{
    /// <summary>Mirrors <c>ResoniteWorkbench.Core.WorldModel.TruncationReasons</c> (a flags enum).</summary>
    [Flags]
    private enum Truncation
    {
        None = 0,
        DepthLimit = 1,
        SlotLimit = 2,
        ReadFailed = 4,
    }

    /// <summary>Transform parts arrive as a string of wire JSON, so they parse with their own options.</summary>
    private static readonly JsonSerializerOptions WireValueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>
    /// Validates depth (WorkbenchLimits.RequireDepth(depth, "GetSlotAsync")) and returns the
    /// world.observe parameters: ScopeRootId = rootId, MaxDepth = Math.Max(1, depth)
    /// (world.observe accepts 1..32), MaxSlots = WorkbenchLimits.MaxObserveSlots.
    /// </summary>
    public static WorldObserveParams BuildObserveParams(string rootId, int depth)
    {
        WorkbenchLimits.RequireDepth(depth, "GetSlotAsync");
        // world.observe rejects maxDepth 0, so a root-only request still observes one level and
        // the extra records only give the stubs below the requested depth their real names.
        return new WorldObserveParams(rootId, Math.Max(1, depth), WorkbenchLimits.MaxObserveSlots);
    }

    /// <summary>
    /// Maps the <c>result</c> element of a world.observe response
    /// (<c>WorldQueryResult&lt;WorldSnapshot&gt;</c> as JSON) to a <see cref="SlotInfo"/> tree.
    /// </summary>
    public static SlotInfo MapObservation(JsonElement queryResult, string requestedRootId, int depth)
    {
        if (queryResult.ValueKind != JsonValueKind.Object)
            throw Malformed("expected a WorldQueryResult object.");

        JsonElement value = Required(queryResult, "value", "result");
        bool unknown = RequiredCompleteness(queryResult) == Completeness.Unknown;
        string? unknownReason = OptionalNullableString(queryResult, "unknownReason", "result");

        if (unknown || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw WorkbenchErrors.NotFound("SLOT_NOT_FOUND",
                $"world.observe returned no snapshot for '{requestedRootId}'" +
                (unknownReason is null ? "." : $": {unknownReason}") +
                " A Workbench 'Unknown' completeness is not proof that the slot does not exist; " +
                "the scope may be unobserved or the read may have failed.",
                new Dictionary<string, object?> { ["slotId"] = requestedRootId });
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw Malformed("result.value is not a WorldSnapshot object.");

        if (queryResult.TryGetProperty("provenance", out JsonElement provenance)
            && provenance.ValueKind != JsonValueKind.Null)
        {
            if (provenance.ValueKind != JsonValueKind.Object)
                throw Malformed("result.provenance is not an object.");
            if (provenance.TryGetProperty("isStale", out JsonElement isStale))
            {
                if (isStale.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Malformed("result.provenance.isStale is not a boolean.");
                if (isStale.GetBoolean())
                    throw WorkbenchErrors.Unavailable(
                        $"The world.observe snapshot for '{requestedRootId}' is stale; " +
                        "the connection changed or the answer outlived the freshness window.");
            }
        }

        string scopeRootId = RequiredString(value, "scopeRootId", "value");
        Truncation truncation = RequiredTruncation(value, "truncation", "value");
        return MapSnapshot(value, scopeRootId, requestedRootId, truncation, depth);
    }

    private static SlotInfo MapSnapshot(JsonElement value, string scopeRootId, string requestedRootId,
        Truncation truncation, int depth)
    {
        if ((truncation & Truncation.SlotLimit) != 0)
        {
            throw WorkbenchLimits.LimitExceeded(
                $"world.observe truncated the subtree of '{scopeRootId}' at {WorkbenchLimits.MaxObserveSlots:N0} slots; " +
                "ResoLoop never maps a silently truncated tree.");
        }

        JsonElement slotsElement = Required(value, "slots", "value");
        if (slotsElement.ValueKind != JsonValueKind.Object)
            throw Malformed("value.slots is not a slot-id -> SlotRecord object.");
        var slots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in slotsElement.EnumerateObject())
            slots[property.Name] = property.Value;

        IReadOnlyDictionary<string, string?> unexpanded = ReadStubNames(value, "unexpanded");
        IReadOnlyDictionary<string, string?> excluded = ReadStubNames(value, "excluded");

        if (!slots.TryGetValue(scopeRootId, out JsonElement root))
        {
            throw WorkbenchErrors.Unavailable(
                $"The world.observe snapshot has no record for its scope root '{scopeRootId}' " +
                $"(requested '{requestedRootId}'); the response violates the protocol.");
        }

        var ancestors = new HashSet<string>(StringComparer.Ordinal) { scopeRootId };
        return BuildSlot(root, scopeRootId, level: 0, depth, slots, unexpanded, excluded, ancestors);
    }

    private static SlotInfo BuildSlot(
        JsonElement record,
        string slotId,
        int level,
        int depth,
        IReadOnlyDictionary<string, JsonElement> slots,
        IReadOnlyDictionary<string, string?> unexpanded,
        IReadOnlyDictionary<string, string?> excluded,
        HashSet<string> ancestors)
    {
        if (record.ValueKind != JsonValueKind.Object)
            throw Malformed($"slot '{slotId}' record is not an object.");

        string path = $"slot '{slotId}'";
        string id = RequiredString(record, "id", path);
        string? name = RequiredNullableString(record, "name", path);
        string? parentId = RequiredNullableString(record, "parentId", path);
        bool? isActive = RequiredNullableBool(record, "isActive", path);
        _ = RequiredInt(record, "level", path); // required on the wire; the walk recomputes levels.
        JsonElement childIds = Required(record, "childIds", path);
        JsonElement components = Required(record, "components", path);
        string? tag = OptionalNullableString(record, "tag", path);
        bool? persistent = OptionalNullableBool(record, "persistent", path);
        bool isReferenceOnly = OptionalNullableBool(record, "isReferenceOnly", path) ?? false;

        Vector3Value? position = null;
        QuaternionValue? rotation = null;
        Vector3Value? scale = null;
        if (record.TryGetProperty("transform", out JsonElement transform)
            && transform.ValueKind != JsonValueKind.Null)
        {
            if (transform.ValueKind != JsonValueKind.Object)
                throw Malformed($"{path}.transform is not an object.");
            position = ParseFloat3(OptionalNullableString(transform, "position", $"{path}.transform"), $"{path}.transform.position");
            rotation = ParseFloatQ(OptionalNullableString(transform, "rotation", $"{path}.transform"), $"{path}.transform.rotation");
            scale = ParseFloat3(OptionalNullableString(transform, "scale", $"{path}.transform"), $"{path}.transform.scale");
        }

        var componentSummaries = new List<ComponentSummary>();
        if (components.ValueKind != JsonValueKind.Array)
            throw Malformed($"{path}.components is not an array.");
        foreach (JsonElement component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object)
                throw Malformed($"{path}.components entry is not an object.");
            componentSummaries.Add(new ComponentSummary(
                RequiredString(component, "id", $"{path}.components entry"),
                RequiredNullableString(component, "componentType", $"{path}.components entry") ?? string.Empty));
        }

        if (childIds.ValueKind != JsonValueKind.Array)
            throw Malformed($"{path}.childIds is not an array.");
        var children = new List<SlotInfo>();
        foreach (JsonElement childElement in childIds.EnumerateArray())
        {
            if (childElement.ValueKind != JsonValueKind.String)
                throw Malformed($"{path}.childIds contains a non-string entry.");
            string childId = childElement.GetString()!;

            if (level + 1 <= depth && slots.TryGetValue(childId, out JsonElement childRecord))
            {
                if (!ancestors.Add(childId))
                    throw Malformed($"slot '{childId}' appears twice on one path; the hierarchy is not a tree.");
                children.Add(BuildSlot(childRecord, childId, level + 1, depth, slots, unexpanded, excluded, ancestors));
                ancestors.Remove(childId);
            }
            else
            {
                children.Add(new SlotInfo(
                    childId,
                    StubName(childId, slots, unexpanded, excluded),
                    id,
                    null, null, null, null, null, null,
                    IsReferenceOnly: true,
                    Components: [], Children: []));
            }
        }

        return new SlotInfo(
            id,
            name ?? string.Empty,
            parentId,
            position, rotation, scale,
            isActive, persistent, tag, isReferenceOnly,
            componentSummaries, children,
            Path: null, Members: null);
    }

    /// <summary>
    /// The stub name is taken from what the snapshot knows about the slot: its unexpanded entry,
    /// its excluded entry, or its full record when it was read but lies below the requested depth.
    /// </summary>
    private static string StubName(
        string childId,
        IReadOnlyDictionary<string, JsonElement> slots,
        IReadOnlyDictionary<string, string?> unexpanded,
        IReadOnlyDictionary<string, string?> excluded)
    {
        if (unexpanded.TryGetValue(childId, out string? name)) return name ?? string.Empty;
        if (excluded.TryGetValue(childId, out name)) return name ?? string.Empty;
        if (slots.TryGetValue(childId, out JsonElement record)
            && record.ValueKind == JsonValueKind.Object
            && record.TryGetProperty("name", out JsonElement nameElement)
            && nameElement.ValueKind == JsonValueKind.String)
            return nameElement.GetString() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>Reads the id/name pairs of an unexpanded/excluded entry list for stub naming.</summary>
    private static IReadOnlyDictionary<string, string?> ReadStubNames(JsonElement snapshot, string property)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!snapshot.TryGetProperty(property, out JsonElement list))
            throw Malformed($"value.{property} is missing.");
        if (list.ValueKind != JsonValueKind.Array)
            throw Malformed($"value.{property} is not an array.");
        foreach (JsonElement entry in list.EnumerateArray())
        {
            string path = $"value.{property} entry";
            if (entry.ValueKind != JsonValueKind.Object)
                throw Malformed($"{path} is not an object.");
            string id = RequiredString(entry, "id", path);
            string? name = RequiredNullableString(entry, "name", path);
            _ = RequiredString(entry, "parentId", path);
            _ = Required(entry, "reason", path); // present on the wire; any reason maps to a stub either way.
            names[id] = name;
        }
        return names;
    }

    /// <summary>
    /// Parses a wire float3 / floatQ string (e.g. "{\"x\":0,\"y\":1.5,\"z\":0}");
    /// null or JSON null -&gt; null.
    /// </summary>
    public static Vector3Value? ParseFloat3(string? wireJson, string fieldName)
    {
        WireFloat3? parsed = ParseWire<WireFloat3>(wireJson, fieldName);
        return parsed is null ? null : new Vector3Value(parsed.X, parsed.Y, parsed.Z);
    }

    /// <summary>Parses a wire floatQ string (e.g. "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}").</summary>
    public static QuaternionValue? ParseFloatQ(string? wireJson, string fieldName)
    {
        WireFloatQ? parsed = ParseWire<WireFloatQ>(wireJson, fieldName);
        return parsed is null ? null : new QuaternionValue(parsed.X, parsed.Y, parsed.Z, parsed.W);
    }

    private static T? ParseWire<T>(string? wireJson, string fieldName) where T : class
    {
        if (wireJson is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(wireJson, WireValueOptions);
        }
        catch (JsonException ex)
        {
            throw Malformed($"{fieldName} is not a valid wire value: {ex.Message}", ex);
        }
    }

    private sealed record WireFloat3(float X, float Y, float Z);
    private sealed record WireFloatQ(float X, float Y, float Z, float W);

    private enum Completeness { Complete, Partial, Unknown }

    private static Completeness RequiredCompleteness(JsonElement result)
    {
        JsonElement element = Required(result, "completeness", "result");
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() switch
            {
                "Complete" => Completeness.Complete,
                "Partial" => Completeness.Partial,
                "Unknown" => Completeness.Unknown,
                var other => throw Malformed($"result.completeness '{other}' is not a known value."),
            };
        }
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int numeric))
        {
            return numeric switch
            {
                0 => Completeness.Complete,
                1 => Completeness.Partial,
                2 => Completeness.Unknown,
                _ => throw Malformed($"result.completeness {numeric} is not a known value."),
            };
        }
        throw Malformed("result.completeness is not a string or number.");
    }

    private static Truncation RequiredTruncation(JsonElement parent, string property, string path)
    {
        JsonElement element = Required(parent, property, path);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int bits))
            return (Truncation)bits;
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
        {
            Truncation result = Truncation.None;
            foreach (string part in text.Split(','))
            {
                string name = part.Trim();
                if (!Enum.TryParse(name, out Truncation flag))
                    throw Malformed($"{path}.{property} '{text}' contains the unknown flag '{name}'.");
                result |= flag;
            }
            return result;
        }
        throw Malformed($"{path}.{property} is not a flags string or a number.");
    }

    private static JsonElement Required(JsonElement parent, string property, string path)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value))
            throw Malformed($"{path}.{property} is missing.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string property, string path)
    {
        JsonElement value = Required(parent, property, path);
        if (value.ValueKind != JsonValueKind.String)
            throw Malformed($"{path}.{property} is not a string.");
        return value.GetString()!;
    }

    private static string? RequiredNullableString(JsonElement parent, string property, string path)
    {
        JsonElement value = Required(parent, property, path);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw Malformed($"{path}.{property} is not a string or null."),
        };
    }

    private static string? OptionalNullableString(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw Malformed($"{path}.{property} is not a string or null."),
        };
    }

    private static bool? RequiredNullableBool(JsonElement parent, string property, string path)
    {
        JsonElement value = Required(parent, property, path);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw Malformed($"{path}.{property} is not a boolean or null."),
        };
    }

    private static bool? OptionalNullableBool(JsonElement parent, string property, string path)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)) return null;
        return RequiredNullableBool(parent, property, path);
    }

    private static int RequiredInt(JsonElement parent, string property, string path)
    {
        JsonElement value = Required(parent, property, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
            throw Malformed($"{path}.{property} is not an integer.");
        return number;
    }

    private static RLoopException Malformed(string detail, Exception? innerException = null) =>
        WorkbenchErrors.Unavailable($"The world.observe result is malformed: {detail}", innerException);
}
