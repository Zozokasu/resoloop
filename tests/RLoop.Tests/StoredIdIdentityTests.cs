using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

// S1-C1F: a stored Slot or Component ID is only a hint. It is reused after a live ownership proof, a live ID that cannot be
// proven stops the command before any write, and equal-looking candidates are counted before any ID is consulted.
// The fake client hands out S1, S2, ... and C1, C2, ... in every fresh world, so a second world collides with old state.
public sealed partial class ApplyWorkflowTests
{
    private const string ChildDoc = """
        {"schemaVersion":"1","ownership":{"key":"KEY"},"slot":{"key":"root","name":"Managed","parent":"Root"},
         "components":[],"children":[{"slot":{"key":"kid","name":"Kid"},"components":[{"key":"target","type":"Test.Target","fields":{"Enabled":true}}]}]}
        """;

    private ApplyDocument ChildDocument(string ownership)
    {
        var path = Path.Combine(_root, ownership + ".json");
        File.WriteAllText(path, ChildDoc.Replace("KEY", ownership));
        return ApplyDocument.Load(path);
    }

    private ApplyDocument ComponentsDocument(string ownership, string components)
    {
        var path = Path.Combine(_root, ownership + ".json");
        File.WriteAllText(path, $$"""
            {"schemaVersion":"1","ownership":{"key":"{{ownership}}"},"slot":{"key":"root","name":"Managed","parent":"Root"},
             "components":{{components}}}
            """);
        return ApplyDocument.Load(path);
    }

    [Fact]
    public async Task HandRenamedSlotStopsResolveAndApplyWithoutWritingOrDuplicating()
    {
        var document = ChildDocument("hand-rename");
        var state = Path.Combine(_root, "hand-rename.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var root = Assert.Single(client.Root.Children);
        root.Name = "Managed-by-hand"; // live ID S1 survives, recorded name and path are gone
        client.ResetWriteCounts();

        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$slot:root", "session-1"));
        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", resolve.Code);
        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", apply.Code);
        Assert.Equal("root", apply.Context["key"]);
        Assert.Equal("S1", apply.Context["storedId"]);
        Assert.Equal("Managed-by-hand", apply.Context["observedName"]);
        Assert.Equal(0, client.Writes);
        Assert.Same(root, Assert.Single(client.Root.Children)); // no second "Managed" was created
    }

    [Fact]
    public async Task HandRenamedChildSlotStopsApplyBeforeCreatingADuplicateChild()
    {
        var document = ChildDocument("hand-rename-child");
        var state = Path.Combine(_root, "hand-rename-child.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var kid = Assert.Single(Assert.Single(client.Root.Children).Children);
        kid.Name = "Kid-by-hand";
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Equal("kid", error.Context["key"]);
        Assert.Equal("S2", error.Context["storedId"]);
        Assert.Equal(0, client.Writes);
        Assert.Same(kid, Assert.Single(Assert.Single(client.Root.Children).Children));
    }

    [Fact]
    public async Task ChildStoredIdHeldByAnUnrelatedSlotStopsEvenWhenTheParentPathMatches()
    {
        var document = ChildDocument("collide-child");
        var state = Path.Combine(_root, "collide-child.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state));
        var newWorld = new FakeResoniteClient();
        var managed = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S1: same id, same name, same path
        await newWorld.CreateSlotAsync(new SlotCreateRequest(managed, "Decoy")); // S2: the stored id of "kid"
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(newWorld).ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Equal("kid", error.Context["key"]);
        Assert.Equal(0, newWorld.Writes);
        Assert.Equal("Decoy", Assert.Single(Assert.Single(newWorld.Root.Children).Children).Name);
    }

    [Fact]
    public async Task ApplyRecreatesWhenTheStoredIdAndTheOldPathAreBothAbsent()
    {
        var document = ChildDocument("truly-absent");
        var state = Path.Combine(_root, "truly-absent.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state));
        var emptyWorld = new FakeResoniteClient();
        emptyWorld.ResetWriteCounts();

        var result = await new WorldService(emptyWorld).ApplyAsync(document, new ApplyOptions(state));

        Assert.True(result.Created);
        Assert.Equal(2, result.SlotsCreated);
        var root = Assert.Single(emptyWorld.Root.Children);
        Assert.Equal("Managed", root.Name);
        var kid = Assert.Single(root.Children);
        Assert.Equal("Kid", kid.Name);
        Assert.Equal("Test.Target", Assert.Single(kid.Components).Type);
    }

    [Fact]
    public async Task StoredComponentIdOnAnotherSlotIsNeverReturnedForTheOwnerSlot()
    {
        var document = Document("other-slot-component", CounterDoc);
        var state = Path.Combine(_root, "other-slot-component.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state));
        var newWorld = new FakeResoniteClient();
        var other = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Other")); // S1 takes the stored id of "root"
        await newWorld.AddComponentAsync(other, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" }); // C1 = stored id
        var owner = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S2: the real owner at the recorded path
        var service = new WorldService(newWorld);
        newWorld.ResetWriteCounts();

        var slot = await service.ResolveStableReferenceAsync(state, "$slot:root", "session-1");
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$member:target.Enabled", "session-1"));

        Assert.Equal(owner, slot.Id); // unique recorded path re-resolves the Slot; the colliding S1 is ignored
        Assert.Equal("FLUX_BINDING_COMPONENT_NOT_FOUND", error.Code);
        Assert.Equal(0, newWorld.Writes);
    }

    [Fact]
    public async Task UnprovableSameTypeLookAlikeStopsApplyAndResolveWhileTheCompleteRecordedSetIsAccepted()
    {
        var document = ComponentsDocument("same-type", """
            [{"key":"t1","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"t2","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        var state = Path.Combine(_root, "same-type.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var root = Assert.Single(client.Root.Children);
        var (c1, c2) = (root.Components[0].Id, root.Components[1].Id);
        root.Components.Reverse(); // order is not evidence
        client.SessionId = "session-2";
        client.ResetWriteCounts();

        // Complete recorded set: every stored id is on the owner Slot, counts match, so each key keeps its own Component.
        var again = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(c1, (await service.ResolveStableReferenceAsync(state, "$component:t1", "session-2")).Id);
        Assert.Equal(c2, (await service.ResolveStableReferenceAsync(state, "$component:t2", "session-2")).Id);
        Assert.Equal(2, again.ComponentsUnchanged);
        Assert.Equal(0, client.Writes);

        // A third look-alike makes the set unprovable: stored ids and index must not pick between equals.
        client.PrependComponent(root, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" });
        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$component:t1", "session-2"));
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", apply.Code);
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", resolve.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task SameTypeComponentsWithStaleStoredIdsAreAmbiguousInANewWorld()
    {
        var document = ComponentsDocument("same-type-stale", """
            [{"key":"t1","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"t2","type":"Test.Target","fields":{"Enabled":true}}]
            """);
        var state = Path.Combine(_root, "same-type-stale.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state)); // C1, C2
        var newWorld = new FakeResoniteClient();
        var managed = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S1 verifies by name and path
        var scratch = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Scratch"));
        foreach (var _ in new[] { 1, 2 })
        {
            var burned = await newWorld.AddComponentAsync(scratch, "Test.Target", new Dictionary<string, string>());
            await newWorld.RemoveComponentAsync(burned.Id); // burn C1, C2 so the new Components get fresh ids
        }
        await newWorld.AddComponentAsync(managed, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" }); // C3
        await newWorld.AddComponentAsync(managed, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "true" }); // C4
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(newWorld).ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, newWorld.Writes);
    }

    [Fact]
    public async Task SameNamedSiblingHoldingTheStoredIdIsAmbiguousForResolveAndApply()
    {
        var document = Document("same-name", CounterDoc);
        var state = Path.Combine(_root, "same-name.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S2, same name as the owned S1
        client.ResetWriteCounts();

        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$slot:root", "session-1"));
        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("SLOT_PATH_AMBIGUOUS", resolve.Code);
        Assert.Equal("APPLY_TARGET_AMBIGUOUS", apply.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task SameNamedAncestorSiblingIsAmbiguousEvenWhenTheChildKeepsItsStoredId()
    {
        var document = ChildDocument("same-name-ancestor");
        var state = Path.Combine(_root, "same-name-ancestor.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        await client.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // second "Managed" above the owned Kid
        client.ResetWriteCounts();

        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$slot:kid", "session-1"));

        Assert.Equal("SLOT_PATH_AMBIGUOUS", resolve.Code);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task RelocatablePathCandidateWithoutMatchingComponentEvidenceIsNotAdopted()
    {
        var path = Path.Combine(_root, "reloc-evidence.json");
        File.WriteAllText(path, """
            {"schemaVersion":"1","ownership":{"key":"reloc-evidence"},
             "slot":{"key":"root","name":"Managed","parent":"Root"},"components":[],
             "children":[{"slot":{"key":"tool","name":"Tool","runtimeRelocatable":true},
               "components":[{"key":"identity","type":"Test.Target","fields":{"Enabled":true},"identityFields":["Enabled"]}]}]}
            """);
        var document = ApplyDocument.Load(path);
        var state = Path.Combine(_root, "reloc-evidence.state.json");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var managed = Assert.Single(client.Root.Children);
        var tool = Assert.Single(managed.Children);
        await client.DeleteSlotAsync(tool.Id); // stored id is gone
        var impostor = await client.CreateSlotAsync(new SlotCreateRequest(managed.Id, "Tool")); // takes the recorded path
        await client.AddComponentAsync(impostor, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        client.ResetWriteCounts();

        var result = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(1, result.SlotsCreated); // a new Tool, not the impostor
        var impostorSlot = managed.Children.Single(child => child.Id == impostor);
        Assert.Equal("false", Assert.Single(impostorSlot.Components).Members["Enabled"].Value!.ToJsonString());
        Assert.Equal(2, managed.Children.Count);
    }

    [Fact]
    public async Task AncestorChainBeyondTheBoundIsDiagnosedInsteadOfSilentlyDroppingTheStoredId()
    {
        var client = new FakeResoniteClient();
        var parentId = "Root";
        string deepId = "";
        for (var level = 1; level <= 66; level++)
        {
            deepId = await client.CreateSlotAsync(new SlotCreateRequest(parentId, level == 66 ? "Deep" : "L" + level));
            parentId = deepId;
        }
        var state = Path.Combine(_root, "deep.state.json");
        // The recorded path names a parent that does not exist, so no path can re-resolve the Slot: the stored ID is all there is.
        File.WriteAllText(state, new JsonObject
        {
            ["schemaVersion"] = 2, ["ownershipKey"] = "deep",
            ["slots"] = new JsonObject { ["deep"] = new JsonObject { ["id"] = deepId, ["path"] = "Root/Elsewhere/Deep",
                ["pathSegments"] = new JsonArray("Root", "Elsewhere", "Deep") } },
            ["components"] = new JsonObject(),
        }.ToJsonString());
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() =>
            new WorldService(client).ResolveStableReferenceAsync(state, "$slot:deep", "session-1"));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Contains("deeper than 64", (string)error.Context["reason"]!);
        Assert.Equal(deepId, error.Context["storedId"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task PruneDoesNotDeleteAnObjectBecauseItOnlyHoldsAStoredId()
    {
        var document = ChildDocument("prune-id");
        var state = Path.Combine(_root, "prune-id.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var kid = Assert.Single(Assert.Single(client.Root.Children).Children);
        kid.Name = "Kid-by-hand"; // still holds the stored id of key "kid", at another name
        var reduced = Path.Combine(_root, "prune-id.json");
        var raw = JsonNode.Parse(File.ReadAllText(reduced))!;
        raw["children"] = new JsonArray();
        File.WriteAllText(reduced, raw.ToJsonString());
        client.ResetWriteCounts();

        await service.ApplyAsync(ApplyDocument.Load(reduced), new ApplyOptions(state, Prune: true, ConfirmDeletes: true));

        Assert.Equal(0, client.Writes);
        Assert.Same(kid, Assert.Single(Assert.Single(client.Root.Children).Children));
    }

    [Fact]
    public async Task PruneDoesNotDeleteAComponentThatOnlySharesTheStoredId()
    {
        var document = ComponentsDocument("prune-component", """[{"key":"gone","type":"Test.Target","fields":{"Enabled":true}}]""");
        var state = Path.Combine(_root, "prune-component.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state)); // gone = C1
        var newWorld = new FakeResoniteClient();
        var managed = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed")); // S1: verified by name and path
        var bystander = await newWorld.AddComponentAsync(managed, "Test.Source", new Dictionary<string, string>()); // C1, another type
        var withoutGone = ComponentsDocument("prune-component", "[]");
        newWorld.ResetWriteCounts();

        await new WorldService(newWorld).ApplyAsync(withoutGone, new ApplyOptions(state, Prune: true, ConfirmDeletes: true));

        Assert.Equal(0, newWorld.Writes);
        Assert.Equal(bystander.Id, Assert.Single(Assert.Single(newWorld.Root.Children).Components).Id);
    }
}
