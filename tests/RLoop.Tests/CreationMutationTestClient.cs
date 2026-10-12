using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

internal sealed partial class CreationMutationTestClient : IResoniteClient, IApplyDeletionObservation,
    IApplyConnectionGuard, IApplySendEvidence, IApplySessionObservation
{
    private int _nextId;
    private int _mutation;
    private int _slotReadCount;
    private int _componentReadCount;
    private bool _sendAllowed;
    private bool _started;
    private bool _received;
    private bool _accepted;
    public Dictionary<string, SlotInfo> Slots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComponentInfo> Components { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComponentTypeInfo> Types { get; } = new(StringComparer.Ordinal);
    public List<string> SendLog { get; } = [];
    public List<string> RequestHistory { get; } = [];
    public List<string> FieldLog { get; } = [];
    public List<(string SlotId, string Type, IReadOnlyDictionary<string, string> Fields)> AddRequests { get; } = [];
    public List<(string ComponentId, string Type, IReadOnlyDictionary<string, string> Fields)> SetRequests { get; } = [];
    public int ThrowOnMutation { get; set; }
    /// <summary>Simulates the engine attaching this many extra Components when the generated-content Component is added.</summary>
    public int EngineAddedComponentsPerAdd { get; set; }
    /// <summary>Adding the key type makes the engine add the value type unless the Slot already has it.</summary>
    public Dictionary<string, string> EngineAutoAdds { get; } = new(StringComparer.Ordinal);
    /// <summary>Component IDs the engine accepted and then removed as duplicates.</summary>
    public HashSet<string> DroppedComponentIds { get; } = new(StringComparer.Ordinal);
    public bool RejectNextMutationResponse { get; set; }
    public string? IncompleteSlotId { get; set; }
    public Func<SlotInfo, int, SlotInfo>? SlotReadOverride { get; set; }
    public Func<ComponentInfo, int, ComponentInfo>? ComponentReadOverride { get; set; }
    public Action<string, string, string>? AfterAddComponent { get; set; }
    public List<string> SlotReadIds { get; } = [];
    public List<string> DeletionObservationIds { get; } = [];
    public List<string> ComponentReadIds { get; } = [];
    public CancellationTokenSource? CancelOnObserve { get; set; }
    public string? AddResultTypeOverride { get; set; }
    public int ValidationCalls { get; private set; }
    public string ConnectionGeneration { get; set; } = "generation";
    public string SessionUrl { get; set; } = "ws://localhost:47610/";
    public string SessionId { get; set; } = "S-test";
    public string SessionIdentityStatus { get; set; } = "matched";
    public int GuardCalls { get; private set; }
    public int? BeforeGuardCallNumber { get; set; }
    public Action? BeforeGuardAction { get; set; }
    public int ConnectCalls { get; private set; }
    public int SessionReadCount { get; private set; }
    public Action<int>? BeforeSessionRead { get; set; }
    public bool ApplySendStarted => _started;
    public bool ApplyResponseReceived => _received;
    public bool ApplyResponseAccepted => _accepted;

    public CreationMutationTestClient()
    {
        var existing = new SlotInfo("root-created", "Root", "parent", null, null, null, true, true,
            null, false, [], [], Members: new Dictionary<string, MemberValue>
            { ["Name"] = new MemberValue("field", "root-created:Name", "System.String", JsonValue.Create("Root")) });
        var parent = new SlotInfo("parent", "Parent", "Root", null, null, null, true, true, null, false, [], [existing]);
        Slots.Add("root-created", existing);
        Slots.Add("parent", parent);
        Slots.Add("Root", new SlotInfo("Root", "Root", null, null, null, null, true, true, null, false, [], [parent]));
        RegisterType(CreationOwnershipVerifier.VariableComponentType, "VariableName:System.String", "Value:System.String");
        RegisterType(GeneratedContentMetadata.ComponentType, GeneratedContentMetadata.SourceMember + ":System.String");
    }

    public void RegisterType(string type, params string[] members)
    {
        Types[type] = new ComponentTypeInfo(type, "FrooxEngine", null, false, members
            .Select(spec =>
            {
                var parts = spec.Split(':', 2);
                var valueType = parts.Length == 2 ? parts[1] : "System.String";
                return new MemberDefinitionInfo(parts[0], "field", valueType, valueType, null);
            }).ToArray());
    }

    public void RegisterReference(string type, string memberName)
    {
        var metadata = Types[type];
        Types[type] = metadata with
        {
            Members = metadata.Members.Select(member => member.Name == memberName ? member with { Kind = "reference" } : member).ToArray()
        };
    }

    public void BeginApplySend() { _sendAllowed = true; _started = false; _received = false; _accepted = false; }
    internal void MarkNextMutationResponseUnknown() { _received = false; _accepted = false; }
    public ApplySessionObservation ObserveApplySession() => new(ApplySessionObservation.NormalizeUrl(SessionUrl), SessionId, SessionIdentityStatus);
    public ApplyConnectionObservation ObserveApplyConnection() => new(true, ConnectionGeneration);
    public IDisposable GuardApplyWrites(string? plannedGeneration)
    {
        GuardCalls++;
        if (GuardCalls == BeforeGuardCallNumber)
        {
            BeforeGuardAction?.Invoke();
            BeforeGuardCallNumber = null;
            BeforeGuardAction = null;
        }
        if (plannedGeneration != ConnectionGeneration)
            throw new RLoopException("APPLY_PRECONDITION_FAILED", "Test connection generation changed.", ExitCodes.ValidationFailed);
        return new Guard();
    }

    public Task<ApplyDeletionObservation> ObserveDeletionSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        CancelOnObserve?.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        DeletionObservationIds.Add(id);
        var slot = SlotReadOverride?.Invoke(ReadSlot(id), ++_slotReadCount) ?? ReadSlot(id);
        SlotReadIds.Add(id);
        var complete = id != IncompleteSlotId;
        return Task.FromResult(new ApplyDeletionObservation(slot, complete, complete));
    }

    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var slot = ReadSlot(id);
        SlotReadIds.Add(id);
        return Task.FromResult(SlotReadOverride?.Invoke(slot, ++_slotReadCount) ?? slot);
    }

    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DroppedComponentIds.Contains(id)) throw new RLoopException("COMPONENT_NOT_FOUND", $"Exact test Component '{id}' was not found.", ExitCodes.NotFound);
        var component = Components.TryGetValue(id, out var found) ? found : throw Missing(id);
        ComponentReadIds.Add(id);
        return Task.FromResult(ComponentReadOverride?.Invoke(component, ++_componentReadCount) ?? component);
    }

    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
    {
        RequestHistory.Add($"create-slot:{request.ParentId}:{request.Name}");
        Mutating("create-slot");
        var id = "slot-" + ++_nextId;
        var child = new SlotInfo(id, request.Name, request.ParentId, request.Position, request.Rotation, request.Scale,
            request.IsActive, true, request.Tag, false, [], [], Members: new Dictionary<string, MemberValue>
            { ["Name"] = new MemberValue("field", id + ":Name", "System.String", JsonValue.Create(request.Name)) });
        Slots.Add(id, child);
        Replace(request.ParentId, slot => slot with { Children = slot.Children.Append(child).ToArray() });
        return Task.FromResult(id);
    }

    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        RequestHistory.Add("delete-slot:" + id);
        Mutating("delete-slot:" + id);
        if (!Slots.TryGetValue(id, out var slot)) throw Missing(id);
        Slots.Remove(id);
        if (slot.ParentId is { } parent && Slots.TryGetValue(parent, out var parentSlot))
            Replace(parent, current => current with { Children = current.Children.Where(child => child.Id != id).ToArray() });
        return Task.CompletedTask;
    }

    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        RequestHistory.Add($"add-component:{slotId}:{componentType}:{string.Join(',', fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))}");
        Mutating("add-component");
        FieldLog.Add($"add:{componentType}:{string.Join(',', fields.Keys)}");
        AddRequests.Add((slotId, componentType, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
        var id = "component-" + ++_nextId;
        if (EngineAutoAdds.ContainsValue(componentType) && Slots[slotId].Components.Any(value => value.Type == componentType))
        {
            DroppedComponentIds.Add(id);
            return Task.FromResult(new ComponentCreateResult(id, componentType));
        }
        var component = new ComponentInfo(id, componentType, Members(id, componentType, fields, includeDefaults: true));
        Components.Add(id, component);
        Replace(slotId, slot => slot with { Components = slot.Components.Append(new ComponentSummary(id, componentType)).ToArray() });
        if (EngineAddedComponentsPerAdd > 0 && componentType == GeneratedContentMetadata.ComponentType)
        {
            var added = Enumerable.Range(1, EngineAddedComponentsPerAdd)
                .Select(n => new ComponentInfo($"engine-added-{id}-{n}", "FrooxEngine.UIX.RectTransform",
                    new Dictionary<string, MemberValue>())).ToArray();
            foreach (var engine in added) Components.Add(engine.Id, engine);
            Replace(slotId, slot => slot with
            {
                Components = slot.Components.Concat(added.Select(engine => new ComponentSummary(engine.Id, engine.Type))).ToArray()
            });
        }
        if (EngineAutoAdds.TryGetValue(componentType, out var autoType) && Slots[slotId].Components.All(value => value.Type != autoType))
        {
            var autoId = "engine-auto-" + ++_nextId;
            var auto = new ComponentInfo(autoId, autoType, Members(autoId, autoType, new Dictionary<string, string>(), includeDefaults: true));
            Components.Add(autoId, auto);
            Replace(slotId, slot => slot with { Components = slot.Components.Append(new ComponentSummary(autoId, autoType)).ToArray() });
        }
        AfterAddComponent?.Invoke(slotId, componentType, id);
        return Task.FromResult(new ComponentCreateResult(id, AddResultTypeOverride ?? componentType));
    }

    public Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        Mutating("set-members");
        FieldLog.Add($"set:{componentType}:{string.Join(',', fields.Keys)}");
        SetRequests.Add((componentId, componentType, new Dictionary<string, string>(fields, StringComparer.Ordinal)));
        var component = Components[componentId];
        var members = component.Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var field in Members(componentId, componentType, fields, includeDefaults: false)) members[field.Key] = field.Value;
        Components[componentId] = component with { Members = members };
        return Task.CompletedTask;
    }

    private void Mutating(string operation)
    {
        if (!_sendAllowed) throw new InvalidOperationException("Mutation bypassed the send delegate.");
        _started = true;
        if (++_mutation == ThrowOnMutation) throw new RLoopException("TEST_SEND_FAILED", "Injected mutation failure.", ExitCodes.OperationFailed);
        SendLog.Add(operation);
        _received = true;
        _accepted = !RejectNextMutationResponse;
        RejectNextMutationResponse = false;
        if (!_accepted) throw new RLoopException("TEST_REJECTED", "Injected rejected mutation response.", ExitCodes.ValidationFailed);
    }

    private IReadOnlyDictionary<string, MemberValue> Members(string id, string type, IReadOnlyDictionary<string, string> fields, bool includeDefaults)
    {
        var result = new Dictionary<string, MemberValue>(StringComparer.Ordinal);
        var metadataMembers = Types.GetValueOrDefault(type)?.Members ?? [];
        if (includeDefaults) foreach (var metadata in metadataMembers)
        {
            var defaultValue = metadata.ValueType is "System.Boolean" or "bool" ? JsonValue.Create(false) : JsonValue.Create("");
            result[metadata.Name] = metadata.Kind == "reference"
                ? new MemberValue("reference", id + ":" + metadata.Name, metadata.ValueType, TargetId: "null")
                : new MemberValue("field", id + ":" + metadata.Name, metadata.ValueType, defaultValue);
        }
        foreach (var pair in fields)
        {
            var metadata = metadataMembers.FirstOrDefault(member => member.Name == pair.Key);
            if (metadata is null) throw new InvalidOperationException($"No fake metadata for {type}.{pair.Key}.");
            var stringLike = metadata.ValueType is "System.String" or "string" or "Uri" or "System.Uri";
            JsonNode? parsed = stringLike ? JsonValue.Create(pair.Value) : JsonNode.Parse(pair.Value);
            result[pair.Key] = metadata.Kind == "reference"
                ? new MemberValue("reference", id + ":" + pair.Key, metadata.ValueType, TargetId: pair.Value)
                : new MemberValue("field", id + ":" + pair.Key, metadata.ValueType, parsed);
        }
        return result;
    }

    private SlotInfo ReadSlot(string id) => Slots.TryGetValue(id, out var slot) ? slot : throw Missing(id);
    private void Replace(string id, Func<SlotInfo, SlotInfo> update) => Slots[id] = update(Slots[id]);
    private static RLoopException Missing(string id) => new("SLOT_NOT_FOUND", $"Exact test ID '{id}' was not found.", ExitCodes.NotFound);
    private sealed class Guard : IDisposable { public void Dispose() { } }

    public Task ValidateComponentMemberAsync(string componentType, string member, string rawValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidationCalls++;
        if (!Types.TryGetValue(componentType, out var type) || !type.Members.Any(value => value.Name == member))
            throw new RLoopException("COMPONENT_MEMBER_NOT_FOUND", $"Unknown fake member '{member}'.", ExitCodes.ValidationFailed);
        return Task.CompletedTask;
    }
    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) =>
        Task.FromResult(Types.TryGetValue(type, out var found) ? found : throw new RLoopException("COMPONENT_TYPE_NOT_FOUND", type, ExitCodes.NotFound));
    public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TypeInfo(type, "FrooxEngine", null, type, null, false, false, false, false, true, false, true, [], []));

    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) { ConnectCalls++; return Unsupported<Task>(); }
    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        var read = ++SessionReadCount;
        BeforeSessionRead?.Invoke(read);
        return Task.FromResult(new SessionInfo(SessionUrl, true, "test", "test", SessionId, ConnectionGeneration));
    }
    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => Unsupported<Task>();
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => Unsupported<Task>();
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => Unsupported<Task>();
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) => Unsupported<Task<IReadOnlyList<string>>>();
    public Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method, IReadOnlyDictionary<string, JsonElement>? arguments = null,
        CancellationToken cancellationToken = default) => Unsupported<Task<SyncMethodCallResult>>();
    public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource, CancellationToken cancellationToken = default) => Unsupported<Task<string>>();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private static T Unsupported<T>() => throw new NotSupportedException("This fake method is outside creation tree execution.");
}
