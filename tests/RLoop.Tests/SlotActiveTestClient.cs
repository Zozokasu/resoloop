using RLoop.Core;

namespace RLoop.Tests;

internal sealed class SlotActiveTestClient : IResoniteClient, IApplySessionObservation, IApplySendEvidence
{
    private static int nextSession;
    private readonly int sessionNumber = Interlocked.Increment(ref nextSession);
    public string Url => $"ws://slot-active-{sessionNumber}.fake/";
    private string SessionId => $"S-slot-active-{sessionNumber}";
    public Dictionary<string, SlotInfo> Slots { get; } = new(StringComparer.Ordinal)
    {
        ["Root"] = new("Root", "Root", null, null, null, null, true, true, "", false, [], [])
    };
    public List<SlotCreateRequest> Creates { get; } = [];
    public List<SlotUpdateRequest> Updates { get; } = [];
    public bool ApplySendStarted { get; private set; }
    public bool ApplyResponseReceived { get; private set; }
    public bool ApplyResponseAccepted { get; private set; }
    private int componentId;

    public void BeginApplySend() { ApplySendStarted = false; ApplyResponseReceived = false; ApplyResponseAccepted = false; }
    public ApplySessionObservation ObserveApplySession() => new(ApplySessionObservation.NormalizeUrl(Url), SessionId, "matched");
    public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionInfo(Url, true, "offline-test", "offline-test", SessionId, "generation-1"));

    public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
    {
        if (!Slots.TryGetValue(id, out var slot))
            throw new RLoopException("SLOT_NOT_FOUND", id, ExitCodes.NotFound);
        SlotInfo Observe(SlotInfo value, int remaining) => value with
        {
            Components = [],
            Children = remaining == 0 ? [] : Slots.Values.Where(child => child.ParentId == value.Id)
                .Select(child => Observe(child, remaining < 0 ? -1 : remaining - 1)).ToArray()
        };
        return Task.FromResult(Observe(slot, depth));
    }

    public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default)
    {
        ApplySendStarted = true;
        Creates.Add(request);
        var id = request.RequestedId ?? $"slot-{Creates.Count}";
        Slots[id] = new(id, request.Name, request.ParentId, request.Position, request.Rotation, request.Scale,
            request.IsActive ?? true, true, request.Tag ?? "", false, [], []);
        ApplyResponseReceived = ApplyResponseAccepted = true;
        return Task.FromResult(id);
    }

    public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ApplySendStarted = true;
        Updates.Add(request);
        var prior = Slots[request.Id];
        Slots[request.Id] = prior with
        {
            Name = request.Name ?? prior.Name,
            ParentId = request.ParentId ?? prior.ParentId,
            Position = request.Position ?? prior.Position,
            Rotation = request.Rotation ?? prior.Rotation,
            Scale = request.Scale ?? prior.Scale,
            IsActive = request.IsActive ?? prior.IsActive,
            Tag = request.Tag ?? prior.Tag
        };
        ApplyResponseReceived = ApplyResponseAccepted = true;
        return Task.CompletedTask;
    }

    public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
        IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ComponentCreateResult($"component-{Interlocked.Increment(ref componentId)}", componentType));
    public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetComponentMemberAsync(string componentId, string member, string rawValue, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SetComponentMembersAsync(string componentId, string componentType, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
