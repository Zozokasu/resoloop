using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task S3SettledDriverAmbiguityExitsThroughExactRepairAndTwoStageReplacement()
    {
        var initial = Document("exit-driver-recovery", """
            [{"key":"target","type":"Test.Target","fields":{"Enabled":true}},
             {"key":"unrelated","type":"Test.Kept","fields":{"Label":"keep me"}},
             {"key":"old","type":"Test.Source","fields":{"Target":"$member:target.Enabled"}}]
            """);
        var assetPath = Path.Combine(_root, "kept.png");
        File.WriteAllBytes(assetPath, [1, 2, 3]); // Fake import only; no image decoder or network.
        initial = initial with
        {
            Assets = new Dictionary<string, ApplyAssetSpec> { ["kept-asset"] = new("texture", assetPath) },
            Children = [new(initial.Slot! with { Name = "Retained", Key = "retained", Parent = null },
                [new("Test.Note", new Dictionary<string, JsonElement>
                    { ["Label"] = JsonSerializer.SerializeToElement("untouched") }, Key: "note")])]
        };
        var client = new FakeResoniteClient(initial) { DiscoverId = "S-recovery-test" };
        var service = new WorldService(client);
        var statePath = Path.Combine(_root, "recovery.state.json");
        var options = new ApplyOptions(statePath, RequireState: true);
        await service.ApplyAsync(initial, options with { RequireState = false });
        var original = ApplyStateStore.Load(statePath, "exit-driver-recovery");
        var ownerId = original.Slots["root"].Id;
        var oldId = original.Components["old"].Id;
        var targetId = original.Components["target"].Id;
        var targetField = (await client.GetComponentAsync(targetId)).Members["Enabled"].Id;
        var unrelatedId = original.Components["unrelated"].Id;
        var noteId = original.Components["note"].Id;
        var unrelatedBefore = JsonSerializer.Serialize(await client.GetComponentAsync(unrelatedId));
        var noteBefore = JsonSerializer.Serialize(await client.GetComponentAsync(noteId));
        var desired = initial with { Components = [initial.Components![0], initial.Components[1],
            new("Test.Source", new Dictionary<string, JsonElement>
                { ["Target"] = JsonSerializer.SerializeToElement("$member:target.Enabled") }, Key: "replacement")] };
        client.TargetClaimedBy = oldId;

        var failure = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired,
            options with { Prune = true, ConfirmDeletes = true }));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", failure.Code);
        Assert.Equal("readbackMismatch", failure.Context["reason"]);
        var pending = Assert.Single(ApplyStateStore.Load(statePath, "exit-driver-recovery").Pending);
        Assert.Equal("replacement", pending.Key);
        var failedId = Assert.IsType<string>(pending.Id);
        Assert.NotEqual(oldId, failedId);
        client.ResetWriteCounts();

        var settled = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
        Assert.Equal("STABLE_COMPONENT_AMBIGUOUS", settled.Code);
        Assert.Equal(0, client.Writes);
        var settledState = ApplyStateStore.Load(statePath, "exit-driver-recovery");
        Assert.Empty(settledState.Pending);
        Assert.Equal(failedId, settledState.Components["replacement"].Id);
        Assert.Equal(oldId, settledState.Components["old"].Id);

        // Observe both exact IDs/types and their actual owning Slot, never select a same-type ordinal.
        var owner = await client.GetSlotAsync(ownerId, 0, true);
        Assert.Equal("Root", owner.ParentId);
        Assert.Contains(owner.Components!, component => component.Id == oldId && component.Type == "Test.Source");
        Assert.Contains(owner.Components!, component => component.Id == failedId && component.Type == "Test.Source");
        Assert.Equal(targetField, (await client.GetComponentAsync(oldId)).Members["Target"].TargetId);
        Assert.Null((await client.GetComponentAsync(failedId)).Members["Target"].TargetId);

        // Byte-for-byte backup precedes both the direct exact-ID removal and the manual record edit.
        var backupPath = Path.Combine(_root, "recovery.before-repair.state.json");
        var beforeRepairBytes = File.ReadAllBytes(statePath);
        File.Copy(statePath, backupPath, overwrite: false);
        Assert.Equal(beforeRepairBytes, File.ReadAllBytes(backupPath));
        var beforeRepair = JsonNode.Parse(beforeRepairBytes)!;
        var mutations = new List<(string Kind, string Id)>();
        client.AfterMutation = (kind, id) => mutations.Add((kind, id));
        // Backend operation of `component remove FAILED_ID --yes`; exact observation authorized it.
        using (await SessionWriteLock.AcquireAsync(client, statePath, CancellationToken.None))
            await client.RemoveComponentAsync(failedId);
        var failedAbsent = await Assert.ThrowsAsync<RLoopException>(() => client.GetComponentAsync(failedId));
        Assert.Equal("COMPONENT_NOT_FOUND", failedAbsent.Code);

        var repair = JsonNode.Parse(File.ReadAllText(statePath))!;
        Assert.Equal(failedId, repair["components"]!["replacement"]!["id"]!.GetValue<string>());
        Assert.Empty((JsonArray)repair["pending"]!);
        Assert.True(((JsonObject)repair["components"]!).Remove("replacement"));
        File.WriteAllText(statePath, repair.ToJsonString());
        var repaired = JsonNode.Parse(File.ReadAllText(statePath))!;
        foreach (var property in (JsonObject)beforeRepair)
            if (property.Key != "components") Assert.True(JsonNode.DeepEquals(property.Value, repaired[property.Key]), property.Key);
        Assert.Equal(((JsonObject)beforeRepair["components"]!).Count - 1, ((JsonObject)repaired["components"]!).Count);
        foreach (var binding in (JsonObject)beforeRepair["components"]!)
            if (binding.Key != "replacement") Assert.True(JsonNode.DeepEquals(binding.Value, repaired["components"]![binding.Key]), binding.Key);
        Assert.Equal(3, repaired["schemaVersion"]!.GetValue<int>());
        Assert.Equal("exit-driver-recovery", repaired["ownershipKey"]!.GetValue<string>());
        Assert.Equal("session-1", repaired["sessionId"]!.GetValue<string>());
        Assert.NotEmpty((JsonObject)repaired["assets"]!);
        Assert.Empty((JsonArray)repaired["pending"]!);

        // Neither driver is declared in this intermediate stage. The deletes-only projection
        // matches the existing CLI filter, with required state and just the inspected old target.
        var intermediate = initial with { Components = [initial.Components![0], initial.Components[1]] };
        var plan = await service.PlanApplyAsync(intermediate, options);
        var deletion = Assert.Single(plan.Operations, operation => operation.Action == "delete");
        Assert.Equal("old", deletion.Key);
        Assert.Equal("component", deletion.Kind);
        Assert.Equal("Test.Source", deletion.Type);
        Assert.DoesNotContain(plan.Changes, operation => operation.Action != "delete");
        var pruned = await service.ApplyAsync(intermediate, options with { Prune = true, ConfirmDeletes = true });
        Assert.Equal(1, pruned.ComponentsDeleted);
        Assert.Equal(0, pruned.SlotsDeleted);
        var oldAbsent = await Assert.ThrowsAsync<RLoopException>(() => client.GetComponentAsync(oldId));
        Assert.Equal("COMPONENT_NOT_FOUND", oldAbsent.Code);
        Assert.Empty(ApplyStateStore.Load(statePath, "exit-driver-recovery").Pending);

        var added = await service.ApplyAsync(desired, options);
        Assert.Equal(1, added.ComponentsAdded);
        Assert.Equal(0, added.ComponentsUpdated);
        var final = ApplyStateStore.Load(statePath, "exit-driver-recovery");
        var replacementId = final.Components["replacement"].Id;
        Assert.NotEqual(oldId, replacementId);
        Assert.NotEqual(failedId, replacementId);
        Assert.Equal("Test.Source", (await client.GetComponentAsync(replacementId)).Type);
        Assert.Contains((await client.GetSlotAsync(ownerId, 0, false)).Components!, component => component.Id == replacementId);
        Assert.Equal(targetField, (await client.GetComponentAsync(replacementId)).Members["Target"].TargetId);
        Assert.Equal("$member:target.Enabled", final.Components["replacement"].ReferenceSelectors!["Target"]);
        Assert.Empty(final.Pending);
        Assert.False(final.Components.ContainsKey("old"));
        Assert.Equal(new[] { ("removeComponent", failedId), ("removeComponent", oldId), ("addComponent", replacementId) }, mutations);
        Assert.Equal(unrelatedBefore, JsonSerializer.Serialize(await client.GetComponentAsync(unrelatedId)));
        Assert.Equal(noteBefore, JsonSerializer.Serialize(await client.GetComponentAsync(noteId)));
        Assert.Equal(original.Components["unrelated"].Id, final.Components["unrelated"].Id);
        Assert.Equal(original.Components["note"].Id, final.Components["note"].Id);
        Assert.Equal(original.Slots["retained"].Id, final.Slots["retained"].Id);
        Assert.Equal(original.Assets["kept-asset"].Url, final.Assets["kept-asset"].Url);
        Assert.Equal(1, client.AssetImports);
        Assert.Equal(beforeRepairBytes, File.ReadAllBytes(backupPath));
    }
}
