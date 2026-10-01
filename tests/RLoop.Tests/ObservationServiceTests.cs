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
    public async Task QueryCursorIsStaleWhenAProjectedValueBeforeTheCursorChanged()
    {
        SlotInfo Build(bool withComponent) => Slot("root", "Root", Enumerable.Range(0, 6)
            .Select(index => Slot($"s{index}", "Match",
                components: index == 0 && withComponent ? [Component("c", "[FrooxEngine]FrooxEngine.Grabbable")] : [])).ToArray());
        var filter = new HierarchyQueryFilter(Name: "Match");
        var page = await new ObservationService(new TreeClient(Build(false)))
            .QueryAsync("Root", "root", filter, ["component.type"], limit: 4);

        // Ids and paths before the cursor are identical, so only the projected value can reveal the change.
        var stale = await Assert.ThrowsAsync<RLoopException>(() => new ObservationService(new TreeClient(Build(true)))
            .QueryAsync("Root", "root", filter, ["component.type"], limit: 4, cursor: page.Truncation?.Continuation));
        Assert.Equal("CURSOR_STALE", stale.Code);
    }

    [Fact]
    public async Task QueryCursorCannotDetectAWorldThatDiffersOnlyAfterTheCursorPosition()
    {
        // Documents the limit of the guarantee: only the rows before the offset are checked.
        var first = Slot("root", "Root", Enumerable.Range(0, 6).Select(index => Slot($"s{index}", "Match")).ToArray());
        var laterDiffers = Slot("root", "Root", Enumerable.Range(0, 6)
            .Select(index => Slot(index < 4 ? $"s{index}" : $"other{index}", "Match")).ToArray());
        var filter = new HierarchyQueryFilter(Name: "Match");
        var page = await new ObservationService(new TreeClient(first)).QueryAsync("Root", "root", filter, [], limit: 4);

        var resumed = await new ObservationService(new TreeClient(laterDiffers))
            .QueryAsync("Root", "root", filter, [], limit: 4, cursor: page.Truncation?.Continuation);

        // No error: the continuation is served from a world that only shares the first four rows.
        Assert.Equal(["Root/Match[4]", "Root/Match[5]"], resumed.Data.Matches.Select(match => match.SlotPath).ToArray());
        Assert.Equal(["other4", "other5"], resumed.Data.Matches.Select(match => match.SlotId).ToArray());
    }

    [Fact]
    public async Task QueryCursorV1AndV2TokensAreInvalid()
    {
        var tree = Slot("root", "Root", Enumerable.Range(0, 6).Select(index => Slot($"s{index}", "Match")).ToArray());
        foreach (var token in new[] { "1\u001fabcdef\u001f4", "v2\u001fabcdef\u001f4\u001fdigest" })
        {
            var cursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token));
            var error = await Assert.ThrowsAsync<RLoopException>(() => new ObservationService(new TreeClient(tree))
                .QueryAsync("Root", "root", new HierarchyQueryFilter(Name: "Match"), [], limit: 4, cursor: cursor));
            Assert.Equal("CURSOR_INVALID", error.Code);
        }
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
            Component("c2", "[FrooxEngine]FrooxEngine.Button")]), Slot("x", "X"), Slot("y", "Y")]);
        var after = Slot("root", "Root", [Slot("a", "Alpha", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["Target"] = new("reference", TargetId: "x")
            })]), Slot("x", "X"), Slot("y", "Y")]);
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
            Slot("b", "Beta"), Slot("x", "X"), Slot("y", "Y")
        ]);
        var after = Slot("root", "Root", [
            Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = new("reference", TargetId: "y") })]),
            Slot("c", "Gamma"), Slot("x", "X"), Slot("y", "Y")
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
        // Members that were never compared are unobserved, so the diff cannot claim to be complete.
        Assert.False(diff.Complete);
        Assert.Contains(diff.Unobserved!, item => item.Reason == "member-scope-mismatch");
    }

    [Fact]
    public async Task SelectedSnapshotsWithDifferentMemberListsAreAScopeMismatchNotMemberAddition()
    {
        using var workspace = new Workspace();
        var tree = Slot("root", "Root", components: [
            Component("c1", "[FrooxEngine]FrooxEngine.Grabbable", new Dictionary<string, MemberValue>
            {
                ["A"] = new("field", Value: JsonValue.Create(1)), ["B"] = new("field", Value: JsonValue.Create(2))
            })]);
        var service = new ObservationService(new TreeClient(tree));
        await service.CreateSnapshotAsync("Root", "root", workspace.File("before.json"), "selected", ["A"]);
        await service.CreateSnapshotAsync("Root", "root", workspace.File("after.json"), "selected", ["A", "B"]);

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "member.added");
        Assert.Contains(diff.Unobserved!, item => item.Reason == "member-scope-mismatch");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task ReferenceChangingToNullIsAReferenceChangeNotAMemberRemoval()
    {
        using var workspace = new Workspace();
        SlotInfo Build(MemberValue target) => Slot("root", "Root", [
            Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue> { ["Target"] = target })]), Slot("x", "X")]);
        await new ObservationService(new TreeClient(Build(new("reference", TargetId: "x"))))
            .CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        var nulled = await new ObservationService(new TreeClient(Build(new("reference", TargetId: null))))
            .CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        // The null reference is kept as a value in the default references scope.
        Assert.Contains("Target", Assert.Single(nulled.Slots.Single(slot => slot.Path == "Root/Alpha").Components).Members.Keys);
        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        var change = Assert.Single(diff.Groups.SelectMany(group => group.Changes));

        Assert.Equal("reference.changed", change.Kind);
        Assert.Equal("Target", change.Member);
        Assert.True(diff.Complete);

        // And back: null to a target is also a reference change.
        var back = ObservationService.Diff(workspace.File("after.json"), workspace.File("before.json"));
        Assert.Equal("reference.changed", Assert.Single(back.Groups.SelectMany(group => group.Changes)).Kind);
    }

    [Fact]
    public async Task DiffDoesNotTrustRawIdsOrTheConnectionIdAsIdentity()
    {
        using var workspace = new Workspace();
        SlotInfo Build(string prefix, string externalTarget) => Slot(prefix + "root", "Root", [
            Slot(prefix + "a", "Alpha", components: [Component(prefix + "c1", "[FrooxEngine]FrooxEngine.Grabbable",
                new Dictionary<string, MemberValue>
                {
                    ["Inside"] = new("reference", TargetId: prefix + "x"),
                    ["Outside"] = new("reference", TargetId: externalTarget)
                })]),
            Slot(prefix + "x", "X")]);
        // Different raw ids everywhere, but the same connection label: nothing about the ids says same or different world.
        await new ObservationService(new TreeClient(Build("p", "ext-1"), connectionId: "1"))
            .CreateSnapshotAsync("Root", "proot", workspace.File("before.json"));
        await new ObservationService(new TreeClient(Build("q", "ext-2"), connectionId: "1"))
            .CreateSnapshotAsync("Root", "qroot", workspace.File("after.json"));

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        // The in-snapshot reference compares by structure (same path), so renamed ids are not a change.
        Assert.Empty(diff.Groups);
        Assert.DoesNotContain(diff.Unobserved!, item => item.Member == "Inside");
        // The out-of-snapshot target can only be compared by raw id, which proves nothing.
        var outside = Assert.Single(diff.Unobserved!);
        Assert.Equal("Outside", outside.Member);
        Assert.Equal("reference-identity-unproven", outside.Reason);
        Assert.False(diff.Complete);
        Assert.Contains(diff.Issues, issue => issue.Code == "SNAPSHOT_IDENTITY_UNPROVEN" && issue.Severity == "info");
        Assert.DoesNotContain(diff.Issues, issue => issue.Code == "SNAPSHOT_CONNECTION_CHANGED");

        // Identical raw ids prove nothing either: still unobserved, never unchanged.
        await new ObservationService(new TreeClient(Build("p", "ext-1"), connectionId: "2"))
            .CreateSnapshotAsync("Root", "proot", workspace.File("same-ids.json"));
        var same = ObservationService.Diff(workspace.File("before.json"), workspace.File("same-ids.json"));
        Assert.Empty(same.Groups);
        Assert.Equal("reference-identity-unproven", Assert.Single(same.Unobserved!).Reason);
        Assert.False(same.Complete);
    }

    [Fact]
    public async Task ReferenceToNullVersusUnprovenTargetIsStillAStructuralChange()
    {
        using var workspace = new Workspace();
        SlotInfo Build(string? target) => Slot("root", "Root", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable",
            new Dictionary<string, MemberValue> { ["Outside"] = new("reference", TargetId: target) })]);
        await new ObservationService(new TreeClient(Build("ext"))).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(Build(null))).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.Equal("reference.changed", Assert.Single(diff.Groups.SelectMany(group => group.Changes)).Kind);
        Assert.Contains(diff.Unobserved!, item => item.Reason == "reference-identity-unproven");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task ReferenceOnlySlotIsAnUnobservedBoundaryAndItsHiddenChildrenAreNeverRemoved()
    {
        using var workspace = new Workspace();
        var full = Slot("root", "Root", [Slot("a", "Alpha", [Slot("hidden", "Hidden")])]);
        // Alpha came back reference-only: it may have children, but the transport did not say.
        var partial = Slot("root", "Root", [Slot("a", "Alpha", referenceOnly: true)]);

        var envelope = await new ObservationService(new TreeClient(partial)).ProfileAsync("Root", "root");
        Assert.False(envelope.Complete);
        Assert.Equal("reference-only-boundary", envelope.Truncation?.Reason);
        Assert.Equal(1, envelope.Truncation?.ReferenceOnlySlots);
        Assert.Equal("Root/Alpha", Assert.Single(envelope.Truncation!.ReferenceOnlyPaths!));

        await new ObservationService(new TreeClient(full)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        var after = await new ObservationService(new TreeClient(partial)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));
        Assert.False(after.Complete);
        Assert.False(after.Slots.Single(slot => slot.Path == "Root/Alpha").ChildrenObserved);

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));

        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Alpha/Hidden" && item.Reason == "slot-presence-unobserved");
        Assert.False(diff.Complete);
    }

    [Fact]
    public async Task ExcludeUserRootsReportsReferenceOnlySlotsWhoseChildrenCouldNotBeClassified()
    {
        var tree = Slot("root", "Root", [
            Slot("u", "User Alice", components: [Component("c", "[FrooxEngine]FrooxEngine.UserRoot")]),
            Slot("a", "Alpha", referenceOnly: true)]);

        var envelope = await new ObservationService(new TreeClient(tree)).ProfileAsync("Root", "root", excludeUserRoots: true);

        Assert.False(envelope.Complete);
        Assert.Equal("user-root-excluded", envelope.Truncation?.Reason);
        Assert.Equal(1, envelope.Truncation?.ExcludedUserRoots);
        Assert.Equal(1, envelope.Truncation?.ReferenceOnlySlots);
    }

    [Fact]
    public async Task LegacySnapshotWithChildrenObservedReadsAsUnknownNotObserved()
    {
        using var workspace = new Workspace();
        var before = Slot("root", "Root", [Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable")]), Slot("b", "Beta")]);
        var after = Slot("root", "Root", [Slot("a", "Alpha", components: [Component("c1", "[FrooxEngine]FrooxEngine.Grabbable")])]);
        await new ObservationService(new TreeClient(before)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(after)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));

        // The real legacy format had childrenObserved, but lacked component/member observation flags.
        var node = JsonNode.Parse(File.ReadAllText(workspace.File("after.json")))!.AsObject();
        Assert.True(node["complete"]!.GetValue<bool>());
        foreach (var slot in node["slots"]!.AsArray().Select(item => item!.AsObject()))
        {
            Assert.True(slot["childrenObserved"]!.GetValue<bool>());
            slot.Remove("componentsObserved");
            slot.Remove("membersObserved");
            foreach (var component in slot["components"]!.AsArray().Select(item => item!.AsObject()))
                component.Remove("membersObserved");
        }
        File.WriteAllText(workspace.File("legacy.json"), node.ToJsonString());

        // Against the intact newer snapshot, Beta removal is proven; against the legacy file it is not.
        Assert.Contains(ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json")).Groups
            .SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("legacy.json"));

        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        Assert.Contains(diff.Issues, issue => issue.Code == "SNAPSHOT_LEGACY_OBSERVATION_FLAGS");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Beta" && item.Reason == "slot-presence-unobserved");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Alpha" && item.Reason == "components-not-observed");
        Assert.False(diff.Complete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacySnapshotWithUntrustedChildCoverageNeverInfersSlotAbsence(bool referenceOnly)
    {
        using var workspace = new Workspace();
        var full = Slot("root", "Root", [Slot("a", "Alpha", [Slot("hidden", "Hidden")])]);
        var partial = Slot("root", "Root", [Slot("a", "Alpha", referenceOnly: referenceOnly)]);
        await new ObservationService(new TreeClient(full)).CreateSnapshotAsync("Root", "root", workspace.File("new.json"));
        await new ObservationService(new TreeClient(partial)).CreateSnapshotAsync("Root", "root", workspace.File("partial.json"));
        var legacy = JsonNode.Parse(File.ReadAllText(workspace.File("partial.json")))!.AsObject();
        legacy["complete"] = true; // Old builds claimed child coverage even at reference-only boundaries.
        foreach (var slot in legacy["slots"]!.AsArray().Select(item => item!.AsObject()))
        {
            slot["childrenObserved"] = true;
            slot.Remove("componentsObserved");
            slot.Remove("membersObserved");
        }
        File.WriteAllText(workspace.File("legacy.json"), legacy.ToJsonString());

        var removed = ObservationService.Diff(workspace.File("new.json"), workspace.File("legacy.json"));
        var created = ObservationService.Diff(workspace.File("legacy.json"), workspace.File("new.json"));
        foreach (var diff in new[] { removed, created })
        {
            Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind is "slot.removed" or "slot.created");
            Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Alpha/Hidden" && item.Reason == "slot-presence-unobserved");
            Assert.Contains(diff.Issues, issue => issue.Code == "SNAPSHOT_LEGACY_OBSERVATION_FLAGS");
            Assert.False(diff.Complete);
        }
    }

    [Fact]
    public async Task ReferenceOnlyParentNeverProvesAbsenceEvenWithCompleteAndChildrenObservedTrue()
    {
        using var workspace = new Workspace();
        var full = Slot("root", "Root", [Slot("a", "Alpha", [Slot("hidden", "Hidden")])]);
        var partial = Slot("root", "Root", [Slot("a", "Alpha", referenceOnly: true)]);
        await new ObservationService(new TreeClient(full)).CreateSnapshotAsync("Root", "root", workspace.File("before.json"));
        await new ObservationService(new TreeClient(partial)).CreateSnapshotAsync("Root", "root", workspace.File("after.json"));
        var node = JsonNode.Parse(File.ReadAllText(workspace.File("after.json")))!.AsObject();
        node["complete"] = true;
        node["slots"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "Root/Alpha")!["childrenObserved"] = true;
        File.WriteAllText(workspace.File("after.json"), node.ToJsonString());

        var diff = ObservationService.Diff(workspace.File("before.json"), workspace.File("after.json"));
        Assert.DoesNotContain(diff.Groups.SelectMany(group => group.Changes), change => change.Kind == "slot.removed");
        Assert.Contains(diff.Unobserved!, item => item.Path == "Root/Alpha/Hidden" && item.Reason == "slot-presence-unobserved");
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
