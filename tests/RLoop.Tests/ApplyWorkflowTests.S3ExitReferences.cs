using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task S3UnsentReferenceSelectorsDoNotGrowOnReapplyOrConfigReadback(string mode)
    {
        var document = ExitReferenceDocument("unsent-reference", mode);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "unsent-reference.state.json"));
        await service.ApplyAsync(document, options);
        AssertNoExitReferenceEvidence(options.StateFile!, document);
        var source = client.Root.Children.Single().Components.Single(c => c.Type == "Test.Source");
        client.ResetWriteCounts();

        for (var i = 0; i < 2; i++)
        {
            await service.ApplyAsync(document, options);
            AssertNoExitReferenceEvidence(options.StateFile!, document);
        }
        Assert.Equal(0, client.Writes);
        Assert.Equal(source.Id, (await service.ResolveStableReferenceAsync(options.StateFile!, "$component:source", "session-1")).Id);

        var desired = ExitReferenceConfig(document, true);
        await service.ApplyAsync(desired, options);
        Assert.Equal(1, client.Writes);
        Assert.True(source.Members["Config"].Value!.GetValue<bool>());
        AssertNoExitReferenceEvidence(options.StateFile!, document);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("driver-owned")]
    public async Task S3CreationReadbackExcludedSelectorCannotReviveAtNoOpCheckpoint(string mode)
    {
        var document = ExitReferenceDocument("excluded-reference", null);
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        client.AfterMutation = (kind, id) =>
        {
            if (kind != "addComponent") return;
            var component = client.Root.Children.Single().Components.Single(c => c.Id == id);
            if (component.Type == "Test.Source")
                component.Members["Target"] = new("reference", id + ":Target");
        };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "excluded-reference.state.json"));
        var initial = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, options));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", initial.Code);
        Assert.Equal("readbackMismatch", initial.Context["reason"]);
        AssertNoExitReferenceEvidence(options.StateFile!, document);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        var preserved = document with { Components = [document.Components![0], document.Components[1] with
            { PropertyModes = new Dictionary<string, string> { ["Target"] = mode } }] };

        await service.ApplyAsync(preserved, options);
        await service.ApplyAsync(preserved, options);

        AssertNoExitReferenceEvidence(options.StateFile!, document);
        Assert.Empty(ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key).Pending);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("runtime", false)]
    [InlineData("driver-owned", false)]
    [InlineData("runtime", true)]
    [InlineData("driver-owned", true)]
    public async Task S3UnmanagedReferenceSiblingCannotTakeOwnershipFromUnsentOrPollutedState(string mode, bool polluted)
    {
        var document = ExitReferenceDocument("reference-sibling", mode);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "reference-sibling.state.json"));
        await service.ApplyAsync(document, options);
        var owner = client.Root.Children.Single();
        var source = owner.Components.Single(c => c.Type == "Test.Source");
        var target = owner.Components.Single(c => c.Type == "Test.Target");
        var sibling = await client.AddComponentAsync(owner.Id, "Test.Source", new Dictionary<string, string>
            { ["Target"] = target.Members["Enabled"].Id!, ["Config"] = "false" });
        if (polluted)
        {
            // Handwritten old-state fixture: earlier checkpoints incorrectly promoted this unsent declaration.
            var old = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
            old["components"]!["source"]!["referenceSelectors"] = new JsonObject { ["Target"] = "$member:target.Enabled" };
            File.WriteAllText(options.StateFile!, old.ToJsonString());
        }
        var checkpoint = File.ReadAllText(options.StateFile!);
        client.ResetWriteCounts();

        var apply = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(ExitReferenceConfig(document, true), options));
        var resolve = await Assert.ThrowsAsync<RLoopException>(() => service.ResolveStableReferenceAsync(options.StateFile!, "$component:source", "session-1"));

        Assert.Equal(polluted ? "APPLY_STORED_ID_UNVERIFIED" : "STABLE_COMPONENT_AMBIGUOUS", apply.Code);
        Assert.Equal(apply.Code, resolve.Code);
        if (polluted)
        {
            Assert.Equal("componentEvidenceMismatch", apply.Context["reason"]);
            Assert.Equal(apply.Context["reason"], resolve.Context["reason"]);
        }
        Assert.Equal(0, client.Writes);
        Assert.False(source.Members["Config"].Value!.GetValue<bool>());
        Assert.False(owner.Components.Single(c => c.Id == sibling.Id).Members["Config"].Value!.GetValue<bool>());
        Assert.Equal(checkpoint, File.ReadAllText(options.StateFile!));
        Assert.Equal(source.Id, ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key).Components["source"].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3UnsentVerifiedSelectorSurvivesConfigReadbackOnlyWhileReferenceStillMatches(bool drift)
    {
        var document = ExitReferenceDocument("verified-readback", null);
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "verified-readback.state.json"));
        await service.ApplyAsync(document, options);
        Assert.Equal("$member:target.Enabled", ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key)
            .Components["source"].ReferenceSelectors!["Target"]);
        var source = client.Root.Children.Single().Components.Single(c => c.Type == "Test.Source");
        if (drift)
            client.AfterMutation = (kind, id) =>
            {
                if (kind == "setMembers" && id == source.Id)
                    source.Members["Target"] = new("reference", id + ":Target");
            };
        var desired = ExitReferenceConfig(document, true);
        desired = desired with { Components = [desired.Components![0], desired.Components[1] with
            { PropertyModes = new Dictionary<string, string> { ["Target"] = "runtime" } }] };
        client.ResetWriteCounts();

        await service.ApplyAsync(desired, options);

        Assert.Equal(1, client.Writes);
        if (drift) AssertNoExitReferenceEvidence(options.StateFile!, document);
        else Assert.Equal("$member:target.Enabled", ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key)
            .Components["source"].ReferenceSelectors!["Target"]);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, options);
        Assert.Equal(0, client.Writes);
        if (drift) AssertNoExitReferenceEvidence(options.StateFile!, document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3VerifiedTopologyStillResolvesAbsentOrStaleStoredIds(bool absent)
    {
        var document = ExitReferenceDocument("verified-stale", null);
        document = document with { Components = [.. document.Components!, new ApplyComponentSpec("Test.Source",
            new Dictionary<string, JsonElement> { ["Target"] = JsonSerializer.SerializeToElement("$slot:root"),
                ["Config"] = JsonSerializer.SerializeToElement(false) }, "other")] };
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "verified-stale.state.json"));
        await service.ApplyAsync(document, options);
        var saved = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        var expected = saved.Components["source"].Id;
        if (absent)
        {
            var owner = client.Root.Children.Single();
            var target = owner.Components.Single(c => c.Type == "Test.Target");
            await client.RemoveComponentAsync(expected);
            expected = (await client.AddComponentAsync(owner.Id, "Test.Source", new Dictionary<string, string>
                { ["Target"] = target.Members["Enabled"].Id!, ["Config"] = "false" })).Id;
        }
        else
        {
            var old = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
            old["components"]!["source"]!["id"] = "absent-stale-id";
            File.WriteAllText(options.StateFile!, old.ToJsonString());
        }
        client.ResetWriteCounts();

        Assert.Equal(expected, (await service.ResolveStableReferenceAsync(options.StateFile!, "$component:source", "session-1")).Id);
        await service.ApplyAsync(document, options);

        Assert.Equal(0, client.Writes);
        Assert.Equal(expected, ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key).Components["source"].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3LegacyPendingUnsentSelectorRequiresItsActualDeclaredTarget(bool matches)
    {
        var document = ExitReferenceDocument("legacy-pending-reference", "runtime");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "legacy-pending-reference.state.json"));
        await service.ApplyAsync(document, options);
        var owner = client.Root.Children.Single();
        var source = owner.Components.Single(c => c.Type == "Test.Source");
        var target = owner.Components.Single(c => c.Type == "Test.Target");
        var actualTarget = matches ? target.Members["Enabled"].Id! : owner.Id + ":Rotation";
        await client.SetComponentMembersAsync(source.Id, source.Type, new Dictionary<string, string>
            { ["Config"] = "true", ["Target"] = actualTarget });

        // Independent legacy fixture: Config was accepted, while Target was never sent. Earlier code
        // promoted the declaration even when both precondition and readback pointed at unrelated field B.
        var old = JsonNode.Parse(File.ReadAllText(options.StateFile!))!;
        old["components"]!["source"]!["referenceSelectors"] = new JsonObject { ["Target"] = "$member:target.Enabled" };
        old["pending"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "legacy-config-only",
            ["kind"] = "setMembers", ["key"] = "source", ["ownershipKey"] = "legacy-pending-reference",
            ["id"] = source.Id, ["type"] = "Test.Source", ["parentId"] = owner.Id,
            ["sendStatus"] = "responseReceived", ["responseReceived"] = true, ["responseAccepted"] = true,
            ["session"] = new JsonObject
                { ["normalizedUrl"] = "ws://fake/", ["discoverSessionId"] = "S-test", ["identityStatus"] = "matched" },
            ["members"] = new JsonObject { ["Config"] = "true" },
            ["componentBinding"] = old["components"]!["source"]!.DeepClone(),
            ["ownershipSlots"] = new JsonObject { [owner.Id] = new JsonObject
                { ["id"] = owner.Id, ["name"] = "Managed", ["parentId"] = "Root" } },
            ["precondition"] = new JsonObject
            {
                ["id"] = source.Id, ["type"] = "Test.Source",
                ["members"] = new JsonObject
                {
                    ["Config"] = new JsonObject { ["kind"] = "field", ["id"] = source.Id + ":Config", ["type"] = "value", ["value"] = false },
                    ["Target"] = new JsonObject { ["kind"] = "reference", ["id"] = source.Id + ":Target", ["targetId"] = actualTarget }
                }
            }
        });
        File.WriteAllText(options.StateFile!, old.ToJsonString());
        var before = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        client.ResetWriteCounts();

        // F9: the sent Config has a known result on the exact recorded ID, so the pending write is resolved in
        // both cases. The never-sent Target cannot keep it pending; its selector survives only when the actual
        // reference is the declared target. Dropping it leaves the recorded ID as owner and replans normally.
        await service.ApplyAsync(ExitReferenceConfig(document, true), options);

        var saved = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        Assert.Empty(saved.Pending);
        if (matches) Assert.Equal("$member:target.Enabled", saved.Components["source"].ReferenceSelectors!["Target"]);
        else AssertNoExitReferenceEvidence(options.StateFile!, document);
        Assert.Equal(source.Id, saved.Components["source"].Id);
        Assert.Equal(before.Components["source"].Id, saved.Components["source"].Id);
        Assert.Equal(before.Components["target"].Id, saved.Components["target"].Id);
        Assert.Equal(before.Slots.ToDictionary(slot => slot.Key, slot => slot.Value.Id),
            saved.Slots.ToDictionary(slot => slot.Key, slot => slot.Value.Id));
        Assert.Equal(0, client.Writes);
        Assert.Equal(actualTarget, source.Members["Target"].TargetId);
        Assert.True(source.Members["Config"].Value!.GetValue<bool>());

        // The repaired checkpoint is stable: no selector growth, no write and the same owner on reapply.
        await service.ApplyAsync(ExitReferenceConfig(document, true), options);
        Assert.Equal(0, client.Writes);
        if (!matches) AssertNoExitReferenceEvidence(options.StateFile!, document);
        Assert.Equal(source.Id, (await service.ResolveStableReferenceAsync(options.StateFile!, "$component:source", "session-1")).Id);
    }

    [Theory]
    [InlineData("cleared")]
    [InlineData("retargeted")]
    [InlineData("ambiguous")]
    public async Task S3ResumedConfigPendingResolvesAndDropsUnsentSelectorThatNoLongerHolds(string drift)
    {
        var document = ExitReferenceDocument("resume-drift", null);
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "resume-drift.state.json"));
        await service.ApplyAsync(document, options);
        Assert.Equal("$member:target.Enabled", ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key)
            .Components["source"].ReferenceSelectors!["Target"]);
        var owner = client.Root.Children.Single();
        var source = owner.Components.Single(c => c.Type == "Test.Source");
        var target = owner.Components.Single(c => c.Type == "Test.Target");
        var desired = ExitReferenceConfig(document, true);
        desired = desired with { Components = [desired.Components![0], desired.Components[1] with
            { PropertyModes = new Dictionary<string, string> { ["Target"] = "runtime" } }] };

        // Only Config is sent and accepted; the readback times out, so the write stays pending with a valid selector.
        client.AfterMutation = (kind, id) =>
        {
            if (kind == "setMembers" && id == source.Id)
                client.BeforeComponentRead = _ => throw new RLoopException("REQUEST_TIMEOUT", "readback timeout", ExitCodes.OperationFailed);
        };
        client.ResetWriteCounts();
        var interrupted = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
        Assert.Equal("REQUEST_TIMEOUT", interrupted.Code);
        Assert.Equal(1, client.Writes);
        var left = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        var pending = Assert.Single(left.Pending);
        Assert.True(pending.ResponseReceived);
        Assert.Equal(source.Id, pending.Id);
        Assert.Equal("Config", Assert.Single(pending.Members).Key);
        Assert.Equal("$member:target.Enabled", left.Components["source"].ReferenceSelectors!["Target"]);
        client.AfterMutation = null;
        client.BeforeComponentRead = null;

        // The world changes before the next apply: the runtime-owned reference drifts, or its target stops being unique.
        if (drift == "cleared") source.Members["Target"] = new("reference", source.Id + ":Target");
        else if (drift == "retargeted")
            await client.SetComponentMembersAsync(source.Id, source.Type, new Dictionary<string, string> { ["Target"] = owner.Id + ":Rotation" });
        else await client.AddComponentAsync(owner.Id, "Test.Target", new Dictionary<string, string> { ["Enabled"] = "false" });
        var observedTarget = source.Members["Target"].TargetId;
        client.ResetWriteCounts();

        if (drift == "ambiguous")
        {
            // The pending write is still resolved; the following planning stops on the existing ambiguity error.
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
            Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", error.Code);
        }
        else await service.ApplyAsync(desired, options);

        var saved = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        Assert.Empty(saved.Pending);
        AssertNoExitReferenceEvidence(options.StateFile!, document);
        Assert.Equal(source.Id, saved.Components["source"].Id);
        Assert.Equal(target.Id, saved.Components["target"].Id);
        Assert.Equal(0, client.Writes);
        Assert.Equal(observedTarget, source.Members["Target"].TargetId);
        Assert.True(source.Members["Config"].Value!.GetValue<bool>());
    }

    [Theory]
    [InlineData("backward")]
    [InlineData("forward")]
    [InlineData("cycle")]
    [InlineData("slot")]
    public async Task S3VerifiedCreationRetainsForwardBackwardCyclicAndSlotTopology(string topology)
    {
        var document = ExitReferenceDocument("created-topology", null);
        if (topology == "forward") document = document with { Components = [document.Components![1], document.Components[0]] };
        if (topology == "cycle") document = Document("created-topology", """
            [{"key":"source","type":"Test.Source","fields":{"Target":"$component:other"}},
             {"key":"other","type":"Test.OtherSource","fields":{"Target":"$component:source"}}]
            """);
        if (topology == "slot")
        {
            document = Document("created-topology", """
                [{"key":"source","type":"Test.Source","fields":{"Target":"$slot:child"}}]
                """);
            var raw = JsonNode.Parse(File.ReadAllText(document.SourcePath!))!;
            raw["children"] = JsonNode.Parse("""[{"slot":{"key":"child","name":"CreatedChild"}}]""");
            File.WriteAllText(document.SourcePath!, raw.ToJsonString());
            document = ApplyDocument.Load(document.SourcePath!);
        }
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var options = new ApplyOptions(Path.Combine(_root, "created-topology.state.json"));
        await service.ApplyAsync(document, options);
        var expectedSelector = topology switch { "cycle" => "$component:other", "slot" => "$slot:child", _ => "$member:target.Enabled" };
        Assert.Equal(expectedSelector, ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key).Components["source"].ReferenceSelectors!["Target"]);
        client.ResetWriteCounts();

        await service.ApplyAsync(document, options);

        var saved = ApplyStateStore.Load(options.StateFile!, document.Ownership!.Key);
        Assert.Equal(expectedSelector, saved.Components["source"].ReferenceSelectors!["Target"]);
        Assert.Equal(saved.Components["source"].Id, (await service.ResolveStableReferenceAsync(options.StateFile!, "$component:source", "session-1")).Id);
        if (topology == "cycle") Assert.Equal("$component:source", saved.Components["other"].ReferenceSelectors!["Target"]);
        Assert.Equal(0, client.Writes);
    }

    private ApplyDocument ExitReferenceDocument(string ownership, string? mode)
    {
        var document = Document(ownership, """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":false}},
             {"key":"source","type":"Test.Source","fields":{"Target":"$member:target.Enabled","Config":false}}]
            """);
        return mode is null ? document : document with { Components = [document.Components![0], document.Components[1] with
            { PropertyModes = new Dictionary<string, string> { ["Target"] = mode } }] };
    }

    private static ApplyDocument ExitReferenceConfig(ApplyDocument document, bool value)
    {
        var fields = document.Components![1].Fields!.ToDictionary();
        fields["Config"] = JsonSerializer.SerializeToElement(value);
        return document with { Components = [document.Components[0], document.Components[1] with { Fields = fields }] };
    }

    private static void AssertNoExitReferenceEvidence(string path, ApplyDocument document) =>
        Assert.True(ApplyStateStore.Load(path, document.Ownership!.Key).Components["source"].ReferenceSelectors is null or { Count: 0 });
}
