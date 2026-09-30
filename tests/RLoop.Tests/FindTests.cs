using RLoop.Core;

namespace RLoop.Tests;

/// <summary>
/// <see cref="WorldService.FindAsync"/> against a fixed slot tree whose level-1 child
/// carries a reference-only grandchild, the shape a backend produces when children are
/// listed beyond the observed depth.
/// </summary>
public sealed class FindTests
{
    private static readonly SlotInfo Tree = new("Root", "Root", null,
        null, null, null, null, null, null, false,
        Components: [], Children:
        [
            new SlotInfo("child", "Child", "Root", null, null, null, null, null, null, false,
                Components: [new ComponentSummary("comp-1", "FrooxEngine.Grabbable")], Children:
                [
                    new SlotInfo("grand", "Grandchild", "child", null, null, null, null, null, null,
                        IsReferenceOnly: true, Components: [], Children: []),
                ]),
        ]);

    [Fact]
    public async Task DirectChildren_DoesNotMatchGrandchildrenBelowTheBoundary()
    {
        var service = new WorldService(new FixedTreeClient(Tree));

        var matches = await service.FindAsync("Grand", false, null, 8,
            options: new FindOptions(DirectChildren: true));

        Assert.Empty(matches);

        // The direct child itself still matches, by name and by component.
        var byName = await service.FindAsync("Child", true, null, 8,
            options: new FindOptions(DirectChildren: true));
        Assert.Equal("child", Assert.Single(byName).Id);
        Assert.Equal("Root/Child", byName[0].Path);
        var byComponent = await service.FindAsync(null, false, "Grabbable", 8,
            options: new FindOptions(DirectChildren: true));
        Assert.Equal("child", Assert.Single(byComponent).Id);
    }

    [Fact]
    public async Task WithoutDirectChildren_StillMatchesGrandchildStubs()
    {
        var service = new WorldService(new FixedTreeClient(Tree));

        var matches = await service.FindAsync("Grand", false, null, 8);

        var match = Assert.Single(matches);
        Assert.Equal("grand", match.Id);
        Assert.Equal("Root/Child/Grandchild", match.Path);
    }

    private sealed class FixedTreeClient(SlotInfo root) : IResoniteClient
    {
        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(id == root.Id ? root : throw new RLoopException(
                "SLOT_NOT_FOUND", id, ExitCodes.NotFound));
        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionInfo("fake://tree", true, "test", "test", "session-1"));
        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task SetComponentMemberAsync(string componentId, string member, string rawValue,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetComponentMembersAsync(string componentId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
