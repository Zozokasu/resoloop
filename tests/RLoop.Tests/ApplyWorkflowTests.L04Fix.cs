using RLoop.Core;
using RLoop.ResoniteLink;
using Link = ResoniteLink;

namespace RLoop.Tests;

/// <summary>
/// L04: apply deletions judged through the real adapter over hand-written raw GetSlot responses in the
/// live shape (TestResults/S3-LIVE/executor-v3b/ResoLoop_Test_DriverSwap_TwoStage_e8c680baa2bf4fe2ba1e8aa231072dcc/
/// driver-swap-raw-getslot.json): SlotData.Depth is 0 although depth 1 was requested.
/// </summary>
public sealed partial class ApplyWorkflowTests
{
    private static Link.Slot RawSlotBody(FakeResoniteClient.FakeSlot slot, bool referenceOnly) => new()
    {
        ID = slot.Id, IsReferenceOnly = referenceOnly,
        Name = referenceOnly ? null : new Link.Field_string { Value = slot.Name },
        Parent = new Link.Reference { TargetID = slot.ParentId }
    };

    /// <summary>The live response to GetSlot(Depth=1, IncludeComponentData=true) for the fake's current content.</summary>
    private static async Task<ResoniteLinkClientAdapter> LiveShapedDeletionObserverAsync(FakeResoniteClient client,
        Func<FakeResoniteClient.FakeSlot, bool>? childReturnedAsReference = null)
    {
        FakeResoniteClient.FakeSlot? Find(FakeResoniteClient.FakeSlot at, string id) =>
            at.Id == id ? at : at.Children.Select(c => Find(c, id)).FirstOrDefault(found => found is not null);
        var adapter = new ResoniteLinkClientAdapter(new ScriptedMetadataLink { Connected = false }, reflectionCache: new("off"),
            getSlotData: request =>
            {
                Assert.Equal(1, request.Depth);
                Assert.True(request.IncludeComponentData);
                var slot = Find(client.Root, request.SlotID);
                if (slot is null)
                    return Task.FromResult(new Link.SlotData { Success = false, ErrorInfo = $"Slot with ID '{request.SlotID}' not found." });
                var raw = RawSlotBody(slot, referenceOnly: false);
                raw.Components = [.. slot.Components.Select(c => new Link.Component { ID = c.Id, ComponentType = c.Type, Members = [] })];
                raw.Children = slot.Children.Count == 0 ? null :
                    [.. slot.Children.Select(c => RawSlotBody(c, childReturnedAsReference?.Invoke(c) == true))];
                return Task.FromResult(new Link.SlotData { Success = true, Depth = 0, Data = raw });
            });
        await adapter.ConnectAsync(new("ws://localhost:47610"), TimeSpan.FromSeconds(2));
        return adapter;
    }

    private static ApplyDocument WithTwoChildren(ApplyDocument doc) => doc with
    {
        Children =
        [
            new(doc.Slot! with { Key = "first", Name = "First", Parent = null }, []),
            new(doc.Slot! with { Key = "second", Name = "Second", Parent = null }, [])
        ]
    };

    [Fact]
    public async Task L04LiveShapedDepthZeroResponseLetsAnOwnedExactComponentBePrunedWithOneDelete()
    {
        var doc = WithTwoChildren(Document("l04-live-shape", """[{"key":"keep","type":"Test.Source"},{"key":"stale","type":"Test.Target"}]"""));
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        var slot = client.Root.Children[0];
        var stale = slot.Components.Single(c => c.Type == "Test.Target").Id;
        await using var adapter = await LiveShapedDeletionObserverAsync(client);
        client.DeletionObserver = adapter;
        var observed = await adapter.ObserveDeletionSlotAsync(slot.Id);
        Assert.True(observed.ChildrenObserved);
        Assert.True(observed.ComponentsObserved);
        var removed = new List<string>();
        client.AfterMutation = (kind, id) => { if (kind == "removeComponent") removed.Add(id); };
        client.ResetWriteCounts();

        var result = await service.ApplyAsync(doc with { Components = [doc.Components![0]] }, options with { Prune = true, ConfirmDeletes = true });

        Assert.Equal(1, result.ComponentsDeleted);
        Assert.Equal(1, client.Writes);
        Assert.Equal([stale], removed);
        Assert.Equal("Test.Source", Assert.Single(slot.Components).Type);
        Assert.Equal(2, slot.Children.Count);
        Assert.Empty(ApplyStateStore.Load(options.StateFile!, "l04-live-shape").Pending);
    }

    [Fact]
    public async Task L04OneReferenceOnlyChildStopsComponentPruneBeforeSendingAndKeepsContent()
    {
        var doc = WithTwoChildren(Document("l04-reference-child", """[{"key":"stale","type":"Test.Target"}]"""));
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        var slot = client.Root.Children[0];
        await using var adapter = await LiveShapedDeletionObserverAsync(client, child => child.Name == "Second");
        client.DeletionObserver = adapter;
        var observed = await adapter.ObserveDeletionSlotAsync(slot.Id);
        Assert.False(observed.ChildrenObserved);
        Assert.True(observed.ComponentsObserved);
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            service.ApplyAsync(doc with { Components = [] }, options with { Prune = true, ConfirmDeletes = true }));

        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal("deletionObservationIncomplete", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Single(slot.Components);
        Assert.Equal(2, slot.Children.Count);
        Assert.Empty(ApplyStateStore.Load(options.StateFile!, "l04-reference-child").Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task L04LiveShapedSubtreePruneDeletesOwnedSlotOnceAndStopsOnAReferenceOnlyGrandchild(bool grandchildReturnedAsReference)
    {
        var doc = Document("l04-subtree", "[]");
        doc = doc with { Children = [new(doc.Slot! with { Key = "child", Name = "Child", Parent = null },
            [new("Test.Target", null, Key: "target")], [new(doc.Slot! with { Key = "grandchild", Name = "Grandchild", Parent = null }, [])])] };
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(doc, options);
        var slot = client.Root.Children[0];
        await using var adapter = await LiveShapedDeletionObserverAsync(client,
            child => grandchildReturnedAsReference && child.Name == "Grandchild");
        client.DeletionObserver = adapter;
        client.ResetWriteCounts();

        if (grandchildReturnedAsReference)
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(doc with { Children = [] }, options));
            Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
            Assert.Equal("deletionObservationIncomplete", error.Context["reason"]);
            Assert.Equal(0, client.Writes);
            Assert.Single(Assert.Single(slot.Children).Children);
        }
        else
        {
            var result = await service.ApplyAsync(doc with { Children = [] }, options);
            Assert.Equal(1, result.SlotsDeleted);
            Assert.Equal(1, client.Writes);
            Assert.Empty(slot.Children);
        }
    }

    [Theory]
    [InlineData("child")]
    [InlineData("component")]
    public async Task L04LiveShapedCoverageStillRejectsUnmanagedContentInThePrunedSubtree(string unmanaged)
    {
        var doc = Document("l04-unmanaged", "[]");
        doc = doc with { Children = [new(doc.Slot! with { Key = "child", Name = "Child", Parent = null }, [])] };
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(doc, options);
        var child = client.Root.Children[0].Children[0];
        if (unmanaged == "child") await client.CreateSlotAsync(new(child.Id, "UserContent"));
        else await client.AddComponentAsync(child.Id, "Test.Target", new Dictionary<string, string>());
        await using var adapter = await LiveShapedDeletionObserverAsync(client);
        client.DeletionObserver = adapter;
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(doc with { Children = [] }, options));

        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal("unmanagedDeletionContent", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Single(client.Root.Children[0].Children);
    }
}
