using RLoop.Core;

namespace RLoop.Tests;

// UniqueSessionId is a per-connection counter. A matching counter must never authorize reusing a stored live ID;
// the fake client gives every new world the same ids (S1, C1, ...) so a fresh world collides with old state.
public sealed partial class ApplyWorkflowTests
{
    private const string CounterDoc = """[{"key":"target","type":"Test.Target","fields":{"Enabled":true}}]""";

    private async Task<(string State, ApplyDocument Document, ApplyResult First)> ApplyIntoOldWorldAsync(string name)
    {
        var document = Document(name, CounterDoc);
        var state = Path.Combine(_root, name + ".state.json");
        var first = await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal("S1", first.SlotId);
        return (state, document, first);
    }

    [Fact]
    public async Task EqualSessionCounterDoesNotReuseStoredSlotIdWhoseLiveObjectIsNotTheOwnedSlot()
    {
        var (state, _, _) = await ApplyIntoOldWorldAsync("counter-slot");
        var newWorld = new FakeResoniteClient();
        await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Decoy")); // takes id S1, counter also "session-1"
        var service = new WorldService(newWorld);
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$slot:root", "session-1"));

        // The stored ID is alive but belongs to another object and the recorded path is absent: stop, do not guess.
        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Equal("S1", error.Context["storedId"]);
        Assert.Equal("Decoy", error.Context["observedName"]);
        Assert.Equal(0, newWorld.Writes);
        Assert.Equal("Decoy", Assert.Single(newWorld.Root.Children).Name);
    }

    [Fact]
    public async Task EqualSessionCounterApplyDoesNotAdoptOrRenameTheObjectHoldingTheStoredId()
    {
        var (state, document, _) = await ApplyIntoOldWorldAsync("counter-apply");
        var newWorld = new FakeResoniteClient();
        await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Decoy"));
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(newWorld).ApplyAsync(document, new ApplyOptions(state)));

        // The object holding the stored ID is neither adopted nor renamed, and no duplicate is created.
        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Equal("root", error.Context["key"]);
        Assert.Equal("S1", error.Context["storedId"]);
        Assert.Equal("Decoy", error.Context["observedName"]);
        Assert.Equal(0, newWorld.Writes);
        var decoy = Assert.Single(newWorld.Root.Children);
        Assert.Equal("Decoy", decoy.Name);
        Assert.Empty(decoy.Components);
    }

    [Fact]
    public async Task EqualSessionCounterDoesNotReuseStoredComponentIdOfADifferentType()
    {
        var (state, document, _) = await ApplyIntoOldWorldAsync("counter-component");
        var newWorld = new FakeResoniteClient();
        var slotId = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S1 at the recorded path
        await newWorld.AddComponentAsync(slotId, "Test.Source", new Dictionary<string, string>()); // takes id C1
        var service = new WorldService(newWorld);

        var resolveError = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$component:target", "session-1"));
        await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal("FLUX_BINDING_COMPONENT_NOT_FOUND", resolveError.Code);
        var slot = Assert.Single(newWorld.Root.Children);
        Assert.Equal(["Test.Source", "Test.Target"], slot.Components.Select(component => component.Type).ToArray());
        Assert.DoesNotContain(slot.Components[0].Members, member => member.Value.Value?.ToJsonString() == "true");
    }

    [Theory]
    [InlineData("session-1")]
    [InlineData("session-restarted")]
    public async Task OwnedObjectsAreReusedWithoutWritesRegardlessOfSessionCounter(string counterAfterReconnect)
    {
        var document = Document("counter-owned", CounterDoc);
        var state = Path.Combine(_root, "counter-owned.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        var first = await service.ApplyAsync(document, new ApplyOptions(state));
        var componentId = (await service.ResolveStableReferenceAsync(state, "$component:target", "session-1")).Id;
        client.SessionId = counterAfterReconnect;
        client.ResetWriteCounts();

        var again = await service.ApplyAsync(document, new ApplyOptions(state));
        var slotResolved = await service.ResolveStableReferenceAsync(state, "$slot:root", counterAfterReconnect);
        var componentResolved = await service.ResolveStableReferenceAsync(state, "$component:target", counterAfterReconnect);

        Assert.False(again.Created);
        Assert.Equal(first.SlotId, again.SlotId);
        Assert.Equal(0, client.Writes);
        Assert.Equal(first.SlotId, slotResolved.Id);
        Assert.Equal(componentId, componentResolved.Id);
        Assert.Single(client.Root.Children);
    }

    [Fact]
    public async Task EqualSessionCounterWithSeveralSameNamedCandidatesStopsBeforeMutation()
    {
        var (state, document, _) = await ApplyIntoOldWorldAsync("counter-ambiguous");
        var newWorld = new FakeResoniteClient();
        await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Other")); // stored id S1 belongs to a different name
        await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(newWorld).ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_TARGET_AMBIGUOUS", error.Code);
        Assert.Equal(0, newWorld.Writes);
    }
}
