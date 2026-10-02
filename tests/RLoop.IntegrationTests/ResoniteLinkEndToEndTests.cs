using RLoop.Core;
using RLoop.ResoniteLink;

namespace RLoop.IntegrationTests;

[Collection("Live world writes")]
public sealed class ResoniteLinkEndToEndTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReparentsIsolatedSlotWithoutChangingItsId()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var name = "ResoLoop_Test_Reparent_" + Guid.NewGuid().ToString("N")[..8];
        var directory = CreateEvidenceDirectory(name);
        await WriteEvidenceAsync(directory, "input", new { create = new SlotCreateRequest("Root", name), reparent = "Source/Moved to Destination" });
        string? rootId = null;
        Exception? scenarioFailure = null;
        try
        {
            rootId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name));
            await WriteEvidenceAsync(directory, "identity", new { rootId, name });
            var sourceId = await client.CreateSlotAsync(new SlotCreateRequest(rootId, "Source"));
            var destinationId = await client.CreateSlotAsync(new SlotCreateRequest(rootId, "Destination"));
            var movedId = await client.CreateSlotAsync(new SlotCreateRequest(sourceId, "Moved", new Vector3Value(1, 2, 3)));

            await client.UpdateSlotAsync(new SlotUpdateRequest(movedId, ParentId: destinationId));

            var source = await client.GetSlotAsync(sourceId, 1, false);
            var destination = await client.GetSlotAsync(destinationId, 1, false);
            await WriteEvidenceAsync(directory, "reparent-observations", new { source, destination, movedId });
            var moved = Assert.Single(destination.Children);
            await WriteEvidenceAsync(directory, "observations", new { source, destination, moved });
            Assert.Empty(source.Children);
            Assert.Equal(movedId, moved.Id);
            Assert.Equal(destinationId, moved.ParentId);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, rootId, name, directory, scenarioFailure);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyRelocatesOwnershipRootAndPrunesItsOldChild()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var containerName = "ResoLoop_Test_RootMove_" + suffix;
        var directory = CreateEvidenceDirectory(containerName);
        var initialPath = Path.Combine(directory, "initial.json");
        var desiredPath = Path.Combine(directory, "desired.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(initialPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"live-root-move-{{{suffix}}}"},
              "slot":{"key":"root","name":"Managed","parent":"Root/{{{containerName}}}/ParentA",
                      "position":[2,0,0],"relocationTransform":"world"},
              "children":[{"slot":{"key":"stale","name":"Stale"}}] }
            """);
        await File.WriteAllTextAsync(desiredPath, $$$"""
            { "schemaVersion":"1", "ownership":{"key":"live-root-move-{{{suffix}}}"},
              "slot":{"key":"root","name":"Managed","parent":"Root/{{{containerName}}}/ParentB",
                      "position":[2,0,0],"relocationTransform":"world"}, "children":[] }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        string? containerId = null;
        Exception? scenarioFailure = null;
        try
        {
            containerId = await client.CreateSlotAsync(new SlotCreateRequest("Root", containerName));
            await WriteEvidenceAsync(directory, "identity", new { rootId = containerId, name = containerName });
            var parentAId = await client.CreateSlotAsync(new SlotCreateRequest(containerId, "ParentA", new Vector3Value(10, 0, 0)));
            var parentBId = await client.CreateSlotAsync(new SlotCreateRequest(containerId, "ParentB", new Vector3Value(20, 0, 0)));
            var world = new WorldService(client);
            var initial = await world.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(statePath));
            await WriteEvidenceAsync(directory, "first-apply", initial);

            var applied = await world.ApplyAsync(ApplyDocument.Load(desiredPath),
                new ApplyOptions(statePath, Prune: true, ConfirmDeletes: true));

            Assert.Equal(initial.SlotId, applied.SlotId);
            Assert.Equal(1, applied.SlotsUpdated);
            Assert.Equal(1, applied.SlotsDeleted);
            Assert.Empty((await client.GetSlotAsync(parentAId, 1, false)).Children);
            var moved = Assert.Single((await client.GetSlotAsync(parentBId, 2, false)).Children);
            await WriteEvidenceAsync(directory, "observations", new { parentAId, parentBId, initial, applied, moved });
            Assert.Equal(initial.SlotId, moved.Id);
            Assert.NotNull(moved.Position);
            Assert.Equal(-8f, moved.Position!.X, 3);
            Assert.Empty(moved.Children);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, containerId, containerName, directory, scenarioFailure, statePath, "live-root-move-" + suffix);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CreatesMutatesAndCleansIsolatedSlot()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        await using var client = new ResoniteLinkClientAdapter();
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var session = await client.GetSessionInfoAsync();
        Assert.True(session.Connected);
        var name = "ResoLoop_Test_Integration_" + Guid.NewGuid().ToString("N")[..8];
        var directory = CreateEvidenceDirectory(name);
        await WriteEvidenceAsync(directory, "input", new
        {
            create = new SlotCreateRequest("Root", name, new Vector3Value(0, 1.5f, 2)),
            position = new Vector3Value(1, 2, 3), scale = new Vector3Value(0.5f, 0.5f, 0.5f),
            componentSearch = "Grabbable", member = "Scalable", initialValue = "true", updatedValue = "false"
        });
        string? slotId = null;
        Exception? scenarioFailure = null;
        try
        {
            slotId = await client.CreateSlotAsync(new SlotCreateRequest("Root", name, new Vector3Value(0, 1.5f, 2)));
            await WriteEvidenceAsync(directory, "identity", new { rootId = slotId, name });
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, Position: new Vector3Value(1, 2, 3), Scale: new Vector3Value(0.5f, 0.5f, 0.5f)));
            var created = await client.GetSlotAsync(slotId, 0, false);
            await WriteEvidenceAsync(directory, "slot-observation", created);
            Assert.Equal(name, created.Name);
            Assert.Equal(new Vector3Value(1, 2, 3), created.Position);
            var types = await client.SearchComponentTypesAsync("Grabbable", 10);
            Assert.NotEmpty(types);
            var component = await client.AddComponentAsync(slotId, types[0], new Dictionary<string, string> { ["Scalable"] = "true" });
            await client.SetComponentMemberAsync(component.Id, "Scalable", "false");
            var inspected = await client.GetComponentAsync(component.Id);
            await WriteEvidenceAsync(directory, "observations", new { session, created, types, component, inspected });
            Assert.Contains("Scalable", inspected.Members.Keys);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, slotId, name, directory, scenarioFailure);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyCheckpointsAndConvergesWithoutSecondRunWrites()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "ResoLoop_Test_Apply_" + suffix;
        var directory = CreateEvidenceDirectory(name);
        var documentPath = Path.Combine(directory, "apply.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(documentPath, $$"""
            {
              "schemaVersion": "1",
              "ownership": { "key": "live-{{suffix}}" },
              "slot": { "key": "root", "name": "{{name}}", "parent": "Root", "position": [0, 1.5, 2] },
              "components": [
                { "key": "grabbable", "type": "FrooxEngine.Grabbable", "fields": { "Scalable": true } }
              ]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? slotId = null;
        Exception? scenarioFailure = null;
        try
        {
            var first = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            slotId = first.SlotId;
            await WriteEvidenceAsync(directory, "identity", new { rootId = slotId, name });
            await WriteEvidenceAsync(directory, "first-apply", first);
            Assert.Equal(1, first.SlotsCreated);
            Assert.Equal(1, first.ComponentsAdded);
            Assert.True(File.Exists(statePath));
            Assert.Equal(slotId, await world.ResolveSlotSelectorAsync("$slot:root", statePath));
            Assert.Equal(Assert.Single((await client.GetSlotAsync(slotId, 0, false)).Components).Id,
                await world.ResolveComponentSelectorAsync("$component:grabbable", statePath));

            var second = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath, Profile: true));
            Assert.Equal(0, second.SlotsCreated);
            Assert.Equal(0, second.SlotsUpdated);
            Assert.Equal(0, second.ComponentsAdded);
            Assert.Equal(0, second.ComponentsUpdated);
            Assert.Equal(2, second.Profile!.NoOps);
            var audit = await world.AuditItemAsync(slotId, strict: true);
            await WriteEvidenceAsync(directory, "observations", new { first, second, audit });
            Assert.True(audit.Portable);
            Assert.True(audit.HasGrabbable);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, slotId, name, directory, scenarioFailure, statePath, "live-" + suffix, "root");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyTagsGeneratedRootAndPortableChildInLiveWorld()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "ResoLoop_Test_GeneratedContent_" + suffix;
        var directory = CreateEvidenceDirectory(name);
        var documentPath = Path.Combine(directory, "apply.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(documentPath, $$$"""
            {
              "schemaVersion":"1", "ownership":{"key":"live-generated-content-{{{suffix}}}"},
              "slot":{"key":"root","name":"{{{name}}}","parent":"Root"},
              "children":[
                {
                  "slot":{"key":"item","name":"PortableItem"},
                  "components":[{"key":"grabbable","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}]
                },
                { "slot":{"key":"plain","name":"PlainChild"}, "components":[] }
              ]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var source = GeneratedContentMetadata.SourceForVersion("integration-test");
        var world = new WorldService(client, source);
        string? rootId = null;
        Exception? scenarioFailure = null;
        try
        {
            var validation = await world.ValidateApplyAsync(ApplyDocument.Load(documentPath), true);
            Assert.True(validation.Valid);
            var first = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath));
            rootId = first.SlotId;
            await WriteEvidenceAsync(directory, "identity", new { rootId, name });
            await WriteEvidenceAsync(directory, "first-apply", first);

            var root = await client.GetSlotAsync(rootId, 1, true);
            await WriteEvidenceAsync(directory, "root-observation", root);
            await AssertMarker(root);
            await AssertMarker(Assert.Single(root.Children, child => child.Name == "PortableItem"));
            Assert.DoesNotContain(Assert.Single(root.Children, child => child.Name == "PlainChild").Components,
                component => SimpleType(component.Type) == "AI_GeneratedContent");

            var second = await world.ApplyAsync(ApplyDocument.Load(documentPath), new ApplyOptions(statePath));
            await WriteEvidenceAsync(directory, "second-apply", second);
            Assert.Equal(0, second.SlotsCreated);
            Assert.Equal(0, second.SlotsUpdated);
            Assert.Equal(0, second.ComponentsAdded);
            Assert.Equal(0, second.ComponentsUpdated);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, rootId, name, directory, scenarioFailure, statePath, "live-generated-content-" + suffix, "root");
        }

        async Task AssertMarker(SlotInfo slot)
        {
            var summary = Assert.Single(slot.Components, component =>
                SimpleType(component.Type) == "AI_GeneratedContent");
            var component = await client.GetComponentAsync(summary.Id);
            await WriteEvidenceAsync(directory, slot.Id == rootId ? "root-marker" : "portable-marker", component);
            var member = component.Members[GeneratedContentMetadata.SourceMember];
            Assert.Equal(source, member.Value!.GetValue<string>());
        }

        static string SimpleType(string type)
        {
            var bracket = type.IndexOf(']');
            if (bracket >= 0) type = type[(bracket + 1)..];
            return type.Split('.').Last();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ApplyPreservesTransformMigratesKeysAndPrunesParentAsOneOperation()
    {
        if (Environment.GetEnvironmentVariable("RESOLOOP_RUN_INTEGRATION") != "1") return;
        var url = Environment.GetEnvironmentVariable("RESONITE_LINK_URL")
                  ?? throw new InvalidOperationException("RESONITE_LINK_URL is required.");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "ResoLoop_Test_Policy_" + suffix;
        var directory = CreateEvidenceDirectory(name);
        var initialPath = Path.Combine(directory, "initial.json");
        var desiredPath = Path.Combine(directory, "desired.json");
        var statePath = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(initialPath, $$$"""
            {
              "schemaVersion":"1", "ownership":{"key":"live-policy-{{{suffix}}}"},
              "slot":{"key":"old-root","name":"{{{name}}}","parent":"Root","position":[0,1,2]},
              "components":[{"key":"old-grabbable","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}],
              "children":[{
                "slot":{"key":"obsolete-parent","name":"Obsolete"},
                "components":[{"key":"obsolete-component","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}]
              }]
            }
            """);
        await File.WriteAllTextAsync(desiredPath, $$$"""
            {
              "schemaVersion":"1", "ownership":{"key":"live-policy-{{{suffix}}}"},
              "slot":{"key":"new-root","migrateFrom":"old-root","name":"{{{name}}}","parent":"Root",
                      "position":[0,1,2],"preserveWorldTransform":true},
              "components":[{"key":"new-grabbable","migrateFrom":"old-grabbable","type":"FrooxEngine.Grabbable","fields":{"Scalable":true}}],
              "children":[]
            }
            """);

        await using var client = new ResoniteLinkClientAdapter(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(new Uri(url), TimeSpan.FromSeconds(30));
        var world = new WorldService(client);
        string? slotId = null;
        Exception? scenarioFailure = null;
        try
        {
            var initial = await world.ApplyAsync(ApplyDocument.Load(initialPath), new ApplyOptions(statePath));
            slotId = initial.SlotId;
            await WriteEvidenceAsync(directory, "identity", new { rootId = slotId, name });
            await WriteEvidenceAsync(directory, "first-apply", initial);
            await client.UpdateSlotAsync(new SlotUpdateRequest(slotId, Position: new Vector3Value(5, 6, 7)));

            var plan = await world.PlanApplyAsync(ApplyDocument.Load(desiredPath), new ApplyOptions(statePath));
            Assert.DoesNotContain(plan.Operations, operation => operation.Action == "create");
            Assert.Single(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "slot");
            Assert.DoesNotContain(plan.Operations, operation => operation.Action == "delete" && operation.Kind == "component");

            var applied = await world.ApplyAsync(ApplyDocument.Load(desiredPath),
                new ApplyOptions(statePath, Prune: true, ConfirmDeletes: true));
            var inspected = await client.GetSlotAsync(slotId, 1, false);
            await WriteEvidenceAsync(directory, "observations", new { initial, plan, applied, inspected });
            Assert.Equal(new Vector3Value(5, 6, 7), inspected.Position);
            Assert.Empty(inspected.Children);
            Assert.Equal(1, applied.SlotsDeleted);
            Assert.Equal(0, applied.ComponentsDeleted);
            Assert.Equal(slotId, applied.SlotId);
        }
        catch (Exception error)
        {
            scenarioFailure = error;
            throw;
        }
        finally
        {
            await CleanSandboxAsync(client, slotId, name, directory, scenarioFailure, statePath, "live-policy-" + suffix, "old-root", "new-root");
        }
    }

    private static string CreateEvidenceDirectory(string name)
    {
        var artifacts = Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS");
        var directory = Path.Combine(string.IsNullOrWhiteSpace(artifacts) ? Path.GetTempPath() : Path.GetFullPath(artifacts), name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Task WriteEvidenceAsync(string directory, string label, object value) =>
        File.WriteAllTextAsync(Path.Combine(directory, label + ".json"),
            System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    private static async Task CleanSandboxAsync(IResoniteClient client, string? rootId, string name, string directory,
        Exception? scenarioFailure, string? statePath = null, string? ownershipKey = null, params string[] rootKeys)
    {
        // Cleanup has its own deadline and never inherits a cancelled scenario token.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SlotInfo? observed = null;
        RLoopException? absenceEvidence = null;
        var deleted = false;
        var absent = false;
        var failures = new List<Exception>();
        var hasState = statePath is not null && File.Exists(statePath);
        var statePreserved = !hasState;
        if (hasState) PreserveState();
        try
        {
            // A known returned ID does not depend on checkpoint or artifact I/O for cleanup.
            if (rootId is null && hasState)
            {
                var state = ApplyStateStore.Load(statePath!, ownershipKey!);
                var recordedIds = rootKeys.Where(state.Slots.ContainsKey).Select(key => state.Slots[key].Id)
                    .Concat(state.Pending.Where(p => rootKeys.Contains(p.Key) && p.Kind == "createSlot" &&
                        p.ParentId == "Root" && p.SlotValues?.Name == name && p.Id is not null).Select(p => p.Id!))
                    .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
                if (recordedIds.Length != 1)
                    throw new InvalidOperationException("Cleanup requires one exact sandbox ID from this test's own checkpoint/pending evidence.");
                rootId = recordedIds[0];
            }
            await RecordAsync("cleanup-identity", new { rootId, name, statePath });
            if (string.IsNullOrWhiteSpace(rootId) || rootId == "Root" || !name.StartsWith("ResoLoop_Test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup without an exact non-Root sandbox ID.");
            observed = await client.GetSlotAsync(rootId, 0, false, cleanup.Token);
            await RecordAsync("cleanup-before", observed);
            if (observed.Id != rootId || observed.Name != name || observed.ParentId != "Root")
                throw new InvalidOperationException("Refusing cleanup: exact ID, created name and parent Root were not verified.");
            await client.DeleteSlotAsync(rootId, cleanup.Token);
            deleted = true;
            var missing = await Assert.ThrowsAsync<RLoopException>(() => client.GetSlotAsync(rootId, 0, false, cleanup.Token));
            Assert.Equal("SLOT_NOT_FOUND", missing.Code);
            absenceEvidence = missing;
            absent = true;
            await RecordAsync("cleanup-absence", new { rootId, name, missing.Code, missing.Context });
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (absent && hasState)
        {
            if (!statePreserved) PreserveState();
            // Preserve the journal before removing an active pending pointer, after verified deletion.
            if (statePreserved)
            {
                try { File.Delete(statePath!); }
                catch (Exception error) { failures.Add(error); }
            }
        }
        await RecordAsync("cleanup", new
        {
            rootId, name, observed, deleted, absent, statePreserved,
            absenceCode = absenceEvidence?.Code, absenceContext = absenceEvidence?.Context,
            activeStateRemoved = statePath is not null && !File.Exists(statePath),
            scenarioError = scenarioFailure?.ToString(), errors = failures.Select(error => error.ToString()).ToArray()
        });
        if (failures.Count == 0 && scenarioFailure is null &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RESOLOOP_UIX_ARTIFACTS")))
        {
            // Delete only exact files authored by these tests; never traverse or recursively remove a directory.
            try
            {
                foreach (var file in new[] { "input.json", "initial.json", "desired.json", "apply.json", "state.json.lock",
                    "identity.json", "reparent-observations.json", "observations.json", "first-apply.json", "second-apply.json",
                    "slot-observation.json", "root-observation.json", "root-marker.json", "portable-marker.json",
                    "inactive-checkpoint.evidence.txt", "cleanup-identity.json", "cleanup-before.json", "cleanup-absence.json", "cleanup.json" })
                    File.Delete(Path.Combine(directory, file));
                Directory.Delete(directory, false);
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0)
        {
            await RecordAsync("cleanup-failure", new
            {
                rootId, name, observed, deleted, absent, statePreserved,
                absenceCode = absenceEvidence?.Code, absenceContext = absenceEvidence?.Context,
                scenarioError = scenarioFailure?.ToString(), errors = failures.Select(error => error.ToString()).ToArray()
            });
            if (scenarioFailure is not null) failures.Insert(0, scenarioFailure);
            throw new AggregateException("Integration scenario/cleanup/evidence failures; exact sandbox absence is recorded separately.", failures);
        }

        void PreserveState()
        {
            try
            {
                // This byte-for-byte backup is evidence only, never an active checkpoint path.
                File.Copy(statePath!, Path.Combine(directory, "inactive-checkpoint.evidence.txt"));
                statePreserved = true;
            }
            catch (Exception error) { failures.Add(error); }
        }

        async Task RecordAsync(string label, object value)
        {
            try { await WriteEvidenceAsync(directory, label, value); }
            catch (Exception error) { failures.Add(error); }
        }
    }
}
