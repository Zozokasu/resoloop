using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed class ObservationServiceTests
{
    [Fact]
    public async Task ProfileCountsDepthBreadthAndComponentTypesWithoutDumpingSlots()
    {
        var tree = Slot("root", "Root", [
            Slot("a", "Alpha", [Slot("a1", "Leaf"), Slot("a2", "Leaf"), Slot("a3", "Leaf")]),
            Slot("b", "Beta", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable")])
        ]);
        var profile = (await new ObservationService(new TreeClient(tree))
            .ProfileAsync("Root", "root", maxDepth: 8, maxSlots: 100)).Data;

        Assert.Equal(6, profile.TotalSlots);
        Assert.Equal(1, profile.TotalComponents);
        Assert.Equal(2, profile.MaxObservedDepth);
        Assert.Equal(3, profile.MaxSiblingCount);
        Assert.Equal("Root/Alpha", profile.MaxSiblingParentPath);
        Assert.Equal(5, profile.EmptySlots);
        Assert.Equal("FrooxEngine.Grabbable", Assert.Single(
            (await new ObservationService(new TreeClient(tree)).ProfileAsync("Root", "root", groupBy: "component-type")).Data.Groups).Key);
    }

    [Fact]
    public async Task ProfileStopsAtSlotBudgetAndNeverReportsCompleteWhenTruncated()
    {
        var wide = Slot("root", "Root", Enumerable.Range(0, 50).Select(index => Slot($"s{index}", $"Child{index}")).ToArray());
        var envelope = await new ObservationService(new TreeClient(wide)).ProfileAsync("Root", "root", maxSlots: 10);

        Assert.False(envelope.Complete);
        Assert.Equal("max-slots", envelope.Truncation?.Reason);
        Assert.Equal(10, envelope.Data.TotalSlots);
    }

    [Fact]
    public async Task ObservationAtTheDepthBoundaryIsReportedIncompleteBecauseAbsentChildrenCannotBeProven()
    {
        var envelope = await new ObservationService(new TreeClient(Slot("root", "Root", [Slot("a", "Alpha")])))
            .ProfileAsync("Root", "root", maxDepth: 1);

        Assert.False(envelope.Complete);
        Assert.Equal("depth-boundary", envelope.Truncation?.Reason);
    }

    [Fact]
    public async Task ShallowTreeWithinBudgetReportsComplete()
    {
        var envelope = await new ObservationService(new TreeClient(Slot("root", "Root", [Slot("a", "Alpha")])))
            .ProfileAsync("Root", "root", maxDepth: 8, maxSlots: 100);

        Assert.True(envelope.Complete);
        Assert.Null(envelope.Truncation);
    }

    [Fact]
    public async Task QueryFiltersInCoreAndProjectsOnlyRequestedFields()
    {
        var tree = Slot("root", "Root", [
            Slot("a", "Panel", components: [Component("c1", "[FrooxEngine]FrooxEngine.UIX.Image")]),
            Slot("b", "Other", components: [Component("c2", "[FrooxEngine]FrooxEngine.Grabbable")])
        ]);
        var envelope = await new ObservationService(new TreeClient(tree)).QueryAsync("Root", "root",
            new HierarchyQueryFilter(ComponentType: "UIX.Image"), ["slot.path", "component.type"]);

        var match = Assert.Single(envelope.Data.Matches);
        Assert.Equal("Root/Panel", match.SlotPath);
        Assert.Null(match.SlotName);
        Assert.Equal("FrooxEngine.UIX.Image", Assert.Single(match.Components!).Type);
        Assert.Null(Assert.Single(match.Components!).Id);
    }

    [Fact]
    public async Task QueryLimitBoundsRowsWhileMaxSlotsBoundsTraversalSeparately()
    {
        var tree = Slot("root", "Root", Enumerable.Range(0, 30).Select(index => Slot($"s{index}", "Match")).ToArray());
        var envelope = await new ObservationService(new TreeClient(tree)).QueryAsync("Root", "root",
            new HierarchyQueryFilter(Name: "Match"), [], limit: 5, maxSlots: 20);

        Assert.Equal(5, envelope.Data.Returned);
        Assert.Equal(19, envelope.Data.Matched);
        Assert.Equal(20, envelope.Data.Traversed);
        Assert.False(envelope.Complete);
    }

    [Fact]
    public async Task QueryCursorPagesForwardAndIsRejectedForAnotherQueryOrConnection()
    {
        var tree = Slot("root", "Root", Enumerable.Range(0, 6).Select(index => Slot($"s{index}", "Match")).ToArray());
        var service = new ObservationService(new TreeClient(tree));
        var first = await service.QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4);

        Assert.Equal(4, first.Data.Returned);
        var cursor = first.Truncation?.Continuation;
        Assert.NotNull(cursor);

        var second = await service.QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4, cursor: cursor);
        Assert.Equal(2, second.Data.Returned);
        Assert.True(second.Complete);

        var differentQuery = await Assert.ThrowsAsync<RLoopException>(() => service.QueryAsync("Root", "root",
            new HierarchyQueryFilter(Name: "Other"), [], limit: 4, cursor: cursor));
        Assert.Equal("CURSOR_QUERY_MISMATCH", differentQuery.Code);

        // A reconnect is not rejected on the connection label; identical rows before the cursor revalidate it.
        var reconnected = new ObservationService(new TreeClient(tree, connectionId: "conn-2"));
        var resumed = await reconnected.QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4, cursor: cursor);
        Assert.Equal(2, resumed.Data.Returned);
    }

    [Fact]
    public async Task QueryCursorIsStaleWhenOnlyTheSequentialSessionIdMatchesAnotherWorld()
    {
        var first = Slot("root", "Root", Enumerable.Range(0, 6).Select(index => Slot($"a{index}", "Match")).ToArray());
        var otherWorld = Slot("root", "Root", Enumerable.Range(0, 6).Select(index => Slot($"b{index}", "Match")).ToArray());
        var page = await new ObservationService(new TreeClient(first, connectionId: "1"))
            .QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4);

        // Same UniqueSessionId ("1") by coincidence, different rows: the cursor must not be honored.
        var error = await Assert.ThrowsAsync<RLoopException>(() => new ObservationService(new TreeClient(otherWorld, connectionId: "1"))
            .QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4, cursor: page.Truncation?.Continuation));
        Assert.Equal("CURSOR_STALE", error.Code);

        var legacy = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("1\u001fabcdef\u001f4"));
        var old = await Assert.ThrowsAsync<RLoopException>(() => new ObservationService(new TreeClient(first, connectionId: "1"))
            .QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4, cursor: legacy));
        Assert.Equal("CURSOR_INVALID", old.Code);
    }

    [Fact]
    public async Task UserRootSlotsAreObservedByDefaultAndExcludedOnlyWhenRequestedAsUnobserved()
    {
        var tree = Slot("root", "Root", [
            Slot("u", "User Alice", [Slot("h", "Head")], [Component("c", "[FrooxEngine]FrooxEngine.UserRoot")]),
            Slot("w", "World")
        ]);
        var service = new ObservationService(new TreeClient(tree));

        var included = await service.ProfileAsync("Root", "root");
        Assert.True(included.Complete);
        Assert.Equal(4, included.Data.TotalSlots);

        var excluded = await service.ProfileAsync("Root", "root", excludeUserRoots: true);
        Assert.False(excluded.Complete);
        Assert.Equal("user-root-excluded", excluded.Truncation?.Reason);
        Assert.Equal(1, excluded.Truncation?.ExcludedUserRoots);
        Assert.Equal("Root/User Alice", Assert.Single(excluded.Truncation!.ExcludedPaths!));
        Assert.Equal(2, excluded.Data.TotalSlots);

        var query = await service.QueryAsync("Root", "root", new HierarchyQueryFilter(ExcludeUserRoots: true), []);
        Assert.False(query.Complete);
        Assert.DoesNotContain(query.Data.Matches, match => match.SlotPath.StartsWith("Root/User Alice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExcludedUserRootIsNeverReportedRemovedByDiff()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", [
            Slot("u", "User Alice", components: [Component("c", "[FrooxEngine]FrooxEngine.UserRoot")]), Slot("w", "World")]);
        await new ObservationService(new TreeClient(tree)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        var after = await new ObservationService(new TreeClient(tree))
            .CreateSnapshotAsync("Root", "root", workspace.File("after.json"), excludeUserRoots: true);

        Assert.False(after.Complete);
        Assert.False(after.Slots.Single(slot => slot.Path == "Root").ChildrenObserved);
        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/User Alice" && item.Reason == "slot-presence-unobserved");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task UnobservedComponentListOrMembersAreNotReportedAsRemoved()
    {
        using var workspace = new Workspace();
        var members = new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "x") };
        var full = Slot("root", "Root", [
            Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", members)]),
            Slot("b", "Beta", components: [Component("c2", "[FrooxEngine]FrooxEngine.Grabbable", members)])]);
        // Alpha came back reference-only (no component list); the Beta component came back without member data.
        var partial = Slot("root", "Root", [
            Slot("a", "Alpha", referenceOnly: true),
            Slot("b", "Beta", components: [new ComponentSummary("c2", "[FrooxEngine]FrooxEngine.Grabbable", null)])]);
        await new ObservationService(new TreeClient(full)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(partial)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        var kinds = diff.Groups.SelectMany(group => group.Changes).Select(change => change.Kind).ToArray();

        Assert.DoesNotContain("component.removed", kinds);
        Assert.DoesNotContain("member.removed", kinds);
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Alpha" && item.Reason == "components-not-observed");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Beta" && item.ComponentType == "FrooxEngine.Grabbable" && item.Reason == "members-not-observed");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task FullyObservedNewerSnapshotStillReportsComponentAndMemberRemoval()
    {
        using var workspace = new Workspace();
        var before = Slot("root", "Root", [Slot("a", "Alpha", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["Target"] = new("reference", TargetId: "x"), ["Other"] = new("reference", TargetId: "y")
            }),
            Component("c2", "[FrooxEngine]FrooxEngine.Button")])]);
        var after = Slot("root", "Root", [Slot("a", "Alpha", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["Target"] = new("reference", TargetId: "x")
            })])]);
        await new ObservationService(new TreeClient(before)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(after)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        var kinds = diff.Groups.SelectMany(group => group.Changes).Select(change => change.Kind).ToArray();

        Assert.Contains("component.removed", kinds);
        Assert.Contains("member.removed", kinds);
        Assert.True(diff.Complete);
        Assert.Equal(0, diff.UnobservedCount);
    }

    [Fact]
    public async Task UnreadableMemberIsUnknownAndNeverAValueChangeOrRemoval()
    {
        using var workspace = new Workspace();
        SlotInfo Build(MemberValue? value) => Slot("root", "Root", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
            new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "x"), ["Hidden"] = value! })]);
        await new ObservationService(new TreeClient(Build(new("reference", TargetId: "z"))))
            .CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        var after = await new ObservationService(new TreeClient(Build(null)))
            .CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        Assert.Equal(["Hidden"], Assert.Single(Assert.Single(after.Slots).Components).UnreadableMembers);
        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.Empty(diff.Groups);
        Assert.Contains(diff.Unobserved!, item => item.Member == "Hidden" && item.Reason == "member-unreadable");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task QueryRejectsUnsupportedProjectionField()
    {
        var error = await Assert.ThrowsAsync<RLoopException>(() => new ObservationService(new TreeClient(Slot("root", "Root")))
            .QueryAsync("Root", "root", new HierarchyQueryFilter(), ["slot.transform"]));

        Assert.Equal("INVALID_OPTION", error.Code);
        Assert.Equal(ExitCodes.InvalidArguments, error.ExitCode);
    }

    [Fact]
    public async Task TenThousandSlotFixtureRespectsOutputLimits()
    {
        var wide = Slot("root", "Root", Enumerable.Range(0, 10000).Select(index => Slot($"s{index}", $"Child{index}")).ToArray());
        var envelope = await new ObservationService(new TreeClient(wide)).QueryAsync("Root", "root",
            new HierarchyQueryFilter(Name: "Child"), [], limit: 50, maxSlots: 1000);

        Assert.Equal(50, envelope.Data.Matches.Count);
        Assert.Equal(1000, envelope.Data.Traversed);
        Assert.False(envelope.Complete);
    }

    [Fact]
    public async Task SnapshotNormalizesTypesAndRecordsExcludedMembers()
    {
        using var workspace = new Workspace();
        var slot = Slot("root", "Root", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["Target"] = new("reference", TargetId: "other", TargetType: "FrooxEngine.Slot"),
                ["Scale"] = new("field", Value: JsonValue.Create(1.5))
            })]);
        var document = await new ObservationService(new TreeClient(slot))
            .CreateSnapshotAsync("Root", "root", workspace.File("before.json"));

        var component = Assert.Single(Assert.Single(document.Slots).Components);
        Assert.Equal("FrooxEngine.Grabbable", component.Type);
        Assert.Equal("other", component.Members["Target"]!["targetId"]!.GetValue<string>());
        Assert.DoesNotContain("Scale", component.Members.Keys);
        Assert.Contains("Scale", document.ExcludedMembers);
    }

    [Fact]
    public async Task DiffReportsStructuralAndReferenceChangesBetweenSnapshots()
    {
        using var workspace = new Workspace();
        var before = Slot("root", "Root", [
            Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "x") })]),
            Slot("b", "Beta")
        ]);
        var after = Slot("root", "Root", [
            Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "y") })]),
            Slot("c", "Gamma")
        ]);
        await new ObservationService(new TreeClient(before)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(after)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        var kinds = diff.Groups.SelectMany(group => group.Changes).Select(change => change.Kind).ToArray();

        Assert.True(diff.Complete);
        Assert.Contains("slot.created", kinds);
        Assert.Contains("slot.removed", kinds);
        Assert.Contains("reference.changed", kinds);
    }

    [Fact]
    public async Task PartialSnapshotNeverReportsRemovalForChildrenItNeverObserved()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", [Slot("a", "Alpha", [Slot("b", "Beta"), Slot("c", "Gamma")])]);
        await new ObservationService(new TreeClient(tree)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        // Budget stops before Alpha's children, so their absence is unobserved rather than removed.
        var partial = await new ObservationService(new TreeClient(tree))
            .CreateSnapshotAsync("Root", "root", workspace.File("after.json"), maxSlots: 2);

        Assert.False(partial.Complete);
        Assert.False(partial.Slots.Single(slot => slot.Path == "Root/Alpha").ChildrenObserved);

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        Assert.Contains(diff.Issues, issue => issue.Code == "SNAPSHOT_PARTIAL");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task DiffOfIdenticalObservationsReportsNoChanges()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", [Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable")])]);
        await new ObservationService(new TreeClient(tree)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(tree)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        Assert.Equal(0, ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json")).Changes);
    }

    [Fact]
    public async Task QueryByReferenceTargetObservesMemberDataEvenWithoutAMemberProjection()
    {
        var tree = Slot("root", "Root", [
            Slot("a", "Holder", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "wanted") })]),
            Slot("b", "Unrelated", components: [Component("c2", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "other") })])
        ]);
        var envelope = await new ObservationService(new TreeClient(tree)).QueryAsync("Root", "root",
            new HierarchyQueryFilter(ReferenceToId: "wanted"), []);

        Assert.Equal("Root/Holder", Assert.Single(envelope.Data.Matches).SlotPath);
    }

    [Fact]
    public async Task SameNamedSiblingsStayDistinctAcrossSnapshotsInsteadOfCollidingOnPath()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", [
            Slot("a", "Item", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable")]),
            Slot("b", "Item"),
            Slot("c", "Item")
        ]);
        var document = await new ObservationService(new TreeClient(tree))
            .CreateSnapshotAsync("Root", "root", workspace.File("before.json"));

        Assert.Equal(["Root", "Root/Item", "Root/Item[1]", "Root/Item[2]"], document.Slots.Select(slot => slot.Path).Order().ToArray());

        await new ObservationService(new TreeClient(tree)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));
        Assert.Equal(0, ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json")).Changes);
    }

    [Fact]
    public async Task SlotNamesContainingSeparatorsDoNotForgeAPathBoundary()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", [Slot("a", "A/B"), Slot("b", "A")]);
        var document = await new ObservationService(new TreeClient(tree))
            .CreateSnapshotAsync("Root", "root", workspace.File("before.json"));

        Assert.Contains("Root/A%2FB", document.Slots.Select(slot => slot.Path));
        Assert.Contains("Root/A", document.Slots.Select(slot => slot.Path));
    }

    [Fact]
    public async Task DiffWarnsAndSkipsMemberComparisonWhenSnapshotsCapturedDifferentScopes()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["Scale"] = new("field", Value: JsonValue.Create(1.5))
            })]);
        var service = new ObservationService(new TreeClient(tree));
        await service.CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await service.CreateSnapshotAsync("Root", "root", workspace.File("after.json"), memberScope: "all");

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.Contains(diff.Issues, issue => issue.Code == "SNAPSHOT_MEMBER_SCOPE_MISMATCH");
        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes),
            change => change.Kind.StartsWith("member.", StringComparison.Ordinal));
    }

    [Fact]
    public void DiffRejectsMissingSnapshotFile()
    {
        using var workspace = new Workspace();
        var error = Assert.Throws<RLoopException>(() => ObservationService.Diff(workspace.File("missing.json"), workspace.File("absent.json")));

        Assert.Equal("SNAPSHOT_NOT_FOUND", error.Code);
        Assert.Equal(ExitCodes.NotFound, error.ExitCode);
    }

    private static SlotInfo Slot(string id, string name, IReadOnlyList<SlotInfo>? children = null,
        IReadOnlyList<ComponentSummary>? components = null, bool referenceOnly = false) =>
        new(id, name, null, null, null, null, null, null, null, referenceOnly, components ?? [], children ?? [],
            Members: referenceOnly ? null : new Dictionary<string, MemberValue>());

    private static ComponentSummary Component(string id, string type, IReadOnlyDictionary<string, MemberValue>? members = null) =>
        new(id, type, members ?? new Dictionary<string, MemberValue>());

    private sealed class Workspace : IDisposable
    {
        private readonly string _directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "resoloop-observation-" + Guid.NewGuid().ToString("N"))).FullName;

        public string File(string name) => Path.Combine(_directory, name);

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
        }
    }

    /// <summary>Serves one in-memory tree. Every mutating member throws, so a read-only path cannot change the world.</summary>
    private sealed class TreeClient(SlotInfo root, string connectionId = "conn-1") : IResoniteClient, IResoniteClientDiagnostics
    {
        private int _requests;

        public Task<SessionInfo> GetSessionInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionInfo("ws://offline", true, "2026.1.1.0", "1.0.0", connectionId));

        public Task<SlotInfo> GetSlotAsync(string id, int depth, bool includeComponentData, CancellationToken cancellationToken = default)
        {
            _requests++;
            var found = Find(root, id) ?? throw new RLoopException("SLOT_NOT_FOUND", id, ExitCodes.NotFound);
            return Task.FromResult(Trim(found, depth, includeComponentData));
        }

        private static SlotInfo? Find(SlotInfo slot, string id) =>
            slot.Id == id ? slot : slot.Children.Select(child => Find(child, id)).FirstOrDefault(match => match is not null);

        private static SlotInfo Trim(SlotInfo slot, int depth, bool includeComponentData) => slot with
        {
            Components = includeComponentData ? slot.Components : slot.Components.Select(c => c with { Members = null }).ToArray(),
            Members = includeComponentData ? slot.Members : null,
            Children = depth == 0 ? [] : slot.Children.Select(child => Trim(child, depth - 1, includeComponentData)).ToArray()
        };

        public void ResetMetrics() => _requests = 0;
        public ClientMetrics SnapshotMetrics() => new(_requests, 0, 0, []);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task ConnectAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ComponentInfo> GetComponentAsync(string id, CancellationToken cancellationToken = default) => throw Readonly();
        public Task<string> CreateSlotAsync(SlotCreateRequest request, CancellationToken cancellationToken = default) => throw Readonly();
        public Task UpdateSlotAsync(SlotUpdateRequest request, CancellationToken cancellationToken = default) => throw Readonly();
        public Task DeleteSlotAsync(string id, CancellationToken cancellationToken = default) => throw Readonly();
        public Task<ComponentCreateResult> AddComponentAsync(string slotId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => throw Readonly();
        public Task SetComponentMemberAsync(string componentId, string member, string rawValue,
            CancellationToken cancellationToken = default) => throw Readonly();
        public Task SetComponentMembersAsync(string componentId, string componentType,
            IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default) => throw Readonly();
        public Task RemoveComponentAsync(string componentId, CancellationToken cancellationToken = default) => throw Readonly();
        public Task<IReadOnlyList<string>> SearchComponentTypesAsync(string query, int limit,
            CancellationToken cancellationToken = default) => throw Readonly();
        public Task<ComponentTypeInfo> DescribeComponentTypeAsync(string type, CancellationToken cancellationToken = default) => throw Readonly();
        public Task<TypeInfo> DescribeTypeAsync(string type, CancellationToken cancellationToken = default) => throw Readonly();

        private static NotSupportedException Readonly() => new("Observation must not call this member.");
    }
}
