using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task RelocationStopsBeforeWritingWhenDestinationHasAnUnrelatedSameNamedSlot()
    {
        var document = Document("occupied-relocation", CounterDoc);
        var raw = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
        raw["slot"]!["parent"] = "Root/ParentA";
        File.WriteAllText(document.SourcePath!, raw.ToJsonString());
        var state = Path.Combine(_root, "occupied-relocation.state.json");
        var client = new FakeResoniteClient();
        var parentA = await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentA"));
        var parentB = await client.CreateSlotAsync(new SlotCreateRequest("Root", "ParentB"));
        var service = new WorldService(client);
        await service.ApplyAsync(ApplyDocument.Load(document.SourcePath!), new ApplyOptions(state));
        var source = Assert.Single(client.Root.Children.Single(x => x.Id == parentA).Children);
        var destination = await client.CreateSlotAsync(new SlotCreateRequest(parentB, "Managed"));
        await client.AddComponentAsync(destination, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        raw["slot"]!["parent"] = "Root/ParentB";
        File.WriteAllText(document.SourcePath!, raw.ToJsonString());
        var checkpoint = File.ReadAllText(state);
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(
            ApplyDocument.Load(document.SourcePath!), new ApplyOptions(state, Prune: true, ConfirmDeletes: true)));

        Assert.Equal("APPLY_TARGET_AMBIGUOUS", error.Code);
        Assert.Equal("relocationDestinationOccupied", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Same(source, Assert.Single(client.Root.Children.Single(x => x.Id == parentA).Children));
        Assert.Equal(destination, Assert.Single(client.Root.Children.Single(x => x.Id == parentB).Children).Id);
        Assert.Equal("false", client.Root.Children.Single(x => x.Id == parentB).Children[0].Components[0].Members["Enabled"].Value!.ToJsonString());
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompletelyReusedComponentIdsStopUpdateAndPruneWithoutIdentityEvidence(bool prune, bool relocatable)
    {
        const string components = """
            [{"key":"t1","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"t2","type":"Test.Target","fields":{"Enabled":true}}]
            """;
        var document = ComponentsDocument("fully-reused", components);
        if (relocatable)
        {
            var raw = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
            raw["slot"]!["runtimeRelocatable"] = true;
            File.WriteAllText(document.SourcePath!, raw.ToJsonString());
            document = ApplyDocument.Load(document.SourcePath!);
        }
        var state = Path.Combine(_root, "fully-reused.state.json");
        await new WorldService(new FakeResoniteClient()).ApplyAsync(document, new ApplyOptions(state));
        var newWorld = new FakeResoniteClient();
        var owner = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        await newWorld.AddComponentAsync(owner, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        await newWorld.AddComponentAsync(owner, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(saved["components"]!["t1"]!["id"]!.GetValue<string>(), newWorld.Root.Children[0].Components[0].Id);
        Assert.Equal(saved["components"]!["t2"]!["id"]!.GetValue<string>(), newWorld.Root.Children[0].Components[1].Id);
        if (prune && !relocatable) document = ComponentsDocument("fully-reused", "[]");
        var checkpoint = File.ReadAllText(state);
        newWorld.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => new WorldService(newWorld).ApplyAsync(
            document, new ApplyOptions(state, Prune: prune, ConfirmDeletes: prune)));

        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        Assert.Equal(0, newWorld.Writes);
        Assert.Equal(2, newWorld.Root.Children[0].Components.Count);
        Assert.All(newWorld.Root.Children[0].Components, c => Assert.Equal("false", c.Members["Enabled"].Value!.ToJsonString()));
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Fact]
    public async Task CompleteStoredIdsDoNotOverrideSwappedReferenceTopology()
    {
        var document = ComponentsDocument("swapped-topology", """
            [{"key":"source-a","type":"Test.Source","fields":{"Target":"$slot:target-a"}},
             {"key":"source-b","type":"Test.Source","fields":{"Target":"$slot:target-b"}}]
            """);
        var raw = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
        raw["children"] = JsonNode.Parse("""
            [{"slot":{"key":"target-a","name":"TargetA"}},{"slot":{"key":"target-b","name":"TargetB"}}]
            """);
        File.WriteAllText(document.SourcePath!, raw.ToJsonString());
        document = ApplyDocument.Load(document.SourcePath!);
        var state = Path.Combine(_root, "swapped-topology.state.json");
        await new WorldService(new FakeResoniteClient(document)).ApplyAsync(document, new ApplyOptions(state));
        var newWorld = new FakeResoniteClient(document);
        var owner = await newWorld.CreateSlotAsync(new SlotCreateRequest("Root", "Managed"));
        var targetA = await newWorld.CreateSlotAsync(new SlotCreateRequest(owner, "TargetA"));
        var targetB = await newWorld.CreateSlotAsync(new SlotCreateRequest(owner, "TargetB"));
        var first = await newWorld.AddComponentAsync(owner, "Test.Source", new Dictionary<string, string> { ["Target"] = targetB });
        var second = await newWorld.AddComponentAsync(owner, "Test.Source", new Dictionary<string, string> { ["Target"] = targetA });
        var saved = JsonNode.Parse(File.ReadAllText(state))!;
        Assert.Equal(first.Id, saved["components"]!["source-a"]!["id"]!.GetValue<string>());
        Assert.Equal(second.Id, saved["components"]!["source-b"]!["id"]!.GetValue<string>());
        newWorld.ResetWriteCounts();
        var service = new WorldService(newWorld);

        Assert.Equal(second.Id, (await service.ResolveStableReferenceAsync(state, "$component:source-a", "session-1")).Id);
        Assert.Equal(first.Id, (await service.ResolveStableReferenceAsync(state, "$component:source-b", "session-1")).Id);
        var result = await service.ApplyAsync(document, new ApplyOptions(state));
        Assert.Equal(2, result.ComponentsUnchanged);
        Assert.Equal(0, newWorld.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StoredSlotReadFailureStopsApplyAndResolveBeforeRecreation(bool child, bool exception)
    {
        var document = child ? ChildDocument("read-failure") : ComponentsDocument("read-failure", CounterDoc);
        var state = Path.Combine(_root, "read-failure.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var slot = child ? client.Root.Children[0].Children[0] : client.Root.Children[0];
        slot.Name = "Renamed";
        client.SlotIdsOmittedFromChildren.Add(slot.Id); // not observed at its recorded path; direct ID read fails below
        client.SlotReadFailures[slot.Id] = exception ? new IOException("Unreadable response") :
            new RLoopException("RESONITE_OPERATION_FAILED", "Unreadable response", ExitCodes.OperationFailed);
        var checkpoint = File.ReadAllText(state);
        client.ResetWriteCounts();

        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(
            state, child ? "$slot:kid" : "$slot:root", "session-1"));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", resolve.Code);
        Assert.Equal("storedIdReadFailed", resolve.Context["reason"]);
        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", apply.Code);
        Assert.Equal("storedIdReadFailed", apply.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(checkpoint, File.ReadAllText(state));
        Assert.Equal("Renamed", slot.Name);
    }

    [Theory]
    [InlineData("identityMismatch")]
    [InlineData("membersNull")]
    [InlineData("memberMissing")]
    public async Task SurvivingStoredComponentWithMismatchedOrUnreadEvidenceStopsWithoutCreating(string evidence)
    {
        var document = ComponentsDocument("component-evidence", """
            [{"key":"identity","type":"Test.Target","initialFields":{"Enabled":true},"identityFields":["Enabled"]}]
            """);
        var state = Path.Combine(_root, "component-evidence.state.json");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var component = Assert.Single(client.Root.Children[0].Components);
        var unread = evidence != "identityMismatch";
        if (evidence == "membersNull") client.UnreadComponentIds.Add(component.Id);
        else if (evidence == "memberMissing") component.Members.Remove("Enabled");
        else await client.SetComponentMemberAsync(component.Id, "Enabled", "false");
        var checkpoint = File.ReadAllText(state);
        client.ResetWriteCounts();

        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));
        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(state, "$component:identity", "session-1"));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", apply.Code);
        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", resolve.Code);
        Assert.Equal(unread ? "componentEvidenceUnread" : "componentEvidenceMismatch", apply.Context["reason"]);
        Assert.Equal(apply.Context["reason"], resolve.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Same(component, Assert.Single(client.Root.Children[0].Components));
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Fact]
    public async Task RelocatableRecordedPathReadFailureStopsBeforeRecreation()
    {
        var document = ComponentsDocument("reloc-path-read", """
            [{"key":"identity","type":"Test.Target","initialFields":{"Enabled":true},"identityFields":["Enabled"]}]
            """);
        var raw = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
        raw["children"] = new JsonArray(new JsonObject
        {
            ["slot"] = new JsonObject { ["key"] = "tool", ["name"] = "Tool", ["runtimeRelocatable"] = true },
            ["components"] = raw["components"]!.DeepClone()
        });
        raw["components"] = new JsonArray();
        File.WriteAllText(document.SourcePath!, raw.ToJsonString());
        document = ApplyDocument.Load(document.SourcePath!);
        var state = Path.Combine(_root, "reloc-path-read.state.json");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        var owner = client.Root.Children[0];
        await client.DeleteSlotAsync(owner.Children[0].Id);
        var impostor = await client.CreateSlotAsync(new SlotCreateRequest(owner.Id, "Tool"));
        await client.AddComponentAsync(impostor, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        client.SlotReadFailures[impostor] = new RLoopException("RESONITE_OPERATION_FAILED", "Unreadable old path", ExitCodes.OperationFailed);
        var checkpoint = File.ReadAllText(state);
        client.ResetWriteCounts();

        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new ApplyOptions(state)));

        Assert.Equal("APPLY_STORED_ID_UNVERIFIED", error.Code);
        Assert.Equal("recordedPathReadFailed", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(impostor, Assert.Single(owner.Children).Id);
        Assert.Equal(checkpoint, File.ReadAllText(state));
    }

    [Fact]
    public async Task TrulyAbsentStoredComponentIsRecreatedOnTheVerifiedOwnerSlot()
    {
        var document = ComponentsDocument("component-absent", CounterDoc);
        var state = Path.Combine(_root, "component-absent.state.json");
        var client = new FakeResoniteClient();
        var service = new WorldService(client);
        await service.ApplyAsync(document, new ApplyOptions(state));
        await client.RemoveComponentAsync(client.Root.Children[0].Components[0].Id);
        client.ResetWriteCounts();

        var result = await service.ApplyAsync(document, new ApplyOptions(state));

        Assert.Equal(1, result.ComponentsAdded);
        Assert.Single(client.Root.Children[0].Components);
    }
}
