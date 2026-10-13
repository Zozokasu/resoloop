using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

internal sealed class CreationLegacyTestClient : IResoniteClient, IApplySessionObservation, IApplySendEvidence
{
    private readonly SlotActiveTestClient inner = new();
    private readonly Dictionary<string, CreationLegacyComponentState> components = new(StringComparer.Ordinal);

    public string Url => inner.Url;
    public List<SlotCreateRequest> Creates => inner.Creates;
    public List<CreationLegacyAddedComponent> AddedComponents { get; } = [];
    public bool ApplySendStarted => inner.ApplySendStarted;
    public bool ApplyResponseReceived => inner.ApplyResponseReceived;
    public bool ApplyResponseAccepted => inner.ApplyResponseAccepted;

    public void BeginApplySend() => inner.BeginApplySend();
    public ApplySessionObservation ObserveApplySession() => inner.ObserveApplySession();
    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner.ConnectAsync(uri, timeout, cancellationToken);
    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
        inner.GetSessionInfoAsync(cancellationToken);
    public async Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData,
        CancellationToken cancellationToken = default)
    {
        var slot = await inner.GetSlotAsync(id, depth, includeComponentData, cancellationToken);
        SlotInfo AddComponents(SlotInfo current, int remaining)
        {
            var summaries = components.Values.Where(component => component.SlotId == current.Id)
                .Select(component => new ComponentSummary(component.Id, component.Type,
                    includeComponentData ? ToMembers(component.Fields) : null));
            var children = remaining == 0 ? current.Children : current.Children
                .Select(child => AddComponents(child, remaining < 0 ? -1 : remaining - 1)).ToArray();
            return current with { Components = current.Components.Concat(summaries).ToArray(), Children = children };
        }
        return AddComponents(slot, depth);
    }
    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var component = components.GetValueOrDefault(id);
        return component is null
            ? inner.GetComponentAsync(id, cancellationToken)
            : Task.FromResult(new ComponentInfo(component.Id, component.Type, ToMembers(component.Fields)));
    }
    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) =>
        inner.CreateSlotAsync(request, cancellationToken);
    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateSlotAsync(request, cancellationToken);
    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) =>
        inner.DeleteSlotAsync(id, cancellationToken);
    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        return AddComponentAndRecordAsync(slotId, componentType, fields, cancellationToken);
    }
    private async Task<ComponentCreateResult> AddComponentAndRecordAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var result = await inner.AddComponentAsync(slotId, componentType, fields, cancellationToken);
        var requestedFields = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        AddedComponents.Add(new CreationLegacyAddedComponent(result.Id, slotId, result.Type, requestedFields));
        var currentFields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.Type == GeneratedContentMetadata.ComponentType)
            currentFields[GeneratedContentMetadata.SourceMember] = string.Empty;
        foreach (var field in fields) currentFields[field.Key] = field.Value;
        components.Add(result.Id, new CreationLegacyComponentState(result.Id, slotId, result.Type, currentFields));
        return result;
    }
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var component = components.GetValueOrDefault(componentId);
        if (component is null) return inner.SetComponentMemberAsync(componentId, member, rawValue, cancellationToken);
        component.Fields[member] = rawValue;
        return Task.CompletedTask;
    }
    public Task SetComponentMembersAsync(string componentId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var component = components.GetValueOrDefault(componentId);
        if (component is null) return inner.SetComponentMembersAsync(componentId, componentType, fields, cancellationToken);
        foreach (var field in fields) component.Fields[field.Key] = field.Value;
        return Task.CompletedTask;
    }
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (components.Remove(componentId)) return Task.CompletedTask;
        return inner.RemoveComponentAsync(componentId, cancellationToken);
    }
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
        CancellationToken cancellationToken = default) => inner.SearchComponentTypesAsync(query, limit, cancellationToken);
    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type != GeneratedContentMetadata.ComponentType) return inner.DescribeComponentTypeAsync(type, cancellationToken);
        return Task.FromResult(new ComponentTypeInfo(type, "FrooxEngine", null, false,
            [new MemberDefinitionInfo(GeneratedContentMetadata.SourceMember, "field", null, "string", null)]));
    }
    public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) =>
        inner.DescribeTypeAsync(type, cancellationToken);
    public Task ValidateComponentMemberAsync(string componentType, string member, string rawValue,
        CancellationToken cancellationToken = default) =>
        ((IResoniteClient)inner).ValidateComponentMemberAsync(componentType, member, rawValue, cancellationToken);
    public Task<SyncMethodCallResult> CallComponentMethodAsync(string componentId, string method,
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? arguments = null,
        CancellationToken cancellationToken = default) =>
        ((IResoniteClient)inner).CallComponentMethodAsync(componentId, method, arguments, cancellationToken);
    public Task<string> ImportAssetAsync(ApplyAssetSpec asset, string resolvedSource,
        CancellationToken cancellationToken = default) =>
        ((IResoniteClient)inner).ImportAssetAsync(asset, resolvedSource, cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static IReadOnlyDictionary<string, MemberValue> ToMembers(IReadOnlyDictionary<string, string> fields) =>
        fields.ToDictionary(field => field.Key,
            field => new MemberValue("field", Value: JsonValue.Create(field.Value)), StringComparer.Ordinal);
}

internal sealed record CreationLegacyAddedComponent(string Id, string SlotId, string Type,
    IReadOnlyDictionary<string, string> Fields);

internal sealed record CreationLegacyComponentState(string Id, string SlotId, string Type,
    Dictionary<string, string> Fields);
