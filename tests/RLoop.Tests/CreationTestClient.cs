using RLoop.Core;
using System.Text.Json.Nodes;

namespace RLoop.Tests;

internal sealed class CreationTestClient : IResoniteClient, IApplyDeletionObservation, IApplySessionObservation, IApplyConnectionGuard
{
    public const string Url = "ws://localhost:47610/";
    public const string SessionId = "S-creation-test";
    public const string ConnectionGeneration = "connection-generation-a";
    public Dictionary<string, ApplyDeletionObservation> Slots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ComponentInfo> Components { get; } = new(StringComparer.Ordinal);
    public List<string> SlotReads { get; } = [];
    public List<string> ComponentReads { get; } = [];
    public List<string> TypeReads { get; } = [];
    public int MutationCalls { get; private set; }
    public int ConnectCalls { get; private set; }
    public int SessionReads { get; private set; }
    public int IdentityReads { get; private set; }
    public int GuardReads { get; private set; }
    public int GuardWriteCalls { get; private set; }
    public ComponentTypeInfo StampMetadata { get; set; } = new(
        CreationOwnershipVerifier.VariableComponentType, "FrooxEngine", null, true,
        [new("VariableName", "field", "string", "System.String", null), new("Value", "field", "string", "System.String", null)]);
    public Func<int, SessionInfo>? SessionRead { get; set; }
    public Func<int, CancellationToken, Task<SessionInfo>>? SessionAsyncRead { get; set; }
    public Func<int, ApplySessionObservation>? IdentityRead { get; set; }
    public Func<int, ApplyConnectionObservation>? GuardRead { get; set; }
    public Func<string, int, Task<ApplyDeletionObservation>>? SlotRead { get; set; }
    public Func<string, int, Task<ComponentInfo>>? ComponentRead { get; set; }
    public Func<string, Task<ComponentTypeInfo>>? TypeRead { get; set; }

    public void AddSlot(SlotInfo slot, bool childrenObserved = true, bool componentsObserved = true) =>
        Slots.Add(slot.Id, new ApplyDeletionObservation(slot, childrenObserved, componentsObserved));

    public void AddComponent(ComponentInfo component) => Components.Add(component.Id, component);

    public void ReplaceComponent(ComponentInfo component) => Components[component.Id] = component;

    public ApplySessionObservation ObserveApplySession()
    {
        var read = ++IdentityReads;
        return IdentityRead?.Invoke(read) ?? new(ApplySessionObservation.NormalizeUrl(Url), SessionId, "matched");
    }

    public ApplyConnectionObservation ObserveApplyConnection()
    {
        var read = ++GuardReads;
        return GuardRead?.Invoke(read) ?? new(true, ConnectionGeneration);
    }

    public IDisposable GuardApplyWrites(string? plannedGeneration)
    {
        GuardWriteCalls++;
        throw new InvalidOperationException("Creation ownership verification must not acquire a write guard.");
    }

    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = ++SessionReads;
        if (SessionAsyncRead is not null) return SessionAsyncRead(read, cancellationToken);
        return Task.FromResult(SessionRead?.Invoke(read) ?? new SessionInfo(Url, true, null, null, "unique-id-is-not-the-discover-id", "session-connection-generation"));
    }

    public Task<ApplyDeletionObservation> ObserveDeletionSlotAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = SlotReads.Count + 1;
        SlotReads.Add(id);
        if (SlotRead is not null) return SlotRead(id, read);
        return Slots.TryGetValue(id, out var slot)
            ? Task.FromResult(slot)
            : Task.FromException<ApplyDeletionObservation>(new RLoopException("SLOT_NOT_FOUND", $"Slot with ID '{id}' not found.", ExitCodes.NotFound));
    }

    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = ComponentReads.Count + 1;
        ComponentReads.Add(id);
        if (ComponentRead is not null) return ComponentRead(id, read);
        return Components.TryGetValue(id, out var component)
            ? Task.FromResult(component)
            : Task.FromException<ComponentInfo>(new RLoopException("COMPONENT_NOT_FOUND", $"Component '{id}' was not found.", ExitCodes.NotFound));
    }

    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TypeReads.Add(type);
        return TypeRead is null ? Task.FromResult(StampMetadata) : TypeRead(type);
    }

    private T Mutate<T>()
    {
        MutationCalls++;
        throw new InvalidOperationException("Creation ownership verification is read-only.");
    }

    private static T Unsupported<T>() => throw new NotSupportedException("This fake method is outside creation ownership verification.");

    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default)
    { ConnectCalls++; return Task.CompletedTask; }
    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default) => Unsupported<Task<SlotInfo>>();
    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) => Mutate<Task<string>>();
    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => Mutate<Task>();
    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => Mutate<Task>();
    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType, IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default) => Mutate<Task<ComponentCreateResult>>();
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => Mutate<Task>();
    public Task SetComponentMembersAsync(string componentId, string componentType, IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default) => Mutate<Task>();
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => Mutate<Task>();
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) => Unsupported<Task<IReadOnlyList<string>>>();
    public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => Unsupported<Task<TypeInfo>>();
    public Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments = null, CancellationToken cancellationToken = default) => Mutate<Task<SyncMethodCallResult>>();
    public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource, CancellationToken cancellationToken = default) => Mutate<Task<string>>();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class CreationOwnershipFixture
{
    public const string CreationId = "room";
    public const string ParentId = "parent-slot";
    public const string RootId = "created-root";
    public const string OwnerNonce = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string GenerationId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string InputHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public static CreationStamp Stamp => new(1, CreationId, OwnerNonce, GenerationId);

    public static CreationState State(CreationStamp? stamp = null) => new("creation", 4, CreationId,
        new CreationSession(ApplySessionObservation.NormalizeUrl(CreationTestClient.Url), CreationTestClient.SessionId, "matched"),
        OwnerNonce, new CreationActiveRoot(RootId, ParentId, GenerationId, (stamp ?? Stamp).Serialize(), InputHash, null));

    public static CreationTestClient Client(SlotInfo? rootOverride = null)
    {
        var client = new CreationTestClient();
        var marker = StampComponent("stamp-component", Stamp.Serialize());
        client.AddComponent(marker);
        var root = rootOverride ?? Slot(RootId, ParentId, [Summary(marker)]);
        var parent = Slot(ParentId, "world", [], [root]);
        client.AddSlot(parent);
        client.AddSlot(root);
        return client;
    }

    public static SlotInfo Slot(string id, string? parentId, IReadOnlyList<ComponentSummary>? components = null,
        IReadOnlyList<SlotInfo>? children = null, bool referenceOnly = false) =>
        new(id, id, parentId, null, null, null, true, true, null, referenceOnly,
            components ?? [], children ?? []);

    public static ComponentInfo StampComponent(string id, string? rawStamp, string variableName = CreationOwnershipVerifier.VariableName,
        string type = CreationOwnershipVerifier.VariableComponentType, bool omitValueMember = false, JsonNode? value = null) =>
        new(id, type, new Dictionary<string, MemberValue>(StringComparer.Ordinal)
        {
            ["VariableName"] = new MemberValue("field", Value: JsonValue.Create(variableName)),
            ["Value"] = omitValueMember ? null! : new MemberValue("field", Value: value ?? JsonValue.Create(rawStamp))
        });

    public static ComponentSummary Summary(ComponentInfo component) => new(component.Id, component.Type);
}

internal sealed class CreationClientWithoutDeletionObservation(CreationTestClient inner) :
    IResoniteClient, IApplySessionObservation, IApplyConnectionGuard
{
    public ApplySessionObservation ObserveApplySession() => inner.ObserveApplySession();
    public ApplyConnectionObservation ObserveApplyConnection() => inner.ObserveApplyConnection();
    public IDisposable GuardApplyWrites(string? plannedGeneration) => inner.GuardApplyWrites(plannedGeneration);
    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => inner.ConnectAsync(uri, timeout, cancellationToken);
    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) => inner.GetSessionInfoAsync(cancellationToken);
    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default) => inner.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => inner.GetComponentAsync(id, cancellationToken);
    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) => inner.CreateSlotAsync(request, cancellationToken);
    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => inner.UpdateSlotAsync(request, cancellationToken);
    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteSlotAsync(id, cancellationToken);
    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType, IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default) => inner.AddComponentAsync(slotId, componentType, fields, cancellationToken);
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => inner.SetComponentMemberAsync(componentId, member, rawValue, cancellationToken);
    public Task SetComponentMembersAsync(string componentId, string componentType, IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default) => inner.SetComponentMembersAsync(componentId, componentType, fields, cancellationToken);
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => inner.RemoveComponentAsync(componentId, cancellationToken);
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) => inner.SearchComponentTypesAsync(query, limit, cancellationToken);
    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) => inner.DescribeComponentTypeAsync(type, cancellationToken);
    public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => inner.DescribeTypeAsync(type, cancellationToken);
    public Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments = null, CancellationToken cancellationToken = default) =>
        inner.CallComponentMethodAsync(componentId, method, arguments, cancellationToken);
    public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource, CancellationToken cancellationToken = default) =>
        inner.ImportAssetAsync(asset, resolvedSource, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
