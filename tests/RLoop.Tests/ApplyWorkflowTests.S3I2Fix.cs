using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Cli;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    [Fact]
    public async Task F9ResolutionSaveFailureKeepsPendingAndSendsNothing()
    {
        var document = Document("f9-resolution-save", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "resolution-save.json");
        client.AfterMutation = (kind, _) =>
        {
            if (kind == "addComponent") client.Root.Children[0].Components[0].Members["Enabled"] =
                client.Root.Children[0].Components[0].Members["Enabled"] with { Value = JsonValue.Create(false) };
        };
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        var before = File.ReadAllText(path);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        using (FailSave((_, state) => { if (state.Pending.Count == 0) throw new IOException("resolution save failed"); }))
            Assert.Equal("APPLY_STATE_WRITE_FAILED", (await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)))).Code);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(0, client.Writes);
        await service.ApplyAsync(document, new(path));
        Assert.Empty(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
    }

    [Fact]
    public async Task F10DiscardLostCreationDoesNotAdoptCandidateAndAllowsNewCreation()
    {
        var document = Document("f10-lost", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { LoseNextComponentCreateResponse = true };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "lost-discard.json");
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        var state = ApplyStateStore.Load(path, document.Ownership!.Key);
        var pending = Assert.Single(state.Pending);
        Assert.Null(pending.Id);
        ApplyPendingDiscard.Discard(document, pending.OperationId, true, path);
        Assert.False(ApplyStateStore.Load(path, document.Ownership.Key).Components.ContainsKey("c"));
        client.ResetWriteCounts();
        await service.ApplyAsync(document, new(path));
        Assert.Equal(1, client.Writes);
        Assert.Equal(2, client.Root.Children[0].Components.Count);
        Assert.Equal(client.Root.Children[0].Components[1].Id, ApplyStateStore.Load(path, document.Ownership.Key).Components["c"].Id);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("position")]
    public async Task F9SlotMismatchUsesObservedBindingAndReplans(string attribute)
    {
        var document = Document("f9-slot", "[]");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var path = Path.Combine(_root, "slot.json");
        var service = new WorldService(client);
        await service.ApplyAsync(document, new(path));
        var originalId = client.Root.Children[0].Id;
        var desired = document with { Slot = document.Slot! with { Name = attribute == "name" ? "Renamed" : "Managed", Position = [1, 2, 3] } };
        client.AfterMutation = (kind, _) =>
        {
            if (kind != "updateSlot") return;
            if (attribute == "name") client.Root.Children[0].Name = "Managed";
            else client.Root.Children[0].Position = new(0, 0, 0);
        };
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path)));
        client.AfterMutation = null;
        client.ResetWriteCounts();
        var result = await service.ApplyAsync(desired, new(path));
        Assert.Equal(1, client.Writes);
        Assert.Equal(originalId, result.SlotId);
        Assert.Contains(ApplyDiagnostics.ForRuntime(result).Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED" && d.Member == attribute);
        Assert.Empty(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F9RejectedPartialUpdateReadbackIsDiagnosticOnly(bool readbackFails)
    {
        var document = Document("f9-partial-reject", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "partial-reject.json");
        await service.ApplyAsync(document, new(path));
        var before = ApplyStateStore.Load(path, document.Ownership!.Key).Components["c"];
        var reads = 0;
        client.AfterMutation = (kind, _) =>
        {
            if (kind != "setMembers") return;
            client.ApplyResponseReceived = true;
            client.ApplyResponseAccepted = false;
            client.BeforeComponentRead = _ => { reads++; if (readbackFails) throw new IOException("diagnostic read failed"); };
            throw new RLoopException("RESONITE_OPERATION_FAILED", "partial rejection", ExitCodes.OperationFailed);
        };
        var desired = document with { Components = [document.Components![0] with { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] };
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path)));
        Assert.Equal("RESONITE_OPERATION_FAILED", error.Code);
        Assert.Equal(1, reads);
        var after = ApplyStateStore.Load(path, document.Ownership.Key);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after.Components["c"]));
        Assert.Empty(after.Pending);
        if (!readbackFails) Assert.True(Assert.IsAssignableFrom<JsonNode>(error.Context["observed"])["members"]!["Enabled"]!["value"]!.GetValue<bool>());
        client.AfterMutation = null;
        client.BeforeComponentRead = null;
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, new(path));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task F10DiscardSaveFailureKeepsOriginalState()
    {
        var document = Document("f10-save", "[]");
        var path = Path.Combine(_root, "discard-save.json");
        var state = new ApplyState { OwnershipKey = document.Ownership!.Key };
        var pending = new ApplyPendingWrite { Key = "root", Kind = "createSlot", OwnershipKey = state.OwnershipKey };
        state.Pending.Add(pending);
        ApplyStateStore.Save(path, state);
        var before = File.ReadAllText(path);
        Assert.Equal("CONFIRMATION_REQUIRED", Assert.Throws<RLoopException>(() => ApplyPendingDiscard.Discard(document, pending.OperationId, false, path)).Code);
        using (FailSave((_, _) => throw new IOException("discard save failed")))
            Assert.Equal("APPLY_STATE_WRITE_FAILED", Assert.Throws<RLoopException>(() => ApplyPendingDiscard.Discard(document, pending.OperationId, true, path)).Code);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("createSlot", false)]
    [InlineData("addComponent", false)]
    [InlineData("updateSlot", false)]
    [InlineData("setMembers", false)]
    [InlineData("deleteSlot", false)]
    [InlineData("removeComponent", false)]
    [InlineData("importAsset", false)]
    [InlineData("createSlot", true)]
    [InlineData("updateSlot", true)]
    [InlineData("setMembers", true)]
    [InlineData("deleteSlot", true)]
    [InlineData("importAsset", true)]
    public async Task F9RejectedRequestsClearOnlyPendingOrRetainItOnSaveFailure(string kind, bool failCleanup)
    {
        var document = Document("f9-reject", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "rejected.json");
        var desired = document;
        if (kind != "createSlot") await service.ApplyAsync(document, new(path));
        switch (kind)
        {
            case "addComponent": desired = document with { Components = [.. document.Components!, document.Components![0] with { Key = "new" }] }; break;
            case "updateSlot": desired = document with { Slot = document.Slot! with { Position = [1, 2, 3] } }; break;
            case "setMembers": desired = document with { Components = [document.Components![0] with { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] }; break;
            case "removeComponent": desired = document with { Components = [] }; break;
            case "deleteSlot":
                document = document with { Children = [new(document.Slot! with { Name = "Child", Key = "child", Parent = null }, [])] };
                client.RegisterDefinitions(document);
                await service.ApplyAsync(document, new(path));
                desired = document with { Children = [] };
                break;
            case "importAsset":
                var assetPath = Path.Combine(_root, "texture.bin");
                File.WriteAllText(assetPath, "image");
                desired = document with { Assets = new Dictionary<string, ApplyAssetSpec> { ["texture"] = new("texture", assetPath) } };
                break;
        }
        var confirmed = ApplyStateStore.Load(path, document.Ownership!.Key);
        var sent = false;
        client.RejectKind = kind;
        client.ResetWriteCounts();
        var options = new ApplyOptions(path, Prune: kind is "deleteSlot" or "removeComponent", ConfirmDeletes: true);
        using (FailSave((_, state) =>
        {
            if (state.Pending.Count > 0) sent = true;
            if (failCleanup && sent && state.Pending.Count == 0) throw new IOException("cleanup failure");
        }))
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, options));
            Assert.Equal(failCleanup ? "APPLY_STATE_WRITE_FAILED" : "RESONITE_OPERATION_FAILED", error.Code);
            if (kind is "updateSlot" or "setMembers")
            {
                var observed = Assert.IsAssignableFrom<JsonNode>(error.Context["observed"]);
                Assert.Equal(kind == "setMembers" ? client.Root.Children[0].Components[0].Id : client.Root.Children[0].Id,
                    observed["id"]!.GetValue<string>());
                Assert.Equal("complete", Assert.IsType<ApplyPendingWrite>(error.Context["pending"]).Completeness["readback"]);
                Assert.Contains(ApplyDiagnostics.ForException(error, "apply").Diagnostics, d => d.Observed.Status == "known");
            }
        }
        var stateAfter = ApplyStateStore.Load(path, document.Ownership.Key);
        Assert.Equal(confirmed.Components.Keys, stateAfter.Components.Keys);
        Assert.Equal(confirmed.Slots.Values.Select(s => s.Id), stateAfter.Slots.Values.Select(s => s.Id));
        if (failCleanup) Assert.Single(stateAfter.Pending);
        else
        {
            Assert.Empty(stateAfter.Pending);
            client.RejectKind = null;
            await service.ApplyAsync(desired, options);
            Assert.Empty(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F9NextApplyResolvesMismatchAndPlansCurrentDeclaration(bool corrected)
    {
        var document = Document("f9-value", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var path = Path.Combine(_root, "value.json");
        var service = new WorldService(client);
        client.AfterMutation = (kind, _) =>
        {
            if (kind == "addComponent") client.Root.Children[0].Components[0].Members["Enabled"] =
                client.Root.Children[0].Components[0].Members["Enabled"] with { Value = JsonValue.Create(false) };
        };
        var first = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("readbackMismatch", first.Context["reason"]);
        var pending = Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        var desired = corrected ? document with { Components = [document.Components![0] with { Fields = new Dictionary<string, JsonElement> { ["Enabled"] = JsonSerializer.SerializeToElement(false) } }] } : document;
        var result = await service.ApplyAsync(desired, new(path));
        var diagnostic = Assert.Single(ApplyDiagnostics.ForRuntime(result).Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED");
        Assert.Equal("warning", diagnostic.Severity);
        Assert.Equal(pending.OperationId, diagnostic.OperationId);
        Assert.Equal("c", diagnostic.Key);
        Assert.Equal("Enabled", diagnostic.Member);
        Assert.Equal("true", diagnostic.Expected.Value);
        Assert.False(Assert.IsAssignableFrom<JsonNode>(diagnostic.Observed.Value)["value"]!.GetValue<bool>());
        Assert.Equal(corrected ? 0 : 1, client.Writes);
        Assert.Equal(pending.Id, ApplyStateStore.Load(path, document.Ownership.Key).Components["c"].Id);
        Assert.Empty(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
    }

    [Theory]
    [InlineData("deleteSlot", false)]
    [InlineData("deleteSlot", true)]
    [InlineData("removeComponent", false)]
    [InlineData("removeComponent", true)]
    public async Task F9PresentDeletionTargetResolvesAndOnlyExplicitPruneDeletes(string kind, bool prune)
    {
        var document = Document("f9-delete", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        document = document with { Children = [new(document.Slot! with { Name = "Child", Key = "child", Parent = null }, [])] };
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "delete.json");
        await service.ApplyAsync(document, new(path));
        var state = ApplyStateStore.Load(path, document.Ownership!.Key);
        var root = client.Root.Children[0];
        var target = kind == "deleteSlot" ? root.Children[0].Id : root.Components[0].Id;
        var key = kind == "deleteSlot" ? "child" : "c";
        state.Pending.Add(new() { Kind = kind, Key = key, Id = target, Type = kind == "deleteSlot" ? "Slot" : "Test.Target",
            ParentId = root.Id, OwnershipKey = state.OwnershipKey, Session = client.ObserveApplySession(),
            ResponseReceived = true, ResponseAccepted = true, SendStatus = "responseReceived",
            OwnershipSlots = new() { [root.Id] = new(root.Id, root.Name, "Root") },
            RemoveSlots = kind == "deleteSlot" ? [key] : [], RemoveComponents = kind == "removeComponent" ? [key] : [] });
        ApplyStateStore.Save(path, state);
        var desired = kind == "deleteSlot" ? document with { Children = [] } : document with { Components = [] };
        client.ResetWriteCounts();
        var result = await service.ApplyAsync(desired, new(path, Prune: prune, ConfirmDeletes: prune));
        Assert.Contains(ApplyDiagnostics.ForRuntime(result).Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED" && d.Member == "absence");
        Assert.Equal(prune ? 1 : 0, client.Writes);
        var after = ApplyStateStore.Load(path, document.Ownership.Key);
        Assert.Empty(after.Pending);
        Assert.Equal(!prune, kind == "deleteSlot" ? after.Slots.ContainsKey(key) : after.Components.ContainsKey(key));
    }

    [Theory]
    [InlineData("unknownIdentity")]
    [InlineData("unknownId")]
    [InlineData("unaccepted")]
    [InlineData("absent")]
    [InlineData("type")]
    [InlineData("parent")]
    [InlineData("unreadMember")]
    public async Task F9UnprovedTargetCannotResolve(string failure)
    {
        var document = Document("f9-unproved", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "unproved.json");
        await service.ApplyAsync(document, new(path));
        var state = ApplyStateStore.Load(path, document.Ownership!.Key);
        var root = client.Root.Children[0];
        var component = root.Components[0];
        var pending = new ApplyPendingWrite { Kind = "setMembers", Key = "c", Id = component.Id, Type = component.Type,
            ParentId = root.Id, OwnershipKey = state.OwnershipKey, Session = client.ObserveApplySession(),
            ResponseReceived = true, ResponseAccepted = true, Members = new() { ["Enabled"] = "false" },
            OwnershipSlots = new() { [root.Id] = new(root.Id, root.Name, "Root") } };
        switch (failure)
        {
            case "unknownIdentity": pending.Session = new("ws://fake/", null, "unknown"); break;
            case "unknownId": pending.Id = null; break;
            case "unaccepted": pending.ResponseAccepted = false; break;
            case "absent": await client.RemoveComponentAsync(component.Id); break;
            case "type": component.Type = "Test.Other"; break;
            case "parent": root.Components.Remove(component); client.Root.Components.Add(component); break;
            case "unreadMember": component.Members.Remove("Enabled"); break;
        }
        state.Pending.Add(pending);
        ApplyStateStore.Save(path, state);
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(0, client.Writes);
        Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        Assert.Contains(error.Suggestions, s => s.Contains("--discard-pending") && s.Contains(pending.OperationId));
        Assert.Contains(ApplyDiagnostics.ForException(error, "apply").Diagnostics, d => d.OperationId == pending.OperationId && d.Message.Contains("--discard-pending"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F10DiscardIsOfflineKeepsConfirmedBindingsAndReportsRisk(bool knownId)
    {
        var document = Document("f10-discard", "[]");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "discard.json");
        await service.ApplyAsync(document, new(path));
        var state = ApplyStateStore.Load(path, document.Ownership!.Key);
        var pending = new ApplyPendingWrite { Kind = "addComponent", Key = "unconfirmed", OwnershipKey = state.OwnershipKey,
            Id = knownId ? "C-exact" : null, ComponentBinding = new("C-candidate", "root", "Test.Target", 0) };
        state.Pending.Add(pending);
        var other = new ApplyPendingWrite { Kind = "createSlot", Key = "other", OwnershipKey = state.OwnershipKey };
        state.Pending.Add(other);
        ApplyStateStore.Save(path, state);
        var before = JsonNode.Parse(File.ReadAllText(path))!;
        var report = Path.Combine(_root, "discard.ndjson");
        var connects = 0;
        client.ResetWriteCounts();
        Assert.Equal(0, await Program.RunAsync(["apply", document.SourcePath!, "--state", path, "--discard-pending", pending.OperationId, "--yes", "--report", report],
            _ => { connects++; throw new InvalidOperationException("must not connect"); }));
        Assert.Equal(0, connects);
        Assert.Equal(0, client.Writes);
        var after = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var dictionary in new[] { "slots", "components", "assets" }) Assert.True(JsonNode.DeepEquals(before[dictionary], after[dictionary]));
        Assert.Equal(other.OperationId, Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending).OperationId);
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(pending.Kind, data.GetProperty("kind").GetString());
        Assert.Equal(pending.Key, data.GetProperty("key").GetString());
        Assert.Equal(pending.Id, data.GetProperty("id").GetString());
        Assert.Contains("outside management", data.GetProperty("warning").GetString());
        Assert.Contains("create the same object again", data.GetProperty("warning").GetString());
        ApplyPendingDiscard.Discard(document, other.OperationId, true, path);
        await service.ApplyAsync(document, new(path));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("noYes", "CONFIRMATION_REQUIRED")]
    [InlineData("unknown", "INVALID_OPTION")]
    [InlineData("prune", "INVALID_OPTION")]
    [InlineData("adopt", "INVALID_OPTION")]
    [InlineData("wrongCommand", "INVALID_OPTION")]
    [InlineData("missingValue", "OPTION_REQUIRED")]
    [InlineData("busy", "APPLY_STATE_BUSY")]
    public async Task F10DiscardRejectsInvalidRequestsBeforeConnecting(string mode, string code)
    {
        var document = Document("f10-invalid", "[]");
        var path = Path.Combine(_root, "invalid.json");
        var state = new ApplyState { OwnershipKey = document.Ownership!.Key };
        var pending = new ApplyPendingWrite { Kind = "createSlot", Key = "root", OwnershipKey = state.OwnershipKey };
        state.Pending.Add(pending);
        ApplyStateStore.Save(path, state);
        var before = File.ReadAllText(path);
        var report = Path.Combine(_root, "invalid.ndjson");
        var args = new List<string> { mode == "wrongCommand" ? "diff" : "apply", document.SourcePath!, "--state", path,
            "--discard-pending" };
        if (mode != "missingValue") args.Add(mode == "unknown" ? "missing" : pending.OperationId);
        if (mode != "noYes") args.Add("--yes");
        if (mode is "prune" or "adopt") args.Add("--" + mode);
        args.AddRange(["--report", report]);
        using var locked = mode == "busy" ? CheckpointFiles.AcquireWriter(path) : null;
        var connects = 0;
        Assert.NotEqual(0, await Program.RunAsync(args.ToArray(), _ => { connects++; throw new InvalidOperationException(); }));
        Assert.Equal(0, connects);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Contains(code, File.ReadAllText(report));
    }

    [Theory]
    [InlineData(1, "slot")]
    [InlineData(2, "slot")]
    [InlineData(1, "component")]
    [InlineData(2, "component")]
    public async Task F10LegacyEmptyIdsHaveStableDiscardIds(int version, string kind)
    {
        var document = Document("f10-legacy", "[]");
        var client = new FakeResoniteClient(document);
        var path = Path.Combine(_root, "legacy-discard.json");
        await new WorldService(client).ApplyAsync(document, new(path));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["schemaVersion"] = version;
        var key = "legacy / 日本語:%";
        if (kind == "slot") json["slots"]![key] = JsonSerializer.SerializeToNode(new { id = "", path = "Root/Missing" });
        else json["components"]![key] = JsonSerializer.SerializeToNode(new { id = "", slotKey = "root", type = "Test.Target", typeOrdinal = 0 });
        File.WriteAllText(path, json.ToJsonString());
        var service = new WorldService(client);
        var first = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        var second = await Assert.ThrowsAsync<RLoopException>(() => service.PlanApplyAsync(document, new(path)));
        var id = "legacy:" + kind + ":" + Uri.EscapeDataString(key);
        Assert.Equal(id, first.Context["operationId"]);
        Assert.Equal(id, second.Context["operationId"]);
        var result = ApplyPendingDiscard.Discard(document, id, true, path);
        Assert.Null(result.Id);
        var state = ApplyStateStore.Load(path, document.Ownership!.Key);
        Assert.Equal(3, state.SchemaVersion);
        Assert.False(kind == "slot" ? state.Slots.ContainsKey(key) : state.Components.ContainsKey(key));
        client.ResetWriteCounts();
        await service.ApplyAsync(document, new(path));
        Assert.Equal(0, client.Writes);
    }
}
