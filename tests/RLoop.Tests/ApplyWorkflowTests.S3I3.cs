using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Cli;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task S3TwoProjectsShareSessionLockAndSendNothingOnContention()
    {
        var first = Document("project-one", "[]");
        var second = Document("project-two", "[]", "Second");
        var client = new FakeResoniteClient();
        var other = new FakeResoniteClient();
        var directory = Path.Combine(_root, "locks");
        SessionLockTestIsolation.Share(client, directory);
        SessionLockTestIsolation.Share(other, directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ApplyOptions(Path.Combine(_root, "one.state.json"));
        // Hold the first apply inside validation, with both locks acquired.
        client.OnDescribe = () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        first = first with { Components = [new("Test.Target", null, Key: "c")] };
        client.RegisterDefinitions(first);
        var running = Task.Run(() => new WorldService(client).ApplyAsync(first, options));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(other).ApplyAsync(second, new(Path.Combine(_root, "two.state.json"))));
            Assert.Equal("APPLY_SESSION_BUSY", e.Code);
            Assert.Equal(7, e.ExitCode);
            Assert.Equal(0, other.Writes);
        }
        finally { release.TrySetResult(); }
        await running;
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("invalid")]
    [InlineData("missing")]
    [InlineData("legacy")]
    [InlineData("nullOwnership")]
    public async Task S3PreviousStateBlocksAnotherProjectAndDirectWrites(string fault)
    {
        var doc = Document("first", "[]");
        var client = new FakeResoniteClient { LoseNextSlotCreateResponse = true };
        var directory = Path.Combine(_root, "locks");
        SessionLockTestIsolation.Share(client, directory);
        var path = Path.Combine(_root, "first.state.json");
        await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(doc, new(path)));
        if (fault == "invalid") File.WriteAllText(path, "{");
        if (fault == "missing") File.Delete(path);
        if (fault == "nullOwnership") File.WriteAllText(path, "{\"schemaVersion\":3,\"ownershipKey\":null}");
        if (fault == "legacy")
        {
            var state = new ApplyState { OwnershipKey = "first", Slots = new() { ["root"] = new("", "Root/Managed") } };
            ApplyStateStore.Save(path, state);
        }
        var other = new FakeResoniteClient();
        SessionLockTestIsolation.Share(other, directory);
        var desired = Document("second", "[]", "Second");
        var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(other).ApplyAsync(desired, new(Path.Combine(_root, "second.state.json"))));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal(path, e.Context["stateFile"]);
        Assert.Equal(fault is "pending" or "legacy" ? "previousStatePending" : "previousStateUnreadable", e.Context["reason"]);
        Assert.Equal(0, other.Writes);
        await Assert.ThrowsAsync<RLoopException>(() => SessionWriteLock.AcquireAsync(other, null, CancellationToken.None));
        Assert.Equal(0, other.Writes);
    }

    [Fact]
    public async Task S3LocationSaveFailureSendsNothingAndCreatesNoPending()
    {
        var doc = Document("location", "[]");
        var client = new FakeResoniteClient();
        var state = Path.Combine(_root, "state.json");
        SessionWriteLock.RecordFault.Value = _ => throw new IOException("location failure");
        try
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(doc, new(state)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", e.Code);
            Assert.Equal(0, client.Writes);
            Assert.False(File.Exists(state));
        }
        finally { SessionWriteLock.RecordFault.Value = null; }
    }

    [Fact]
    public async Task S3SessionUrlChangeStopsBeforeWritingToAnUnlockedDestination()
    {
        var doc = Document("url-change", """[{"key":"c","type":"Test.Target"}]""");
        var client = new FakeResoniteClient(doc);
        SessionWriteLock.RecordFault.Value = _ => client.SessionUrl = "ws://other/";
        try
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(doc, new(Path.Combine(_root, "state.json"))));
            Assert.Equal("APPLY_PRECONDITION_FAILED", e.Code);
            Assert.Equal("sessionUrlChanged", e.Context["reason"]);
            Assert.Equal(0, client.Writes);
        }
        finally { SessionWriteLock.RecordFault.Value = null; }
    }

    [Fact]
    public async Task S3FrozenPipeBackendKeepsItsExistingDeletionConditions()
    {
        var doc = Document("pipe-delete", """[{"key":"c","type":"Test.Target"}]""");
        var client = new FakeResoniteClient(doc) { SessionUrl = "pipe:///test" };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        client.DeletionComponentsObserved = false;
        client.ResetWriteCounts();
        var result = await service.ApplyAsync(doc with { Components = [] }, options with { Prune = true, ConfirmDeletes = true });
        Assert.Equal(1, result.ComponentsDeleted);
        Assert.Equal(1, client.Writes);
    }

    [Theory]
    [InlineData("status", false)]
    [InlineData("slot", true)]
    [InlineData("component", true)]
    [InlineData("probe", true)]
    public async Task S3ReadOnlyCommandsSkipLockAndDirectWritesAndProbesParticipate(string command, bool blocked)
    {
        var client = new FakeResoniteClient();
        var directory = Path.Combine(_root, "locks");
        SessionLockTestIsolation.Share(client, directory);
        var slot = await client.CreateSlotAsync(new("Root", "Direct"));
        var component = await client.AddComponentAsync(slot, "Test.Target", new Dictionary<string, string>());
        client.ResetWriteCounts();
        using var holder = SessionWriteLock.Acquire("ws://fake/", directory);
        if (command == "probe")
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).TestAsync(Document("probes", "[]"), allowProbe: true));
            Assert.Equal("APPLY_SESSION_BUSY", e.Code);
        }
        else
        {
            var args = command == "status" ? new[] { "status" } : command == "slot" ? new[] { "slot", "delete", slot, "--yes" } : new[] { "component", "remove", component.Id, "--yes" };
            var exit = await Program.RunAsync([.. args, "--url", "ws://fake", "--json"], _ => Task.FromResult<IResoniteClient>(client));
            Assert.Equal(blocked ? 7 : 0, exit);
        }
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3ProjectContentionReleasesSessionLockWithoutSending()
    {
        var client = new FakeResoniteClient();
        var directory = Path.Combine(_root, "locks");
        SessionLockTestIsolation.Share(client, directory);
        var path = Path.Combine(_root, "state.json");
        using (CheckpointFiles.AcquireWriter(path))
        {
            var e = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(client).ApplyAsync(Document("state-busy", "[]"), new(path)));
            Assert.Equal("APPLY_STATE_BUSY", e.Code);
            using var session = SessionWriteLock.Acquire("ws://fake/", directory);
        }
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3DirectRootDeletionStillRefusesBeforeSending()
    {
        var client = new FakeResoniteClient();
        var exit = await Program.RunAsync(["slot", "delete", "Root", "--yes", "--url", "ws://fake", "--json"],
            _ => Task.FromResult<IResoniteClient>(client));
        Assert.Equal(6, exit);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3PruneAmbiguousComponentIdsStopsBeforeSending()
    {
        var doc = Document("ambiguous-prune", """[{"key":"stale","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        await client.AddComponentAsync(client.Root.Children[0].Id, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        client.ResetWriteCounts();
        var e = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(doc with { Components = [] }, options with { Prune = true, ConfirmDeletes = true }));
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", e.Code);
        Assert.Equal(0, client.Writes);
        Assert.Equal(2, client.Root.Children[0].Components.Count);
    }

    [Fact]
    public async Task S3PruneNeverSelectsRootFromACorruptStaleClaim()
    {
        var doc = Document("root-prune", "[]");
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        var state = ApplyStateStore.Load(options.StateFile!, "root-prune");
        state.Slots["stale-root"] = new("Root", "Root", PathSegments: ["Root"]);
        ApplyStateStore.Save(options.StateFile!, state);
        client.ResetWriteCounts();
        var plan = await service.PlanApplyAsync(doc, options);
        Assert.DoesNotContain(plan.Operations, op => op.Action == "delete" && op.Key == "stale-root");
        var result = await service.ApplyAsync(doc, options with { Prune = true, ConfirmDeletes = true });
        Assert.Equal(0, result.SlotsDeleted);
        Assert.Equal(0, client.Writes);
        Assert.Single(client.Root.Children);
    }

    [Theory]
    [InlineData("ownerChanged")]
    [InlineData("lostResponse")]
    [InlineData("readbackPresent")]
    public async Task S3RelocationSourceDeletionRechecksOwnershipAndKeepsUnverifiedPending(string fault)
    {
        var doc = Document("relocation-delete", """[{"key":"moved","type":"Test.Target","fields":{"Enabled":false}}]""");
        doc = doc with { Children = [new(doc.Slot! with { Key = "destination", Name = "Destination", Parent = null }, [])] };
        var client = new FakeResoniteClient(doc) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(doc, options);
        var sourceSlot = client.Root.Children[0];
        var source = sourceSlot.Components[0];
        var desired = doc with { Components = [], Children = [doc.Children![0] with { Components = doc.Components }] };
        if (fault == "ownerChanged") client.BeforeDeletionObservation = _ => sourceSlot.Name = "Changed";
        client.AfterMutation = (kind, _) =>
        {
            if (kind != "removeComponent") return;
            if (fault == "lostResponse") throw new IOException("lost response");
            if (fault == "readbackPresent")
                client.BeforeComponentRead = id => { if (id == source.Id) throw new RLoopException("RESONITE_OPERATION_FAILED", "absence unproven"); };
        };
        var e = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
        Assert.Equal(fault == "ownerChanged" ? "APPLY_PRECONDITION_FAILED" : fault == "lostResponse" ? "APPLY_WRITE_UNVERIFIED" : "RESONITE_OPERATION_FAILED", e.Code);
        var state = ApplyStateStore.Load(options.StateFile!, "relocation-delete");
        if (fault == "ownerChanged")
        {
            Assert.Empty(state.Pending);
            Assert.Contains(sourceSlot.Components, c => c.Id == source.Id);
        }
        else
        {
            var pending = Assert.Single(state.Pending);
            Assert.Equal("removeComponent", pending.Kind);
            Assert.Equal(source.Id, pending.Id);
            Assert.Equal(sourceSlot.Id, pending.ParentId);
            Assert.Equal(fault != "lostResponse", pending.ResponseReceived);
            client.AfterMutation = null;
            client.BeforeComponentRead = null;
            client.ResetWriteCounts();
            if (fault == "lostResponse")
            {
                Assert.Equal("APPLY_WRITE_UNVERIFIED", (await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options))).Code);
                Assert.Equal(0, client.Writes);
            }
            else
            {
                await service.ApplyAsync(desired, options);
                Assert.Equal(0, client.Writes);
            }
        }
    }

    [Theory]
    [InlineData("child")]
    [InlineData("component")]
    [InlineData("childrenIncomplete")]
    [InlineData("componentsIncomplete")]
    [InlineData("ownershipChanged")]
    [InlineData("omitted")]
    public async Task S3PruneRejectsUnownedOrIncompleteSubtree(string fault)
    {
        var doc = Document("subtree", "[]");
        doc = doc with { Children = [new(doc.Slot! with { Key = "child", Name = "Child", Parent = null }, [])] };
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(doc, options);
        var child = client.Root.Children[0].Children[0];
        if (fault == "child") await client.CreateSlotAsync(new(child.Id, "UserContent"));
        if (fault == "component") await client.AddComponentAsync(child.Id, "Test.Target", new Dictionary<string, string>());
        if (fault == "childrenIncomplete") client.DeletionChildrenObserved = false;
        if (fault == "componentsIncomplete") client.DeletionComponentsObserved = false;
        if (fault == "ownershipChanged") client.BeforeDeletionObservation = id => { if (id == child.Id) child.Name = "Changed"; };
        if (fault == "omitted")
        {
            var id = await client.CreateSlotAsync(new(child.Id, "Hidden"));
            client.SlotIdsOmittedFromChildren.Add(id);
        }
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(doc with { Children = [] }, options));
        Assert.Equal("APPLY_PRECONDITION_FAILED", error.Code);
        Assert.Equal(0, client.Writes);
        Assert.Single(client.Root.Children[0].Children);
        Assert.Empty(ApplyStateStore.Load(options.StateFile!, "subtree").Pending);
    }

    [Fact]
    public async Task S3OwnedSubtreeDeletesOnlyExactIdsAndReadbacksAbsence()
    {
        var doc = Document("owned-subtree", "[]");
        doc = doc with { Children = [new(doc.Slot! with { Key = "child", Name = "Child", Parent = null },
            [new("Test.Target", null, Key: "target")], [new(doc.Slot! with { Key = "grandchild", Name = "Grandchild", Parent = null }, [])])] };
        var client = new FakeResoniteClient(doc);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(doc, options);
        var exact = client.Root.Children[0].Children[0].Id;
        client.ResetWriteCounts();
        var result = await service.ApplyAsync(doc with { Children = [] }, options);
        Assert.Equal(1, client.Writes);
        Assert.Equal(1, result.SlotsDeleted);
        Assert.Equal("SLOT_NOT_FOUND", (await Assert.ThrowsAsync<RLoopException>(() => client.GetSlotAsync(exact, 0, false))).Code);
        var state = ApplyStateStore.Load(options.StateFile!, "owned-subtree");
        Assert.Empty(state.Pending);
        Assert.False(state.Slots.ContainsKey("child"));
        Assert.False(state.Slots.ContainsKey("grandchild"));
        Assert.False(state.Components.ContainsKey("target"));
    }

    [Theory]
    [InlineData("removeComponent")]
    [InlineData("deleteSlot")]
    public async Task S3LostDeletionResponseRemainsPendingWithoutSecondDelete(string kind)
    {
        var doc = Document("lost-delete", """[{"key":"c","type":"Test.Target"}]""");
        doc = doc with { Children = [new(doc.Slot! with { Key = "child", Name = "Child", Parent = null }, [])] };
        var client = new FakeResoniteClient(doc) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"), Prune: true, ConfirmDeletes: true);
        await service.ApplyAsync(doc, options);
        client.AfterMutation = (operation, _) => { if (operation == kind) throw new IOException("lost deletion response"); };
        var desired = kind == "deleteSlot" ? doc with { Children = [] } : doc with { Components = [] };
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
        Assert.Equal(kind, Assert.Single(ApplyStateStore.Load(options.StateFile!, "lost-delete").Pending).Kind);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        var e = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", e.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3DriverReplacementSucceedsWhenOldDriverIsPrunedFirst()
    {
        var initial = Document("staged-driver", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"old","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """);
        var client = new FakeResoniteClient(initial);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "state.json"));
        await service.ApplyAsync(initial, options);
        var old = client.TargetClaimedBy = client.Root.Children[0].Components.Single(c => c.Type == "Test.Source").Id;
        var withoutOld = initial with { Components = [initial.Components![0]] };
        var pruned = await service.ApplyAsync(withoutOld, options with { Prune = true, ConfirmDeletes = true });
        Assert.Equal(1, pruned.ComponentsDeleted);
        Assert.DoesNotContain(client.Root.Children[0].Components, c => c.Id == old);
        var desired = initial with { Components = [initial.Components[0], initial.Components[1] with { Key = "replacement" }] };
        var applied = await service.ApplyAsync(desired, options);
        Assert.Equal(1, applied.ComponentsAdded);
        Assert.Empty(ApplyStateStore.Load(options.StateFile!, "staged-driver").Pending);
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, options);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("setMembers", "update evidence")]
    [InlineData("updateSlot", "update evidence")]
    [InlineData("deleteSlot", "deletion evidence")]
    [InlineData("removeComponent", "deletion evidence")]
    public void S3DiscardWarningsPreserveConfirmedUpdateAndDeletionBindings(string kind, string phrase)
    {
        var warning = ApplyPendingDiscard.WarningFor(kind);
        Assert.Contains(phrase, warning);
        Assert.Contains("preserves confirmed bindings", warning);
        Assert.DoesNotContain("outside management", warning);
        Assert.DoesNotContain("duplicate", warning);
    }
}
