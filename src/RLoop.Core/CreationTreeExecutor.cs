using System.Text.Json;
using System.Text.Json.Nodes;

namespace RLoop.Core;

internal sealed record CreationPreparedTree(ApplyDocument Definition, string RootKey,
    IReadOnlyList<CreationTreeExecutor.SlotPlan> Slots, IReadOnlyList<CreationTreeExecutor.ComponentPlan> Components,
    IReadOnlyDictionary<string, string> ResolvedTypes);

internal sealed class CreationTreeExecutor(IResoniteClient client)
{
    internal sealed record SlotPlan(string Key, string? ParentKey, ApplySlotSpec Spec);
    internal sealed record ComponentPlan(string ReferenceKey, string SlotKey, ApplyComponentSpec Spec,
        IReadOnlyDictionary<string, JsonElement> Fields, string RuntimeType);

    public async Task<CreationPreparedTree> PrepareAsync(ApplyDocument definition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidatePlannedBounds(definition);
        RejectUnsupportedAssets(definition);
        if (definition.Slot is null || string.IsNullOrWhiteSpace(definition.Slot.Key))
            throw Invalid("Creation tree root requires a stable Slot key.");
        var plannedDefinition = AssignMissingSlotKeys(definition);
        var plannedRoot = plannedDefinition.Slot ?? throw Invalid("Creation tree root Slot is missing.");

        var slots = new List<SlotPlan>();
        var components = new List<ComponentPlan>();
        var runtimeTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalizedTypes = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ApplySlotSpec slot, IReadOnlyList<ApplyComponentSpec>? componentSpecs,
            IReadOnlyList<ApplyNodeSpec>? children, string? parentKey)
        {
            var key = slot.Key;
            if (string.IsNullOrWhiteSpace(key)) throw Invalid("Every Slot in a creation tree requires a stable key.");
            slots.Add(new SlotPlan(key, parentKey, slot));
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var spec in componentSpecs ?? [])
            {
                var normalized = StableEffectiveKeys.NormalizeType(spec.Type);
                var ordinal = ordinals.GetValueOrDefault(normalized);
                ordinals[normalized] = ordinal + 1;
                var referenceKey = spec.Key ?? StableEffectiveKeys.Component(key, normalized, ordinal);
                normalizedTypes.Add(spec.Type);
                components.Add(new ComponentPlan(referenceKey, key, spec, CreationTreeValues.MergeFields(spec), ""));
            }
            foreach (var child in children ?? []) Walk(child.Slot, child.Components, child.Children, key);
        }

        Walk(plannedRoot, plannedDefinition.Components, plannedDefinition.Children, null);
        foreach (var type in normalizedTypes)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = await client.DescribeComponentTypeAsync(type, ct);
            runtimeTypes[type] = metadata.FullTypeName;
        }

        var strictDefinition = FilterRuntimeFields(plannedDefinition);
        var validation = await ApplyDocumentValidator.ValidateAsync(strictDefinition, client, ct, runtimeTypes);
        ApplyDocumentValidator.ThrowIfInvalid(validation);
        var resolvedComponents = components.Select(component => component with
        {
            RuntimeType = runtimeTypes[component.Spec.Type]
        }).ToArray();
        return new CreationPreparedTree(definition, plannedRoot.Key!, slots, resolvedComponents, runtimeTypes);
    }

    public async Task<IReadOnlyList<object?>> ExecuteAsync(CreationPreparedTree tree, string exactRootId, Func<Func<Task>, Task> send,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(send);
        if (string.IsNullOrWhiteSpace(exactRootId) || exactRootId.Equals("Root", StringComparison.OrdinalIgnoreCase))
            throw Invalid("The exact already-created root ID is required.");

        var slotIds = new Dictionary<string, string>(StringComparer.Ordinal) { [tree.RootKey] = exactRootId };
        var componentIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var allIds = new HashSet<string>(StringComparer.Ordinal) { exactRootId };
        foreach (var slot in tree.Slots.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            if (slot.ParentKey is null || !slotIds.TryGetValue(slot.ParentKey, out var parentId))
                throw Invalid($"Parent Slot for '{slot.Key}' was not created.");
            string? createdId = null;
            await send(async () =>
            {
                createdId = await client.CreateSlotAsync(new SlotCreateRequest(parentId, slot.Spec.Name,
                    slot.Spec.Position is null ? null : new Vector3Value(slot.Spec.Position[0], slot.Spec.Position[1], slot.Spec.Position[2]),
                    slot.Spec.Rotation is null ? null : new QuaternionValue(slot.Spec.Rotation[0], slot.Spec.Rotation[1], slot.Spec.Rotation[2], slot.Spec.Rotation[3]),
                    slot.Spec.Scale is null ? null : new Vector3Value(slot.Spec.Scale[0], slot.Spec.Scale[1], slot.Spec.Scale[2]),
                    Tag: slot.Spec.Tag), ct);
            });
            var id = RequireNewId(createdId, slot.Key, allIds);
            slotIds.Add(slot.Key, id);
            var observed = await client.GetSlotAsync(id, 0, false, ct);
            if (observed.Id != id || observed.ParentId != parentId || observed.Name != slot.Spec.Name || observed.IsReferenceOnly ||
                !SlotValuesMatch(observed, slot.Spec))
                throw Unverified($"Created Slot '{slot.Key}' did not match its exact-ID readback.");
        }

        var diagnostics = new List<object?>();
        var deferred = new List<(ComponentPlan Component, IReadOnlyDictionary<string, JsonElement> Fields)>();
        foreach (var component in tree.Components)
        {
            ct.ThrowIfCancellationRequested();
            if (!slotIds.TryGetValue(component.SlotKey, out var slotId)) throw Invalid($"Owner Slot for '{component.ReferenceKey}' was not created.");
            var immediate = new Dictionary<string, string>(StringComparer.Ordinal);
            var late = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var field in component.Fields)
            {
                if (CreationTreeValues.ContainsReference(field.Value)) late[field.Key] = field.Value;
                else immediate[field.Key] = CreationTreeValues.Raw(field.Value);
            }
            string? createdId = null;
            string? resultType = null;
            await send(async () =>
            {
                var result = await client.AddComponentAsync(slotId, component.RuntimeType, immediate, ct);
                createdId = result.Id;
                resultType = result.Type;
            });
            var id = RequireNewId(createdId, component.ReferenceKey, allIds);
            if (!SameType(resultType, component.RuntimeType))
                throw Unverified($"AddComponent returned unexpected type '{resultType}' for '{component.ReferenceKey}'.");
            try
            {
                await VerifyComponentAsync(id, component.RuntimeType, immediate, ct);
            }
            catch (RLoopException ex) when (ex.Code == "COMPONENT_NOT_FOUND")
            {
                // Resonite may drop a declared Component as a duplicate of one it added itself; use that one if it is unambiguous.
                var owner = await client.GetSlotAsync(slotId, 0, false, ct);
                var candidates = (owner.Components ?? []).Where(candidate => !allIds.Contains(candidate.Id) &&
                    StableEffectiveKeys.NormalizeType(candidate.Type) == StableEffectiveKeys.NormalizeType(component.RuntimeType)).ToArray();
                if (owner.Id != slotId || candidates.Length != 1) throw;
                id = candidates[0].Id;
                allIds.Add(id);
                if (immediate.Count > 0)
                    await send(() => client.SetComponentMembersAsync(id, component.RuntimeType, immediate, ct));
                await VerifyComponentAsync(id, component.RuntimeType, immediate, ct);
                diagnostics.Add(new Dictionary<string, object?>
                {
                    ["code"] = "CREATION_COMPONENT_MERGED_WITH_ENGINE_ADDED",
                    ["severity"] = "warning",
                    ["slotKey"] = component.SlotKey,
                    ["componentKey"] = component.ReferenceKey,
                    ["type"] = component.RuntimeType,
                    ["componentId"] = id,
                    ["message"] = "Resonite removed the declared Component as a duplicate of one it had added itself; the declared values were applied to the engine-added Component."
                });
            }
            if (!componentIds.TryAdd(component.ReferenceKey, id)) throw Invalid($"Duplicate effective Component key '{component.ReferenceKey}'.");
            await VerifyComponentOwnerAsync(slotId, id, ct);
            if (late.Count > 0) deferred.Add((component, late));
        }

        foreach (var item in deferred)
        {
            ct.ThrowIfCancellationRequested();
            var id = componentIds[item.Component.ReferenceKey];
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in item.Fields)
                resolved[field.Key] = await CreationTreeValues.ResolveAsync(field.Value, slotIds, componentIds, client, ct);
            await send(() => client.SetComponentMembersAsync(id, item.Component.RuntimeType, resolved, ct));
            await VerifyComponentAsync(id, item.Component.RuntimeType, resolved, ct);
        }
        return diagnostics;
    }

    private async Task VerifyComponentAsync(string id, string type, IReadOnlyDictionary<string, string> fields, CancellationToken ct)
    {
        var observed = await client.GetComponentAsync(id, ct);
        if (observed.Id != id || StableEffectiveKeys.NormalizeType(observed.Type) != StableEffectiveKeys.NormalizeType(type))
            throw Unverified($"Component '{id}' did not match its exact-ID type readback.");
        foreach (var field in fields)
            if (!observed.Members.TryGetValue(field.Key, out var member) || !WorldService.MemberMatchesRaw(member, field.Value))
                throw Unverified($"Component '{id}' member '{field.Key}' did not match its exact-ID readback.");
    }

    private async Task VerifyComponentOwnerAsync(string ownerId, string componentId, CancellationToken ct)
    {
        var owner = await client.GetSlotAsync(ownerId, 0, false, ct);
        if (owner.Id != ownerId || owner.Components is null || owner.Components.Count(component => component.Id == componentId) != 1)
            throw Unverified($"Component '{componentId}' was not observed exactly once under its exact owner Slot '{ownerId}'.");
    }

    internal static bool SlotValuesMatch(SlotInfo observed, ApplySlotSpec desired)
    {
        if (desired.Tag is not null && !WorldService.MemberMatchesRaw(
                new MemberValue("field", Type: "System.String", Value: JsonValue.Create(observed.Tag)), desired.Tag)) return false;
        if (desired.Position is { } position)
        {
            if (observed.Position is not { } actualPosition) return false;
            var member = new MemberValue("field", Type: "float3", Value: new JsonArray(actualPosition.X, actualPosition.Y, actualPosition.Z));
            var vector = new Vector3Value(position[0], position[1], position[2]);
            var expected = JsonSerializer.Serialize(new[] { vector.X, vector.Y, vector.Z });
            if (!WorldService.MemberMatchesRaw(member, expected)) return false;
        }
        if (desired.Rotation is { } rotation)
        {
            if (observed.Rotation is not { } actualRotation) return false;
            var member = new MemberValue("field", Type: "float4", Value: new JsonArray(actualRotation.X, actualRotation.Y, actualRotation.Z, actualRotation.W));
            var quaternion = new QuaternionValue(rotation[0], rotation[1], rotation[2], rotation[3]);
            var expected = JsonSerializer.Serialize(new[] { quaternion.X, quaternion.Y, quaternion.Z, quaternion.W });
            if (!WorldService.MemberMatchesRaw(member, expected)) return false;
        }
        if (desired.Scale is { } scale)
        {
            if (observed.Scale is not { } actualScale) return false;
            var member = new MemberValue("field", Type: "float3", Value: new JsonArray(actualScale.X, actualScale.Y, actualScale.Z));
            var vector = new Vector3Value(scale[0], scale[1], scale[2]);
            var expected = JsonSerializer.Serialize(new[] { vector.X, vector.Y, vector.Z });
            if (!WorldService.MemberMatchesRaw(member, expected)) return false;
        }
        return true;
    }

    private static bool SameType(string? observed, string expected) => observed is not null &&
        StableEffectiveKeys.NormalizeType(observed) == StableEffectiveKeys.NormalizeType(expected);

    private static ApplyDocument AssignMissingSlotKeys(ApplyDocument definition)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rootSlot = definition.Slot ?? throw Invalid("Creation tree root Slot is missing.");
        if (rootSlot.Key is { } rootKey) used.Add(rootKey);
        void Collect(IReadOnlyList<ApplyNodeSpec>? children)
        {
            foreach (var child in children ?? [])
            {
                if (!string.IsNullOrWhiteSpace(child.Slot.Key)) used.Add(child.Slot.Key);
                Collect(child.Children);
            }
        }
        Collect(definition.Children);
        var sequence = 0;
        string NextKey()
        {
            string candidate;
            do
            {
                var index = sequence++;
                candidate = "__creation_slot_" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            }
            while (!used.Add(candidate));
            return candidate;
        }
        ApplyNodeSpec Walk(ApplyNodeSpec node)
        {
            var slot = string.IsNullOrWhiteSpace(node.Slot.Key) ? node.Slot with { Key = NextKey() } : node.Slot;
            return node with { Slot = slot, Children = (node.Children ?? []).Select(Walk).ToArray() };
        }
        return definition with { Children = (definition.Children ?? []).Select(Walk).ToArray() };
    }

    private static void ValidatePlannedBounds(ApplyDocument definition)
    {
        var queue = new Queue<(ApplySlotSpec? Slot, IReadOnlyList<ApplyComponentSpec>? Components,
            IReadOnlyList<ApplyNodeSpec>? Children, int Depth)>();
        queue.Enqueue((definition.Slot, definition.Components, definition.Children, 0));
        var slots = 0;
        var components = 1; // Reserve the creation stamp that is added outside this tree.
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            slots++;
            if (slots > CreationTreeVerification.MaxSlots)
                throw Invalid("Creation tree exceeds the 10000 Slot verification limit.");
            if (current.Depth > CreationTreeVerification.MaxDepth)
                throw Invalid("Creation tree exceeds the depth 64 verification limit.");
            components += current.Components?.Count ?? 0;
            if (components > CreationTreeVerification.MaxComponents)
                throw Invalid("Creation tree exceeds the 10000 Component verification limit including its creation stamp.");
            foreach (var child in current.Children ?? [])
            {
                if (child is null) continue;
                if (current.Depth >= CreationTreeVerification.MaxDepth)
                    throw Invalid("Creation tree exceeds the depth 64 verification limit.");
                if (queue.Count >= CreationTreeVerification.MaxQueuedSlots)
                    throw Invalid("Creation tree exceeds the 10000 pending Slot verification limit.");
                queue.Enqueue((child.Slot, child.Components, child.Children, current.Depth + 1));
            }
        }
    }

    private static ApplyDocument FilterRuntimeFields(ApplyDocument definition)
    {
        ApplyComponentSpec Filter(ApplyComponentSpec component) => component with
        {
            Fields = (component.Fields ?? new Dictionary<string, JsonElement>())
                .Where(pair => CreationTreeValues.WritesProperty(component, pair.Key)).ToDictionary(StringComparer.Ordinal),
            InitialFields = (component.InitialFields ?? new Dictionary<string, JsonElement>())
                .Where(pair => CreationTreeValues.WritesProperty(component, pair.Key)).ToDictionary(StringComparer.Ordinal),
            IdentityFields = []
        };
        ApplyNodeSpec Walk(ApplyNodeSpec node) => node with
        {
            Components = (node.Components ?? []).Select(Filter).ToArray(),
            Children = (node.Children ?? []).Select(Walk).ToArray()
        };
        return definition with
        {
            Components = (definition.Components ?? []).Select(Filter).ToArray(),
            Children = (definition.Children ?? []).Select(Walk).ToArray()
        };
    }

    private static void RejectUnsupportedAssets(ApplyDocument definition)
    {
        const string message = "This content is not supported by regenerate in this version.";
        var issues = new List<ApplyValidationIssue>();
        void Add(string path) => issues.Add(new ApplyValidationIssue("CREATION_DOCUMENT_INVALID", message, path));
        foreach (var asset in definition.Assets ?? new Dictionary<string, ApplyAssetSpec>()) Add("$.definition.assets." + asset.Key);

        void Visit(ApplySlotSpec? slot, IReadOnlyList<ApplyComponentSpec>? components,
            IReadOnlyList<ApplyNodeSpec>? children, string path)
        {
            for (var index = 0; index < (components?.Count ?? 0); index++)
            {
                var component = components![index];
                if (component is null) continue;
                var componentPath = $"{path}.components[{index}]";
                CheckFields(component.Fields, componentPath + ".fields");
                CheckFields(component.InitialFields, componentPath + ".initialFields");
            }
            for (var index = 0; index < (children?.Count ?? 0); index++)
            {
                var child = children![index];
                if (child is not null) Visit(child.Slot, child.Components, child.Children, $"{path}.children[{index}]");
            }
        }

        void CheckFields(IReadOnlyDictionary<string, JsonElement>? fields, string path)
        {
            foreach (var field in fields ?? new Dictionary<string, JsonElement>()) CheckValue(field.Value, path + "." + field.Key);
        }

        void CheckValue(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString()?.StartsWith("$asset:", StringComparison.Ordinal) == true)
            {
                Add(path);
                return;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray()) CheckValue(item, $"{path}[{index++}]");
            }
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject()) CheckValue(property.Value, path + "." + property.Name);
        }

        Visit(definition.Slot, definition.Components, definition.Children, "$.definition");
        if (issues.Count > 0)
            throw new RLoopException("CREATION_DOCUMENT_INVALID", message, ExitCodes.ValidationFailed,
                new Dictionary<string, object?> { ["issues"] = issues });
    }

    private static string RequireNewId(string? id, string key, HashSet<string> allIds)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("Root", StringComparison.OrdinalIgnoreCase) || !allIds.Add(id))
            throw Unverified($"Creation of '{key}' returned an empty, reserved, or duplicate ID.");
        return id;
    }

    private static RLoopException Invalid(string message) => new("APPLY_VALIDATION_FAILED", message, ExitCodes.ValidationFailed);
    private static RLoopException Unverified(string message) => new("APPLY_WRITE_UNVERIFIED", message, ExitCodes.OperationFailed);
}
