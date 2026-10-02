using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;

namespace RLoop.Tests;

public sealed partial class ApplyWorkflowTests
{
    private static IDisposable FailSave(Action<string, ApplyState> fault)
    {
        var previous = ApplyStateStore.SaveFault.Value;
        ApplyStateStore.SaveFault.Value = fault;
        return new RestoreSaveFault(() => ApplyStateStore.SaveFault.Value = previous);
    }
    private sealed class RestoreSaveFault(Action restore) : IDisposable { public void Dispose() => restore(); }

    [Fact]
    public async Task S3CreationReadbackReadsOnlyExactComponentAndStructuralParent()
    {
        var document = Document("i2-bounded", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document);
        string? created = null;
        var reads = new List<string>();
        client.AfterMutation = (kind, id) => { if (kind == "addComponent") created = id; };
        client.BeforeComponentRead = id => { if (created is not null) { Assert.Equal(created, id); reads.Add("component"); } };
        client.BeforeSlotRead = (_, depth, includeMembers) =>
        {
            if (created is null) return;
            Assert.Equal(0, depth);
            Assert.False(includeMembers);
            reads.Add("parent");
        };
        await new WorldService(client).ApplyAsync(document, new(Path.Combine(_root, "bounded.state.json")));
        Assert.Equal(new[] { "component", "parent" }, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3AdapterBoundaryProofControlsUnsentCleanup(bool failCleanup)
    {
        var document = Document("i2-boundary", "[]");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "boundary.state.json");
        await service.ApplyAsync(document, new(path));
        var before = File.ReadAllText(path);
        client.ResetWriteCounts();
        using var cancellation = new CancellationTokenSource();
        client.BeforeWriteBoundary = () => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); };
        var sawIntent = false;
        using (FailSave((_, state) =>
        {
            if (state.Pending.Count > 0) sawIntent = true;
            if (failCleanup && sawIntent && state.Pending.Count == 0) throw new IOException("unsent cleanup save fault");
        }))
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document with { Slot = document.Slot! with { Name = "Renamed" } }, new(path), cancellation.Token));
            Assert.Equal(failCleanup ? "APPLY_STATE_WRITE_FAILED" : "APPLY_CANCELLED", error.Code);
            Assert.Equal("notSentProven", error.Context["sendStatus"]);
            Assert.NotNull(error.Context["operationId"]);
        }
        Assert.Equal(0, client.Writes);
        if (failCleanup) Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        else Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task S3RejectedResponseClearsPendingAndAllowsNextApply()
    {
        var document = Document("i2-reject", "[]");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        client.RejectKind = "createSlot";
        var service = new WorldService(client);
        var path = Path.Combine(_root, "reject.state.json");
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("RESONITE_OPERATION_FAILED", error.Code);
        Assert.Equal("responseReceived", error.Context["sendStatus"]);
        Assert.Empty(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        client.RejectKind = null;
        client.ResetWriteCounts();
        await service.ApplyAsync(document, new(path));
        Assert.Equal(1, client.Writes);
    }

    [Fact]
    public async Task S3FailureAfterReplacementStopsAndRetainsConservativeJournal()
    {
        var document = Document("i2-replaced", "[]");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var path = Path.Combine(_root, "replaced.state.json");
        var service = new WorldService(client);
        var previous = ApplyStateStore.AfterSaveFault.Value;
        try
        {
            ApplyStateStore.AfterSaveFault.Value = (_, state) => { if (state.Pending.Count > 0) throw new IOException("failure after replacement"); };
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
            Assert.Equal(0, client.Writes);
        }
        finally { ApplyStateStore.AfterSaveFault.Value = previous; }
        Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        var resume = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("pendingUnresolved", resume.Context["reason"]);
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3PendingSaveFailureSendsNothingAndPreservesState()
    {
        var document = Document("i2-save", "[]");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "save.state.json");
        await service.ApplyAsync(document, new(path));
        var before = File.ReadAllText(path);
        client.ResetWriteCounts();
        using var fault = FailSave((_, state) => { if (state.Pending.Count > 0) throw new IOException("intent save fault"); });
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document with { Slot = document.Slot! with { Name = "Renamed" } }, new(path)));
        Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
        Assert.Equal("notSentProven", error.Context["sendStatus"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.NotNull(ApplyDiagnostics.ForException(error, "apply").Diagnostics.Last().OperationId);
    }

    [Fact]
    public async Task S3CancelBeforeMutationEntryClearsOnlyProvenUnsentIntent()
    {
        var document = Document("i2-cancel-before", "[]");
        var client = new FakeResoniteClient(document);
        var path = Path.Combine(_root, "before.state.json");
        var service = new WorldService(client);
        await service.ApplyAsync(document, new(path));
        var before = File.ReadAllText(path);
        client.ResetWriteCounts();
        using var cancellation = new CancellationTokenSource();
        using var fault = FailSave((_, state) => { if (state.Pending.Count > 0) cancellation.Cancel(); });
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document with { Slot = document.Slot! with { Name = "Renamed" } }, new(path), cancellation.Token));
        Assert.Equal("APPLY_CANCELLED", error.Code);
        Assert.Equal("notSentProven", error.Context["sendStatus"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("component")]
    public async Task S3LostCreateResponseNeverRecreates(string kind)
    {
        var document = Document("i2-lost-" + kind, """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        client.LoseNextSlotCreateResponse = kind == "slot";
        client.LoseNextComponentCreateResponse = kind == "component";
        var path = Path.Combine(_root, kind + ".state.json");
        var service = new WorldService(client);
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal("responseLost", error.Context["reason"]);
        var saved = ApplyStateStore.Load(path, document.Ownership!.Key);
        var pending = Assert.Single(saved.Pending);
        Assert.Null(pending.Id);
        Assert.False(pending.ResponseReceived);
        Assert.Equal(kind == "slot" ? 1 : 2, client.Writes);
        Assert.Single(client.Root.Children);
        if (kind == "component") { Assert.Single(client.Root.Children[0].Components); Assert.Empty(saved.Components); }
        else Assert.Empty(saved.Slots);
        client.ResetWriteCounts();
        var resume = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("pendingUnresolved", resume.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(pending.OperationId, Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending).OperationId);
    }

    [Theory]
    [InlineData("slot", "cancel")]
    [InlineData("component", "cancel")]
    [InlineData("slot", "timeout")]
    [InlineData("component", "timeout")]
    public async Task S3FailureAfterMutationStopsNextSendAndCannotReplay(string kind, string failure)
    {
        var document = Document("i2-after-" + kind + failure, """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        using var cancellation = new CancellationTokenSource();
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        client.AfterMutation = (operation, _) =>
        {
            if (operation != (kind == "slot" ? "createSlot" : "addComponent")) return;
            if (failure == "cancel") { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            throw new RLoopException("REQUEST_TIMEOUT", "lost response", ExitCodes.OperationFailed);
        };
        var path = Path.Combine(_root, "after.state.json");
        var service = new WorldService(client);
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path), cancellation.Token));
        Assert.Equal(failure == "cancel" ? "APPLY_CANCELLED" : "REQUEST_TIMEOUT", error.Code);
        Assert.Equal(kind == "slot" ? 1 : 2, client.Writes);
        Assert.False(Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending).ResponseReceived);
        client.AfterMutation = null;
        client.ResetWriteCounts();
        var resume = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("APPLY_WRITE_UNVERIFIED", resume.Code);
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("slot", "response")]
    [InlineData("slot", "commit")]
    [InlineData("component", "response")]
    [InlineData("component", "commit")]
    public async Task S3ResultSaveFailureRetainsLastDurablePendingAndStops(string kind, string stage)
    {
        var document = Document("i2-result-" + kind + stage, """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var path = Path.Combine(_root, "result.state.json");
        var service = new WorldService(client);
        var operationKind = kind == "slot" ? "createSlot" : "addComponent";
        string? operation = null;
        using (FailSave((_, state) =>
        {
            var pending = state.Pending.FirstOrDefault(p => p.Kind == operationKind);
            if (pending is not null) operation = pending.OperationId;
            if (stage == "response" && pending?.ResponseReceived == true ||
                stage == "commit" && operation is not null && state.Pending.All(p => p.OperationId != operation))
                throw new IOException("result save fault");
        }))
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
            Assert.Equal("APPLY_STATE_WRITE_FAILED", error.Code);
        }
        var saved = ApplyStateStore.Load(path, document.Ownership!.Key);
        var remaining = Assert.Single(saved.Pending);
        Assert.Equal(stage == "commit", remaining.ResponseReceived);
        Assert.Equal(stage == "commit", remaining.Id is not null);
        Assert.Equal(kind == "slot" ? 1 : 2, client.Writes);
        Assert.Single(client.Root.Children);
        if (kind == "slot") Assert.Empty(saved.Slots); else Assert.Empty(saved.Components);
        client.ResetWriteCounts();
        if (stage == "response")
        {
            var resume = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
            Assert.Equal("pendingUnresolved", resume.Context["reason"]);
            Assert.Equal(0, client.Writes);
        }
        else
        {
            await service.ApplyAsync(document, new(path));
            Assert.Empty(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
            Assert.Single(client.Root.Children);
            Assert.Single(client.Root.Children[0].Components);
            Assert.Equal(kind == "slot" ? 1 : 0, client.Writes);
        }
    }

    [Theory]
    [InlineData("matched", "matched", true)]
    [InlineData("unknown", "matched", false)]
    [InlineData("matched", "unknown", false)]
    [InlineData("unknown", "unknown", false)]
    [InlineData("matched", "different", false)]
    public async Task S3ExactResponseIdResumesOnlyWithProvenIdentity(string before, string after, bool canResume)
    {
        var document = Document("i2-identity", "[]");
        using var cancellation = new CancellationTokenSource();
        var client = new FakeResoniteClient(document) { DiscoverId = before == "matched" ? "S-test" : null,
            CancelAfterWrites = 1, Cancellation = cancellation };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "identity.state.json");
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path), cancellation.Token));
        var p = Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        Assert.True(p.ResponseReceived);
        Assert.NotNull(p.Id);
        client.DiscoverId = after == "matched" ? "S-test" : after == "different" ? "S-other" : null;
        client.CancelAfterWrites = null;
        client.SessionId = "reconnected-counter";
        client.Generation = "new-generation";
        client.ResetWriteCounts();
        if (canResume) { await service.ApplyAsync(document, new(path)); Assert.Empty(ApplyStateStore.Load(path, document.Ownership.Key).Pending); }
        else
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
            Assert.Equal("identityUnproven", error.Context["reason"]);
            Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        }
        Assert.Equal(0, client.Writes);
        Assert.Single(client.Root.Children);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("parent")]
    [InlineData("value")]
    [InlineData("owner")]
    public async Task S3ResumeReobservesExactResponseTypeParentValuesAndOwner(string drift)
    {
        var document = Document("i2-resume-proof", """[{"key":"c","type":"Test.Target","fields":{"Enabled":true}}]""");
        using var cancellation = new CancellationTokenSource();
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test", CancelAfterWrites = 2, Cancellation = cancellation };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "proof.state.json");
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path), cancellation.Token));
        var slot = client.Root.Children[0];
        var component = slot.Components[0];
        switch (drift)
        {
            case "type": component.Type = "Test.Other"; break;
            case "parent": slot.Components.Remove(component); client.Root.Components.Add(component); break;
            case "value": component.Members["Enabled"] = component.Members["Enabled"] with { Value = JsonValue.Create(false) }; break;
            case "owner": slot.Name = "External"; break;
        }
        client.CancelAfterWrites = null;
        client.ResetWriteCounts();
        if (drift == "value")
        {
            var result = await service.ApplyAsync(document, new(path));
            Assert.Contains(ApplyDiagnostics.ForRuntime(result).Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED");
            Assert.Equal(1, client.Writes);
            Assert.Empty(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
            return;
        }
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("pendingUnresolved", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S3PartialBatchRetainsOnlyUnconfirmedMembersAndNeverResends(bool failPartialSave)
    {
        var document = Document("i2-partial", """[{"key":"c","type":"Test.Source","fields":{"Target":"$component:t"}},{"key":"t","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "partial.state.json");
        await service.ApplyAsync(document, new(path));
        var source = client.Root.Children[0].Components.First(c => c.Type == "Test.Source");
        // Two actual members in one update; one retains the sent value, the other rejects it.
        client.AfterMutation = (kind, id) => { if (kind == "setMembers" && id == source.Id) source.Members["Target"] = source.Members["Target"] with { TargetId = "rejected" }; };
        var desired = document with { Components = [document.Components![0] with { Fields = new Dictionary<string, JsonElement>
            { ["Target"] = JsonSerializer.SerializeToElement("null") } }, document.Components[1]] };
        // Register an extra writable field through the existing fake definition seam.
        client.RegisterDefinitions(desired with { Components = [desired.Components[0] with { Fields = new Dictionary<string, JsonElement>
            { ["Target"] = JsonSerializer.SerializeToElement("null"), ["Accepted"] = JsonSerializer.SerializeToElement(true) } }] });
        source.Members["Accepted"] = new("field", source.Id + ":Accepted", "value", JsonValue.Create(false));
        desired = desired with { Components = [desired.Components[0] with { Fields = new Dictionary<string, JsonElement>
            { ["Target"] = JsonSerializer.SerializeToElement("null"), ["Accepted"] = JsonSerializer.SerializeToElement(true) } }, desired.Components[1]] };
        client.ResetWriteCounts();
        using (FailSave((_, state) => { if (failPartialSave && state.Pending.Any(p => p.Confirmed.Contains("Accepted"))) throw new IOException("partial save fault"); }))
        {
            var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path)));
            Assert.Equal(failPartialSave ? "APPLY_STATE_WRITE_FAILED" : "APPLY_WRITE_UNVERIFIED", error.Code);
            Assert.NotNull(error.Context["operationId"]);
        }
        var p = Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        Assert.Equal(1, client.Writes);
        Assert.Equal(failPartialSave ? Array.Empty<string>() : new[] { "Accepted" }, p.Confirmed);
        Assert.True(p.ResponseReceived);
        client.ResetWriteCounts();
        var resume = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path)));
        Assert.Equal("readbackMismatch", resume.Context["reason"]);
        Assert.Equal(1, client.Writes);
        Assert.Contains(ApplyDiagnostics.ForException(resume, "apply").Diagnostics, d => d.Code == "APPLY_PENDING_RESOLVED_NOT_APPLIED");
        Assert.True(source.Members["Accepted"].Value!.GetValue<bool>());
        var retried = Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        Assert.NotEqual(p.OperationId, retried.OperationId);
        Assert.DoesNotContain("Accepted", retried.Members.Keys);
    }

    [Theory]
    [InlineData(1, "slot")]
    [InlineData(2, "slot")]
    [InlineData(1, "component")]
    [InlineData(2, "component")]
    public async Task S3V1V2EmptyIdsRemainUnprovedLegacyCreations(int version, string kind)
    {
        var document = Document("i2-legacy", "[]");
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "legacy.state.json");
        await service.ApplyAsync(document, new(path));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["schemaVersion"] = version;
        if (kind == "slot") json["slots"]!["root"]!["id"] = "";
        else json["components"]!["legacy"] = JsonSerializer.SerializeToNode(new { id = "", slotKey = "root", type = "Test.Target", typeOrdinal = 0 });
        File.WriteAllText(path, json.ToJsonString());
        var before = File.ReadAllText(path);
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal("pendingUnresolved", error.Context["reason"]);
        Assert.Equal(0, client.Writes);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void S3V1V2ReadSaveV3AndOldReaderRejectsIt(int version)
    {
        var path = Path.Combine(_root, "versions.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = version, ownershipKey = "versions", slots = new { }, components = new { } }));
        var state = ApplyStateStore.Load(path, "versions");
        Assert.Equal(version, state.SchemaVersion);
        ApplyStateStore.Save(path, state);
        Assert.Equal(3, ApplyStateStore.Load(path, "versions").SchemaVersion);
        // The pre-I2 reader's version gate accepted exactly 1 or 2; the same gate is parameterized here.
        var error = Assert.Throws<RLoopException>(() => ApplyStateStore.RequireSupportedVersion(3, path, maximumVersion: 2));
        Assert.Equal("APPLY_STATE_VERSION_UNSUPPORTED", error.Code);
        ApplyStateStore.Save(path, state);
        Assert.Equal(3, ApplyStateStore.Load(path, "versions").SchemaVersion);
    }

    [Fact]
    public async Task S3PendingSurvivesCacheAndBundleRemovalAndReportsExactEvidence()
    {
        var document = Document("i2-cache", "[]");
        var client = new FakeResoniteClient(document) { LoseNextSlotCreateResponse = true };
        var path = Path.Combine(_root, "cache.state.json");
        var service = new WorldService(client);
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        var bytes = File.ReadAllText(path);
        // Disposable cache/bundle files are independent of the authoritative state file.
        var cache = Path.Combine(_root, "type-cache.json"); var bundle = Path.Combine(_root, "generated-bundle.json");
        File.WriteAllText(cache, "{}"); File.WriteAllText(bundle, "{}");
        File.Delete(cache); File.Delete(bundle);
        Assert.Equal(bytes, File.ReadAllText(path));
        Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        var diagnostic = ApplyDiagnostics.ForException(error, "apply").Diagnostics.Last();
        Assert.NotNull(diagnostic.OperationId);
        Assert.Equal("possiblySent", diagnostic.SendStatus);
        Assert.NotNull(diagnostic.Pending);
        Assert.NotNull(diagnostic.EvidencePersistence);
        Assert.NotNull(diagnostic.ConfirmedBindings);
        client.ResetWriteCounts();
        await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(document, new(path)));
        Assert.Equal(0, client.Writes);
    }

    [Fact]
    public async Task S3ReadbackNormalizationBecomesTheNextStrictPrecondition()
    {
        var initial = Document("i2-normalized", "[]");
        var desired = Document("i2-normalized", """
            [{"key":"source","type":"Test.Source","fields":{"Data":{"amount":1.0,"target":"$slot-member:root.Rotation"}}},
             {"key":"trigger","type":"Test.Trigger"}]
            """);
        var client = new FakeResoniteClient(desired);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "normalization.state.json");
        await service.ApplyAsync(initial, new(path));
        client.AfterMutation = (kind, id) =>
        {
            var c = client.Root.Children[0].Components.SingleOrDefault(c => c.Id == id);
            if (c?.Type == "Test.Source" && c.Members.TryGetValue("Data", out var raw) && raw.Value is JsonObject obj)
                c.Members["Data"] = new("syncObject", id + ":Data", Members: new Dictionary<string, MemberValue>
                {
                    ["amount"] = new("field", id + ":amount", "float", JsonValue.Create(1.000001)),
                    ["target"] = new("reference", id + ":target", TargetId: obj["target"]!.GetValue<string>()),
                });
            // Simulate a late field-ID observation that requires the source's aggregate member to be sent again.
            if (kind == "addComponent" && c?.Type == "Test.Trigger")
                client.Root.Children[0].RotationFieldId = "new-observed-field-id";
        };
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, new(path));
        Assert.Equal(1, client.BatchUpdates); // Creation readback preceded this strict check and update.
        var source = client.Root.Children[0].Components.Single(c => c.Type == "Test.Source");
        Assert.Equal(1.000001, source.Members["Data"].Members!["amount"].Value!.GetValue<double>());
        Assert.Equal("new-observed-field-id", source.Members["Data"].Members!["target"].TargetId);
        client.ResetWriteCounts();
        await service.ApplyAsync(desired, new(path));
        Assert.Equal(0, client.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task S3ReadbackFailureKeepsResponseIdAndStopsWithoutResend(string fault)
    {
        var document = Document("i2-readback", """[{"key":"c","type":"Test.Target","fields":{"Enabled":false}}]""");
        var client = new FakeResoniteClient(document) { DiscoverId = "S-test" };
        var service = new WorldService(client);
        var path = Path.Combine(_root, "readback.state.json");
        await service.ApplyAsync(document, new(path));
        var c = client.Root.Children[0].Components[0];
        using var cancellation = new CancellationTokenSource();
        client.AfterMutation = (kind, _) =>
        {
            if (kind != "setMembers") return;
            if (fault == "missing") c.Members.Remove("Enabled");
            else if (fault == "cancel") cancellation.Cancel();
            else client.BeforeComponentRead = _ => throw new RLoopException("REQUEST_TIMEOUT", "readback timeout", ExitCodes.OperationFailed);
        };
        var desired = document with { Components = [document.Components![0] with { Fields = new Dictionary<string, JsonElement>
            { ["Enabled"] = JsonSerializer.SerializeToElement(true) } }] };
        client.ResetWriteCounts();
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path), cancellation.Token));
        Assert.Equal(fault == "cancel" ? "APPLY_CANCELLED" : fault == "timeout" ? "REQUEST_TIMEOUT" : "APPLY_WRITE_UNVERIFIED", error.Code);
        Assert.Equal(1, client.Writes);
        var pending = Assert.Single(ApplyStateStore.Load(path, document.Ownership!.Key).Pending);
        Assert.True(pending.ResponseReceived);
        Assert.Equal(c.Id, pending.Id);
        Assert.Empty(pending.Confirmed);
    }

    [Theory]
    [InlineData("slot")]
    [InlineData("component")]
    public async Task S3DeletionOnlyExactNotFoundConfirmsAbsence(string kind)
    {
        var document = Document("i2-delete", kind == "component" ? """[{"key":"c","type":"Test.Target"}]""" : "[]");
        if (kind == "slot") document = document with { Children = [new(document.Slot! with { Name = "Child", Key = "child", Parent = null }, [], [])] };
        var client = new FakeResoniteClient(document);
        var service = new WorldService(client);
        var path = Path.Combine(_root, "delete.state.json");
        await service.ApplyAsync(document, new(path));
        var saved = ApplyStateStore.Load(path, document.Ownership!.Key);
        var id = kind == "slot" ? saved.Slots["child"].Id : saved.Components["c"].Id;
        if (kind == "slot") client.SlotReadFailures[id] = new RLoopException("RESONITE_OPERATION_FAILED", "not an exact absence", ExitCodes.OperationFailed);
        else client.BeforeComponentRead = readId => { if (readId == id) throw new RLoopException("RESONITE_OPERATION_FAILED", "not an exact absence", ExitCodes.OperationFailed); };
        var desired = document with { Components = [], Children = [] };
        var error = await Assert.ThrowsAsync<RLoopException>(() => service.ApplyAsync(desired, new(path, Prune: true, ConfirmDeletes: true)));
        Assert.Equal("RESONITE_OPERATION_FAILED", error.Code);
        var pending = Assert.Single(ApplyStateStore.Load(path, document.Ownership.Key).Pending);
        Assert.True(pending.ResponseReceived);
        Assert.Equal(id, pending.Id);
        Assert.Empty(pending.Confirmed);
        if (kind == "slot") Assert.True(ApplyStateStore.Load(path, document.Ownership.Key).Slots.ContainsKey("child"));
        else Assert.True(ApplyStateStore.Load(path, document.Ownership.Key).Components.ContainsKey("c"));
    }
}
